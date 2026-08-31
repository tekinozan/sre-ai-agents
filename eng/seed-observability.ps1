#!/usr/bin/env pwsh
# Seeds the observability overlay with the incident the fakes fabricate.
#
# The point is comparability. The deterministic fakes produce one coherent story
# — a connection pool filling, clients queueing behind it, errors following —
# and the vertical slice is verified against that story. Reproducing the same
# shape in a real Prometheus and a real Loki means the report a real run
# produces can be held next to the report the fake run produces, and anything
# that differs is the adapter's doing rather than the data's.
#
# Written for Windows PowerShell 5.1 as well as pwsh 7, so no pipeline chain
# operators, ternaries or null-coalescing.
#
# Usage, after the overlay is up:
#   powershell -ExecutionPolicy Bypass -File eng/seed-observability.ps1

[CmdletBinding()]
param(
    [string]$Pushgateway = 'http://localhost:9091',
    [string]$Loki = 'http://localhost:3100',
    [string]$Service = 'checkout',
    [string]$Environment = 'production',
    # Twelve steps at two seconds is about the shortest run that leaves a
    # visible slope rather than a single point, which is what an agent needs to
    # say the pool was filling rather than merely full.
    [int]$Steps = 12,
    [int]$IntervalSeconds = 2
)

$ErrorActionPreference = 'Stop'

# Only gauges. The scripted model provider asks for exactly one metric —
# db_connection_pool_waiting — and its expression is sum(...) with no rate(), so
# counters and histogram buckets would be work with nothing exercising them. The
# neighbouring pool gauges are pushed anyway so the story reads whole if someone
# queries them by hand.
function Get-Sample {
    param([int]$Step, [int]$Total)

    # Flat, then climbing over the last third: what a saturating resource looks
    # like, and the same shape FakeSignals.Ramp produces.
    $rampStart = [Math]::Floor($Total * 0.66)
    if ($Step -lt $rampStart) { $progress = 0.0 }
    else { $progress = ($Step - $rampStart) / [Math]::Max(1, $Total - $rampStart) }

    return @{
        db_connection_pool_active          = [Math]::Round(12 + (28 * $progress), 0)
        db_connection_pool_idle            = [Math]::Round(28 - (28 * $progress), 0)
        db_connection_pool_waiting         = [Math]::Round(0 + (27 * $progress), 0)
        container_memory_working_set_bytes = 412000000
    }
}

function Push-Metrics {
    param([hashtable]$Values)

    $labels = "service=`"$Service`",environment=`"$Environment`""
    $lines = New-Object System.Collections.Generic.List[string]

    foreach ($name in $Values.Keys) {
        $lines.Add("# TYPE $name gauge")
        $lines.Add("$name{$labels} $($Values[$name])")
    }

    # Pushgateway requires a trailing newline and rejects a body without one.
    $body = ($lines -join "`n") + "`n"

    Invoke-RestMethod -Method Post -Uri "$Pushgateway/metrics/job/sre-seed" `
        -Body $body -ContentType 'text/plain' | Out-Null
}

function Push-Logs {
    # Loki orders entries within a stream, so they go oldest first here. The
    # adapter is what sorts them newest-first on the way out; pushing them in
    # that order would have Loki reject the stream.
    $now = [DateTimeOffset]::UtcNow
    $entries = @(
        @{ Offset = -180; Message = 'Npgsql.NpgsqlException: The connection pool has been exhausted, either raise MaxPoolSize (currently 40) or Timeout (currently 15 seconds)' },
        @{ Offset = -120; Message = 'Timeout expired while awaiting a connection from the pool for checkout.orders' },
        @{ Offset =  -60; Message = 'Npgsql.NpgsqlException: Exception while reading from stream' }
    )

    $values = @()
    foreach ($entry in $entries) {
        $nanoseconds = ($now.AddSeconds($entry.Offset).ToUnixTimeMilliseconds() * 1000000)
        $values += , @("$nanoseconds", $entry.Message)
    }

    $payload = @{
        streams = @(
            @{
                stream = @{
                    service     = $Service
                    environment = $Environment
                    level       = 'error'
                }
                values = $values
            }
        )
    }

    $json = $payload | ConvertTo-Json -Depth 6 -Compress

    Invoke-RestMethod -Method Post -Uri "$Loki/loki/api/v1/push" `
        -Body $json -ContentType 'application/json' | Out-Null
}

Write-Host "==> pushing $Steps steps of pool metrics to $Pushgateway" -ForegroundColor Cyan

for ($step = 0; $step -lt $Steps; $step++) {
    $sample = Get-Sample -Step $step -Total $Steps
    Push-Metrics -Values $sample

    Write-Host ("    step {0,2}/{1}  active={2,3}  idle={3,3}  waiting={4,3}" -f `
        ($step + 1), $Steps, $sample.db_connection_pool_active, `
        $sample.db_connection_pool_idle, $sample.db_connection_pool_waiting)

    # Each push replaces the value in the pushgateway; the history that rate()
    # and a range query read is built by Prometheus scraping between pushes.
    if ($step -lt ($Steps - 1)) { Start-Sleep -Seconds $IntervalSeconds }
}

# The last push has not been scraped yet, and a caller that sends an alert
# straight after this would query Prometheus for a value it has not collected.
# Waiting one interval is the difference between the seed being true when it says
# it is and being true a moment later.
Write-Host "==> waiting one scrape interval for the last push to be collected"
Start-Sleep -Seconds ($IntervalSeconds + 2)

Write-Host "==> pushing error logs to $Loki" -ForegroundColor Cyan
Push-Logs

Write-Host ''
Write-Host 'Seeded. Now send an alert:' -ForegroundColor Green
Write-Host @'
  curl -X POST http://localhost:8080/v1/alerts/alertmanager -H "Content-Type: application/json" -d "{\"status\":\"firing\",\"fingerprint\":\"seeded-1\",\"labels\":{\"alertname\":\"HighCheckoutLatency\",\"service\":\"checkout\",\"environment\":\"production\",\"severity\":\"critical\",\"tenant\":\"local\"},\"annotations\":{\"summary\":\"Checkout p99 latency above 2s\"}}"
'@
Write-Host ''
Write-Host 'Then confirm the evidence came from the real backend:' -ForegroundColor Green
Write-Host '  SELECT provider, source_type, count(*) FROM evidence GROUP BY 1,2;'
Write-Host '  -- expect provider=prometheus for the Metric rows'
Write-Host ''
Write-Host 'Two things this run will not show, both expected:' -ForegroundColor Yellow
Write-Host @'
  1. The tool summary reads the pool as "stayed flat". Pushgateway strips
     timestamps by design, so the ramp occupies only this script's wall-clock
     span while the tool asks about thirty minutes. For a slope visible at that
     resolution: -Steps 30 -IntervalSeconds 20, which takes ten minutes.

  2. No loki evidence. The scripted provider only asks for logs.search when the
     agent has no metrics tool, and both shipped experts have one. The Loki
     adapter is covered by SreAiAgent.ContractTests instead; to confirm the seed
     landed, query it the way the adapter does:

     curl -sG http://localhost:3100/loki/api/v1/query_range \
       --data-urlencode 'query={service="checkout",environment="production",level=~"error|fatal"}' \
       --data-urlencode "start=$(([DateTimeOffset]::UtcNow.AddMinutes(-30)).ToUnixTimeMilliseconds() * 1000000)" \
       --data-urlencode "end=$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() * 1000000)"
'@
