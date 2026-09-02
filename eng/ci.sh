#!/usr/bin/env bash
# CI minimums from ARCHITECTURE.md section 22.
# Usage: eng/ci.sh [--with-integration] [--live-eval]
set -euo pipefail

cd "$(dirname "$0")/.."

export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export CI=true

SOLUTION="SreAiAgent.slnx"
WITH_INTEGRATION=false
LIVE_EVAL=false
for argument in "$@"; do
  case "$argument" in
    --with-integration) WITH_INTEGRATION=true ;;
    --live-eval) LIVE_EVAL=true ;;
    *) echo "Unknown argument: $argument" >&2; exit 2 ;;
  esac
done

step() { printf '\n\033[1;36m==> %s\033[0m\n' "$1"; }

step "restore (locked mode)"
dotnet restore "$SOLUTION" --locked-mode

step "format verification"
dotnet format "$SOLUTION" --verify-no-changes --no-restore

step "build (warnings are errors)"
dotnet build "$SOLUTION" --no-restore -warnaserror

step "unit, contract, architecture and security tests"
dotnet test "$SOLUTION" --no-build -- \
  --filter-not-trait "Category=Integration" \
  --filter-not-trait "Category=ScriptedEval" \
  --filter-not-trait "Category=LiveEval"

step "deterministic scripted eval smoke"
dotnet test "tests/SreAiAgent.Evals/SreAiAgent.Evals.csproj" --no-build -- \
  --filter-trait "Category=ScriptedEval"

if [ "$WITH_INTEGRATION" = true ]; then
  step "integration tests (requires a Docker daemon)"
  # Each project is named rather than filtering the solution by the trait. The
  # test platform reports "zero tests ran" as a failure, so a trait filter across
  # the solution fails in every project that does not carry it.
  for project in SreAiAgent.IntegrationTests SreAiAgent.ContractTests; do
    dotnet test "tests/$project/$project.csproj" --no-build -- --filter-trait "Category=Integration"
  done
fi

if [ "$LIVE_EVAL" = true ]; then
  step "explicit live eval matrix"
  # The selected test fails when the exact candidate, compatible baseline or
  # candidate credential is missing. A requested live run is never skipped.
  dotnet test "tests/SreAiAgent.Evals/SreAiAgent.Evals.csproj" --no-build -- \
    --filter-trait "Category=LiveEval"
fi

step "vulnerable package audit"
if dotnet list "$SOLUTION" package --vulnerable --include-transitive 2>&1 | tee /tmp/sre-vuln.log | grep -q "has the following vulnerable packages"; then
  cat /tmp/sre-vuln.log
  echo "Vulnerable packages found." >&2
  exit 1
fi

step "all checks passed"
