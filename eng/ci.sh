#!/usr/bin/env bash
# CI minimums from ARCHITECTURE.md section 22.
# Usage: eng/ci.sh [--with-integration]
set -euo pipefail

cd "$(dirname "$0")/.."

export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export CI=true

SOLUTION="SreAiAgent.slnx"
WITH_INTEGRATION="${1:-}"

step() { printf '\n\033[1;36m==> %s\033[0m\n' "$1"; }

step "restore (locked mode)"
dotnet restore "$SOLUTION" --locked-mode

step "format verification"
dotnet format "$SOLUTION" --verify-no-changes --no-restore

step "build (warnings are errors)"
dotnet build "$SOLUTION" --no-restore -warnaserror

step "unit, contract, architecture and security tests"
dotnet test "$SOLUTION" --no-build -- --filter-not-trait "Category=Integration"

if [ "$WITH_INTEGRATION" = "--with-integration" ]; then
  step "integration tests (requires a Docker daemon)"
  # Scoped to the project that owns them rather than run across the solution.
  # The test platform treats "zero tests ran" as a failure, so filtering the whole
  # solution by a trait only one project carries fails in the other seven.
  dotnet test tests/SreAiAgent.IntegrationTests/SreAiAgent.IntegrationTests.csproj     --no-build -- --filter-trait "Category=Integration"
fi

step "vulnerable package audit"
if dotnet list "$SOLUTION" package --vulnerable --include-transitive 2>&1 | tee /tmp/sre-vuln.log | grep -q "has the following vulnerable packages"; then
  cat /tmp/sre-vuln.log
  echo "Vulnerable packages found." >&2
  exit 1
fi

step "all checks passed"
