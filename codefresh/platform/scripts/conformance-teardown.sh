#!/usr/bin/env bash
# Teardown of platform-env/conformance and platform-env/conformance-destructive (ADR-IR34 "Cost
# control"): force-sleeps every app cluster the run woke, through runbook env-sleep of
# platform-infrastructure (Sleep.Force=true), in parallel. "Woke" = not Running when the run
# started (<results>/power-before.txt from conformance-run.sh); without that record both tiers are
# slept. env-sleep's busy rule keeps a cluster up while a deployment or runbook run is queued or
# executing, so the teardown never stops a cluster under a task.
# CONFORMANCE_SLEEP_AFTER=false keeps the clusters up for debugging. Never fails the build.
#
# Usage: conformance-teardown.sh <results folder>
# Environment: OCTOPUS_URL, OCTOPUS_SPACE_ID, OCTOPUS_API_KEY (platform-octopus); PLATFORM_RUN_ID.
set -uo pipefail

results="${1:-}"
if [ "${CONFORMANCE_SLEEP_AFTER:-true}" = "false" ]; then
  printf 'conformance-teardown.sh: CONFORMANCE_SLEEP_AFTER=false; the clusters stay up\n' >&2
  exit 0
fi
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
before="$results/power-before.txt"

pids=()
names=()
for tier in nonprod prod; do
  state=""
  if [ -n "$results" ] && [ -f "$before" ]; then
    state="$(sed -n "s/^${tier}=//p" "$before" | head -n 1)"
  fi
  case "$state" in
    Running/*)
      printf 'conformance-teardown.sh: %s was Running before the run; left up\n' "$tier" >&2
      continue
      ;;
    unconfigured)
      printf 'conformance-teardown.sh: %s has no cluster settings; skipped\n' "$tier" >&2
      continue
      ;;
  esac
  bash "$script_dir/octopus-runbook.sh" --project platform-infrastructure --runbook env-sleep \
    --environment "infra-${tier}" --prompt Sleep.Force=true \
    --notes "conformance:${PLATFORM_RUN_ID:-unknown} teardown" --wait 30 >/dev/null &
  pids+=("$!")
  names+=("infra-${tier}")
done

for index in "${!pids[@]}"; do
  if wait "${pids[$index]}"; then
    printf 'conformance-teardown.sh: env-sleep finished in %s\n' "${names[$index]}" >&2
  else
    printf 'conformance-teardown.sh: WARN env-sleep did not finish successfully in %s; the hourly env-sleep will retry\n' "${names[$index]}" >&2
  fi
done
exit 0
