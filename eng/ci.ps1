#!/usr/bin/env pwsh
# CI minimums from ARCHITECTURE.md section 22.
#
# The twin of eng/ci.sh: same steps, same order, same failure conditions. It
# exists because the development machine is Windows and an agent that cannot run
# the gate does not run it. Written for Windows PowerShell 5.1 as well as pwsh 7,
# so no pipeline chain operators, ternaries or null-coalescing.
#
# Usage: pwsh eng/ci.ps1 [-WithIntegration]

[CmdletBinding()]
param([switch]$WithIntegration)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:CI = 'true'

$solution = 'SreAiAgent.slnx'

function Write-Step {
    param([string]$Name)
    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
}

function Invoke-Step {
    param([string]$Name, [scriptblock]$Command)

    Write-Step $Name
    & $Command

    # dotnet is a native executable, so a non-zero exit does not raise and does
    # not stop the script. Unchecked, this reports success on a failed build.
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Step failed: $Name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Invoke-Step 'restore (locked mode)' {
    dotnet restore $solution --locked-mode
}

Invoke-Step 'format verification' {
    dotnet format $solution --verify-no-changes --no-restore
}

Invoke-Step 'build (warnings are errors)' {
    dotnet build $solution --no-restore -warnaserror
}

Invoke-Step 'unit, contract, architecture and security tests' {
    dotnet test $solution --no-build -- --filter-not-trait "Category=Integration"
}

if ($WithIntegration) {
    # Each project is named rather than filtering the solution by the trait. The
    # test platform reports "zero tests ran" as a failure, so a trait filter
    # across the solution fails in every project that does not carry it.
    foreach ($project in @('SreAiAgent.IntegrationTests', 'SreAiAgent.ContractTests')) {
        Invoke-Step "integration tests: $project" {
            dotnet test "tests/$project/$project.csproj" --no-build -- --filter-trait "Category=Integration"
        }
    }
}

Write-Step 'vulnerable package audit'
$audit = dotnet list $solution package --vulnerable --include-transitive
$audit | Write-Host
if ($audit -match 'has the following vulnerable packages') {
    Write-Host 'Vulnerable packages found.' -ForegroundColor Red
    exit 1
}

Write-Step 'all checks passed'
