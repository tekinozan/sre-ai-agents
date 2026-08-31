#!/usr/bin/env bash
# Seeds the observability overlay with the incident the fakes fabricate.
# The twin of eng/seed-observability.ps1; see that file for the reasoning.
#
# Usage, after the overlay is up:
#   bash eng/seed-observability.sh
set -euo pipefail

PUSHGATEWAY="${PUSHGATEWAY:-http://localhost:9091}"
LOKI="${LOKI:-http://localhost:3100}"
SERVICE="${SERVICE:-checkout}"
ENVIRONMENT="${ENVIRONMENT:-production}"
STEPS="${STEPS:-12}"
INTERVAL="${INTERVAL:-2}"

LABELS="service=\"${SERVICE}\",environment=\"${ENVIRONMENT}\""

printf '\n\033[1;36m==> pushing %s steps of pool metrics to %s\033[0m\n' "$STEPS" "$PUSHGATEWAY"

ramp_start=$(( STEPS * 66 / 100 ))

for (( step = 0; step < STEPS; step++ )); do
  if (( step < ramp_start )); then
    progress=0
  else
    progress=$(( (step - ramp_start) * 100 / (STEPS - ramp_start) ))
  fi

  active=$(( 12 + (28 * progress / 100) ))
  idle=$(( 28 - (28 * progress / 100) ))
  waiting=$(( 27 * progress / 100 ))

  # Only gauges. The scripted provider asks for db_connection_pool_waiting, whose
  # expression is sum(...) with no rate(), so counters and histogram buckets
  # would be work with nothing exercising them.
  curl -sf --data-binary @- "${PUSHGATEWAY}/metrics/job/sre-seed" <<EOF > /dev/null
# TYPE db_connection_pool_active gauge
db_connection_pool_active{${LABELS}} ${active}
# TYPE db_connection_pool_idle gauge
db_connection_pool_idle{${LABELS}} ${idle}
# TYPE db_connection_pool_waiting gauge
db_connection_pool_waiting{${LABELS}} ${waiting}
# TYPE container_memory_working_set_bytes gauge
container_memory_working_set_bytes{${LABELS}} 412000000
EOF

  printf '    step %2d/%s  active=%3d  idle=%3d  waiting=%3d\n' \
    "$(( step + 1 ))" "$STEPS" "$active" "$idle" "$waiting"

  # Each push replaces the value; the history a range query reads is built by
  # Prometheus scraping between pushes.
  if (( step < STEPS - 1 )); then sleep "$INTERVAL"; fi
done

# The last push has not been scraped yet, and a caller that sends an alert
# straight after this would query Prometheus for a value it has not collected.
printf '\n\033[1;36m==> waiting one scrape interval for the last push to be collected\033[0m\n'
sleep $(( INTERVAL + 2 ))

printf '\n\033[1;36m==> pushing error logs to %s\033[0m\n' "$LOKI"

now_ns=$(( $(date +%s) * 1000000000 ))

# Oldest first: Loki orders entries within a stream and rejects one that goes
# backwards. The adapter is what sorts them newest-first on the way out.
curl -sf -X POST "${LOKI}/loki/api/v1/push" \
  -H 'Content-Type: application/json' \
  -d @- > /dev/null <<EOF
{"streams":[{"stream":{"service":"${SERVICE}","environment":"${ENVIRONMENT}","level":"error"},
"values":[
["$(( now_ns - 180000000000 ))","Npgsql.NpgsqlException: The connection pool has been exhausted, either raise MaxPoolSize (currently 40) or Timeout (currently 15 seconds)"],
["$(( now_ns - 120000000000 ))","Timeout expired while awaiting a connection from the pool for checkout.orders"],
["$(( now_ns - 60000000000 ))","Npgsql.NpgsqlException: Exception while reading from stream"]]}]}
EOF

printf '\n\033[1;32mSeeded.\033[0m Now send an alert:\n'
cat <<'EOF'
  curl -X POST http://localhost:8080/v1/alerts/alertmanager \
    -H 'Content-Type: application/json' \
    -d '{"status":"firing","fingerprint":"seeded-1",
         "labels":{"alertname":"HighCheckoutLatency","service":"checkout",
                   "environment":"production","severity":"critical","tenant":"local"},
         "annotations":{"summary":"Checkout p99 latency above 2s"}}'

Then confirm the evidence came from the real backend:
  SELECT provider, source_type, count(*) FROM evidence GROUP BY 1,2;
  -- expect provider=prometheus for the Metric rows

Two things this run will not show, both expected:

  1. The tool summary reads the pool as "stayed flat". Pushgateway strips
     timestamps by design, so the ramp occupies only this script's wall-clock
     span while the tool asks about thirty minutes. For a slope visible at that
     resolution: STEPS=30 INTERVAL=20, which takes ten minutes.

  2. No loki evidence. The scripted provider only asks for logs.search when the
     agent has no metrics tool, and both shipped experts have one. The Loki
     adapter is covered by SreAiAgent.ContractTests instead.
EOF
