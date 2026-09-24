#!/usr/bin/env bash
# Runs the .NET conformance suite and summarises it (ADR-IR34 "Test harness"): dotnet test of
# tests/Platform.Conformance.sln with TEST_FILTER and the TRX logger (no JUnit), then
# Platform.Conformance.Report for summary.md and summary.json, printed to the log and recorded
# as Codefresh build annotations (counts and failed capability IDs; best effort).
#
# Before the tests it records the power state of both app clusters in <results>/power-before.txt;
# conformance-teardown.sh force-sleeps the ones that were not Running (the clusters the run woke).
#
# Usage: conformance-run.sh <results folder>     (run from the environment repo root)
# Environment: TEST_FILTER; PLATFORM_RUN_ID (minted as by conformance-arm.sh when absent, and
# exported to the later steps); the harness's secrets (OCTOPUS_API_KEY, AZURE_CLIENT_ID,
# AZURE_CLIENT_SECRET, AZURE_TENANT_ID, GITHUB_TOKEN, CODEFRESH_API_KEY); CF_API_KEY.
# Exit code: that of dotnet test (a failed test fails the build).
set -uo pipefail

results="${1:?usage: conformance-run.sh <results folder>}"
filter="${TEST_FILTER:?TEST_FILTER is not set}"
mkdir -p "$results"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ -z "${PLATFORM_RUN_ID:-}" ]; then
  build="${CF_BUILD_ID:-local}"
  PLATFORM_RUN_ID="$(printf 'r%s-%s' "$(date -u +%Y%m%dt%H%M)" "${build: -8}" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9.-' '-' | cut -c1-40)"
fi
export PLATFORM_RUN_ID
if command -v cf_export >/dev/null 2>&1; then
  cf_export PLATFORM_RUN_ID="$PLATFORM_RUN_ID"
fi
printf 'conformance-run.sh: run %s, filter %s\n' "$PLATFORM_RUN_ID" "$filter" >&2

# shellcheck source=codefresh/platform/scripts/aks-power.sh
. "$script_dir/aks-power.sh"
power_dir="$(mktemp -d)"
: >"$results/power-before.txt"
if aks_power_init "$power_dir"; then
  for tier in nonprod prod; do
    printf '%s=%s\n' "$tier" "$(aks_power_state "$tier")" >>"$results/power-before.txt"
  done
fi
rm -rf -- "$power_dir"
export PLATFORM_ARTIFACTS_DIR="${PLATFORM_ARTIFACTS_DIR:-$results/artifacts}"
# Q49: the build's own Codefresh key serves the harness unless the context supplies one.
export CODEFRESH_API_KEY="${CODEFRESH_API_KEY:-${CF_API_KEY:-}}"

dotnet build tests/Platform.Conformance.sln --configuration Release --nologo || exit 1
status=0
# The console logger at normal verbosity streams each test's outcome to the build log while the
# suite runs (a live run takes hours; the TRX appears only at the end). Codefresh terminates a
# build whose log stays silent for 45 minutes ("inactivity", first live run 2026-09-24), and one
# live test may wait longer than that, so a heartbeat line every 5 minutes keeps the build active.
(
  while sleep 300; do
    echo "conformance-run: dotnet test still running at $(date -u +%H:%M:%SZ)"
  done
) &
heartbeat=$!
dotnet test tests/Platform.Conformance.sln --configuration Release --no-build \
  --filter "$filter" --logger "trx;LogFilePrefix=conformance" --logger "console;verbosity=normal" \
  --results-directory "$results" || status=$?
kill "$heartbeat" 2>/dev/null || true
wait "$heartbeat" 2>/dev/null || true

if ls "$results"/*.trx >/dev/null 2>&1; then
  dotnet run --project tests/Platform.Conformance.Report --configuration Release --no-build -- report \
    --trx "$results" \
    --assembly tests/Platform.Conformance.Tests/bin/Release/net10.0/Platform.Conformance.Tests.dll \
    --assembly tests/Platform.Conformance.Offline/bin/Release/net10.0/Platform.Conformance.Offline.dll \
    --repo-root . --out "$results" --title "${CF_PIPELINE_NAME:-conformance} ${PLATFORM_RUN_ID:-}" || true
  cat "$results/summary.md" 2>/dev/null || true
fi

# Build annotations: per-verdict counts and failed capability IDs (never fatal).
key="${CF_API_KEY:-${CODEFRESH_API_KEY:-}}"
if [ -n "$key" ] && [ -n "${CF_BUILD_ID:-}" ] && [ -f "$results/summary.json" ]; then
  headers="$(mktemp)"
  (umask 077 && printf 'Authorization: %s\nContent-Type: application/json\n' "$key" >"$headers")
  jq -c --arg build "$CF_BUILD_ID" --arg run "${PLATFORM_RUN_ID:-}" '
    [ {key: "conformance-run-id", value: $run},
      {key: "conformance-passed", value: (.totals.passed | tostring)},
      {key: "conformance-failed", value: (.totals.failed | tostring)},
      {key: "conformance-inconclusive", value: (.totals.inconclusive | tostring)},
      {key: "conformance-failed-capabilities", value: ([.capabilities[] | select(.status == "fail") | .id] | join(" "))} ]
    | .[] | {entityType: "build", entityId: $build} + .' "$results/summary.json" |
    while IFS= read -r annotation; do
      curl -fsS --max-time 30 -H @"$headers" -X POST --data-binary "$annotation" \
        "${CF_URL:-https://g.codefresh.io}/api/annotations" >/dev/null ||
        printf 'conformance-run.sh: WARN annotation not recorded [VERIFY the annotations route]\n' >&2
    done
  rm -f "$headers"
fi
exit "$status"
