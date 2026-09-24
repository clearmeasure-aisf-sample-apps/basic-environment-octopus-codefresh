#!/usr/bin/env bash
# platform-env/conformance-arm (ADR-IR34 "Test harness", Scheduling):
#   1. mints the run ID (PLATFORM_RUN_ID) unless one is given;
#   2. force-sleeps both app clusters through runbook env-sleep (Sleep.Force=true) in infra-nonprod
#      and infra-prod, waits for both tasks and then until both clusters report powerState Stopped
#      (env-sleep stops without waiting), then CONFORMANCE_STOP_GRACE_MINUTES (default 15;
#      Microsoft advises 15-30 minutes between a stop and a start, E50);
#   3. pushes the run's sandbox commits to <sandbox-app-repo>:
#        conformance/<run id>/failing-test   adds toggles/failing-test   -> sandbox/ci fails (CAP-CF-004)
#        conformance/<run id>/green          adds conformance/run-id      -> sandbox/ci passes (CAP-CF-004)
#        main                                conformance/last-run (canary) -> sandbox/release (CAP-CF-006..009)
#      and deletes the branches of older runs;
#   4. queues a second sandbox/release build of the release commit (the rerun of CAP-CF-008), then
#      platform-env/conformance with the run ID, the three commit SHAs and the rerun's build ID.
#      With one build at a time (BASIC_1) the sandbox builds run first [VERIFY Q41].
# CONFORMANCE_SKIP_SLEEP=true skips step 2 (debugging only).
#
# Environment: OCTOPUS_URL, OCTOPUS_SPACE_ID, OCTOPUS_API_KEY (platform-octopus); GITHUB_TOKEN,
# AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET (platform-conformance); SANDBOX_APP_REPO
# (spec variable); CF_API_KEY (the build's own key) or CODEFRESH_API_KEY (Q49); TEST_FILTER
# (optional, passed on); CONFORMANCE_STOP_TIMEOUT_MINUTES (default 30). The clusters come from
# tests/platform.settings.json (PLATFORM_SETTINGS_FILE; AZURE_SUBSCRIPTION_ID overrides); while
# it holds placeholders only the grace period applies. Prints nothing secret.
set -euo pipefail

die() {
  printf 'conformance-arm.sh: %s\n' "$1" >&2
  exit 1
}

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=codefresh/platform/scripts/sandbox-git.sh
. "$script_dir/sandbox-git.sh"
# shellcheck source=codefresh/platform/scripts/aks-power.sh
. "$script_dir/aks-power.sh"
sandbox_require || exit 1

work="$(mktemp -d)"
# Codefresh terminates a build whose log stays silent for 45 minutes ("inactivity"); the runbook
# waits, the wait for Stopped and the grace period below can together exceed that, so a heartbeat
# line every 5 minutes keeps the build active.
(
  while sleep 300; do
    printf 'conformance-arm.sh: still running at %s\n' "$(date -u +%H:%M:%SZ)" >&2
  done
) &
heartbeat=$!
trap 'kill "$heartbeat" 2>/dev/null || true; rm -rf -- "$work"' EXIT

run_id="${PLATFORM_RUN_ID:-}"
if [ -z "$run_id" ]; then
  build="${CF_BUILD_ID:-local}"
  run_id="r$(date -u +%Y%m%dt%H%M)-${build: -8}"
fi
run_id="$(printf '%s' "$run_id" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9.-' '-' | cut -c1-40)"
printf 'conformance-arm.sh: run %s\n' "$run_id" >&2
if command -v cf_export >/dev/null 2>&1; then
  cf_export PLATFORM_RUN_ID="$run_id"
fi

# ---------------------------------------------------------------- 2. force-sleep both tiers
# env-sleep stops without waiting: wait for powerState Stopped of both clusters (aks-power.sh).
# A cluster that does not stop in time (env-sleep kept it up because a task was running) is
# reported and the run goes on: the tests that need it wake it themselves.
wait_stopped() {
  local limit="${CONFORMANCE_STOP_TIMEOUT_MINUTES:-30}" deadline pending tier state
  case "$limit" in '' | *[!0-9]*) limit=30 ;; esac
  aks_power_init "$work" || { printf 'conformance-arm.sh: WARN not waiting for Stopped\n' >&2; return 0; }
  deadline=$(($(date +%s) + limit * 60))
  while :; do
    pending=""
    for tier in nonprod prod; do
      state="$(aks_power_state "$tier")"
      case "$state" in Stopped/Succeeded | unconfigured) ;; *) pending="${pending} ${tier}=${state}" ;; esac
    done
    if [ -z "$pending" ]; then
      printf 'conformance-arm.sh: both clusters report Stopped\n' >&2
      return 0
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
      printf 'conformance-arm.sh: WARN not stopped after %s minute(s):%s\n' "$limit" "$pending" >&2
      return 0
    fi
    sleep 30
  done
}

if [ "${CONFORMANCE_SKIP_SLEEP:-false}" != "true" ]; then
  pids=()
  for environment in infra-nonprod infra-prod; do
    bash "$script_dir/octopus-runbook.sh" --project platform-infrastructure --runbook env-sleep \
      --environment "$environment" --prompt Sleep.Force=true --notes "conformance:${run_id} force-sleep" \
      --wait 60 >/dev/null &
    pids+=("$!")
  done
  failed=0
  for pid in "${pids[@]}"; do
    wait "$pid" || failed=1
  done
  [ "$failed" -eq 0 ] || die "env-sleep did not finish successfully in both tiers"
  wait_stopped
  grace="${CONFORMANCE_STOP_GRACE_MINUTES:-15}"
  case "$grace" in '' | *[!0-9]*) grace=15 ;; esac
  printf 'conformance-arm.sh: waiting %s minute(s) before anything starts the clusters\n' "$grace" >&2
  sleep $((grace * 60))
fi

# ---------------------------------------------------------------- 3. sandbox commits
sandbox_git clone --quiet --branch main "$(sandbox_url)" "$work/sandbox" || die "clone of $SANDBOX_APP_REPO failed"
cd "$work/sandbox"

# Branches of earlier runs go first; the results branch stays.
sandbox_git ls-remote --heads origin 'conformance/*' | awk '{print $2}' | sed 's#^refs/heads/##' |
  while IFS= read -r branch; do
    case "$branch" in
      "conformance/${run_id}/"*) ;;
      *) sandbox_git push --quiet origin --delete "$branch" || printf 'conformance-arm.sh: WARN could not delete %s\n' "$branch" >&2 ;;
    esac
  done

commit_branch() {
  local branch="$1" path="$2" message="$3"
  git checkout --quiet -B "$branch" origin/main
  mkdir -p "$(dirname "$path")"
  printf 'run-id: %s\n' "$run_id" >"$path"
  git add "$path"
  sandbox_git commit --quiet -m "$message"
  sandbox_git push --quiet --force origin "HEAD:refs/heads/$branch"
  git rev-parse HEAD
}

failing_sha="$(commit_branch "conformance/${run_id}/failing-test" toggles/failing-test "conformance ${run_id}: failing-test toggle (CAP-CF-004)")"
green_sha="$(commit_branch "conformance/${run_id}/green" conformance/run-id "conformance ${run_id}: green branch (CAP-CF-004)")"
git checkout --quiet -B main origin/main
mkdir -p conformance
printf 'run-id: %s\n' "$run_id" >conformance/last-run
git add conformance/last-run
sandbox_git commit --quiet -m "conformance ${run_id}: release canary"
sandbox_git push --quiet origin HEAD:refs/heads/main
release_sha="$(git rev-parse HEAD)"
printf 'conformance-arm.sh: failing %s, green %s, release %s\n' "$failing_sha" "$green_sha" "$release_sha" >&2
if command -v cf_export >/dev/null 2>&1; then
  cf_export CONFORMANCE_FAILING_SHA="$failing_sha" CONFORMANCE_GREEN_SHA="$green_sha" CONFORMANCE_RELEASE_SHA="$release_sha"
fi

# ---------------------------------------------------------------- 4. rerun and queue
key="${CODEFRESH_API_KEY:-${CF_API_KEY:-}}"
[ -n "$key" ] || die "no Codefresh API key (CF_API_KEY or CODEFRESH_API_KEY) to queue platform-env/conformance"
cf_url="${CF_URL:-https://g.codefresh.io}"
(umask 077 && printf 'Authorization: %s\nContent-Type: application/json\n' "$key" >"$work/cf-headers")
cf_api() {
  curl -fsS --max-time 60 -H @"$work/cf-headers" "$@"
}

# A second sandbox/release build of the same commit (CAP-CF-008: a rerun creates no second
# Octopus release; CAP-CF-014: the rerun reuses the locked images). With one build at a time this
# build may start before the push's own build; the tests order the builds by start time. The run
# names the pipeline's git trigger by ID. Not fatal: without it the rerun half of the test is
# Inconclusive.
rerun_id=""
trigger_id="$(cf_api "$cf_url/api/pipelines/sandbox%2Frelease" | jq -r 'first(.spec.triggers[]? | select(.name == "main-push") | .id) // empty')" || trigger_id=""
if [ -n "$trigger_id" ]; then
  jq -n --arg sha "$release_sha" --arg trigger "$trigger_id" '{branch: "main", sha: $sha, trigger: $trigger}' >"$work/rerun.json"
  rerun_id="$(cf_api -X POST --data-binary @"$work/rerun.json" "$cf_url/api/pipelines/run/sandbox%2Frelease" | tr -d '"')" || rerun_id=""
fi
if [ -n "$rerun_id" ]; then
  printf 'conformance-arm.sh: sandbox/release rerun queued as build %s\n' "$rerun_id" >&2
else
  printf 'conformance-arm.sh: WARN the sandbox/release rerun was not queued\n' >&2
fi

jq -n --arg run "$run_id" --arg failing "$failing_sha" --arg green "$green_sha" --arg release "$release_sha" \
  --arg rerun "$rerun_id" --arg filter "${TEST_FILTER:-}" \
  '{branch: "main",
    variables: ({PLATFORM_RUN_ID: $run, CONFORMANCE_FAILING_SHA: $failing, CONFORMANCE_GREEN_SHA: $green,
                 CONFORMANCE_RELEASE_SHA: $release}
                + (if $rerun == "" then {} else {CONFORMANCE_RERUN_BUILD_ID: $rerun} end)
                + (if $filter == "" then {} else {TEST_FILTER: $filter} end))}' >"$work/run.json"
build_id="$(cf_api -X POST --data-binary @"$work/run.json" "$cf_url/api/pipelines/run/platform-env%2Fconformance" | tr -d '"')" ||
  die "queueing platform-env/conformance failed"
printf 'conformance-arm.sh: platform-env/conformance queued as build %s\n' "$build_id" >&2
