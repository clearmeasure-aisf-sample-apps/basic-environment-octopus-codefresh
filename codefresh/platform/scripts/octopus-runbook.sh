#!/usr/bin/env bash
# Runs a config-as-code Octopus runbook through the REST API, optionally with prompted variable
# values, and optionally waits for its task (ADR-IR34 "Test harness": conformance-arm force-sleeps
# both app clusters through env-sleep; conformance teardown does the same). Used only by the
# platform-env/conformance* pipelines, which carry context platform-octopus (§7.0 decision 4).
#
# The calls mirror the harness's OctopusApi (tests/Platform.Conformance.Harness):
#   GET  /api/{space}/projects/{slug}
#   GET  /api/{space}/environments/all
#   GET  /api/{space}/projects/{projectId}/{gitRef}/runbooks?take=1000
#   GET  /api/{space}/projects/{projectId}/{gitRef}/runbooks/{runbookId}/runbookRuns/preview/{environmentId}
#   POST /api/{space}/projects/{projectId}/{gitRef}/runbooks/{runbookId}/run/v1
#   GET  /api/{space}/tasks/{taskId}
#
# Usage: octopus-runbook.sh --project <slug> --runbook <slug|name> --environment <name>
#                           [--git-ref refs/heads/main] [--prompt Name=value]... [--notes <text>]
#                           [--wait <minutes>]
# Prints the task ID on stdout. With --wait it exits 0 only when the task finished successfully.
# Environment: OCTOPUS_URL, OCTOPUS_SPACE_ID, OCTOPUS_API_KEY (never printed; sent from a private
# header file). Requires: bash, curl, jq.
set -euo pipefail

die() {
  printf 'octopus-runbook.sh: %s\n' "$1" >&2
  exit 1
}

project=""
runbook=""
environment=""
git_ref="refs/heads/main"
notes="platform-env conformance ${CF_BUILD_ID:-local}"
wait_minutes=0
prompts=()

while [ "$#" -gt 0 ]; do
  case "$1" in
    --project | --runbook | --environment | --git-ref | --prompt | --notes | --wait)
      [ "$#" -ge 2 ] || die "$1 needs a value"
      case "$1" in
        --project) project="$2" ;;
        --runbook) runbook="$2" ;;
        --environment) environment="$2" ;;
        --git-ref) git_ref="$2" ;;
        --prompt) prompts+=("$2") ;;
        --notes) notes="$2" ;;
        --wait) wait_minutes="$2" ;;
      esac
      shift 2
      ;;
    *)
      die "unknown argument: $1"
      ;;
  esac
done

[ -n "$project" ] && [ -n "$runbook" ] && [ -n "$environment" ] || die "--project, --runbook and --environment are required"
case "$wait_minutes" in '' | *[!0-9]*) die "--wait takes whole minutes" ;; esac
for key in OCTOPUS_URL OCTOPUS_SPACE_ID OCTOPUS_API_KEY; do
  value="${!key:-}"
  case "$value" in
    "" | *'<'*'>'*) die "$key is missing (context platform-octopus)" ;;
  esac
done
command -v jq >/dev/null 2>&1 || die "jq is required"

base="${OCTOPUS_URL%/}/api/${OCTOPUS_SPACE_ID}"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
(umask 077 && printf 'X-Octopus-ApiKey: %s\nContent-Type: application/json\n' "$OCTOPUS_API_KEY" >"$work/headers")

octo() {
  curl -fsS --max-time 60 -H @"$work/headers" "$@"
}

urlencode() {
  jq -rn --arg value "$1" '$value | @uri'
}

ref="$(urlencode "$git_ref")"
project_id="$(octo "$base/projects/$(urlencode "$project")" | jq -r '.Id // empty')" || die "project $project lookup failed"
[ -n "$project_id" ] || die "project $project not found"
environment_id="$(octo "$base/environments/all" | jq -r --arg name "$environment" 'first(.[] | select(.Name == $name) | .Id) // empty')" || die "environment lookup failed"
[ -n "$environment_id" ] || die "environment $environment not found"
runbook_id="$(octo "$base/projects/$project_id/$ref/runbooks?take=1000" |
  jq -r --arg rb "$runbook" 'first(.Items[] | select(.Slug == $rb or .Name == $rb or .Id == $rb) | .Id) // empty')" || die "runbook lookup failed"
[ -n "$runbook_id" ] || die "runbook $runbook not found in $project at $git_ref"

# Prompted variables: map each name to the form element of the run preview.
form='{}'
if [ "${#prompts[@]}" -gt 0 ]; then
  octo "$base/projects/$project_id/$ref/runbooks/$runbook_id/runbookRuns/preview/$environment_id" >"$work/preview.json" ||
    die "run preview failed"
  for prompt in "${prompts[@]}"; do
    name="${prompt%%=*}"
    value="${prompt#*=}"
    [ "$name" != "$prompt" ] || die "--prompt takes Name=value"
    element="$(jq -r --arg name "$name" 'first(.Form.Elements[]? | select((.Control.Name // "") | ascii_downcase == ($name | ascii_downcase)) | .Name) // empty' "$work/preview.json")"
    [ -n "$element" ] || die "runbook $runbook has no prompted variable $name in $environment"
    form="$(jq -c --arg key "$element" --arg value "$value" '. + {($key): $value}' <<<"$form")"
  done
fi

jq -n --arg space "$OCTOPUS_SPACE_ID" --arg project "$project_id" --arg runbook "$runbook_id" --arg ref "$git_ref" \
  --arg environment "$environment_id" --arg notes "$notes" --argjson form "$form" \
  '{SpaceId: $space, ProjectId: $project, RunbookId: $runbook, GitRef: $ref, Notes: $notes,
    Runs: [{EnvironmentId: $environment, FormValues: $form, Comments: $notes}]}' >"$work/run.json"
task_id="$(octo -X POST --data-binary @"$work/run.json" "$base/projects/$project_id/$ref/runbooks/$runbook_id/run/v1" |
  jq -r '(.Resources // [])[0].TaskId // empty')" || die "the run request failed"
[ -n "$task_id" ] || die "Octopus returned no task for the run"
printf 'octopus-runbook.sh: %s/%s in %s queued as %s\n' "$project" "$runbook" "$environment" "$task_id" >&2
printf '%s\n' "$task_id"

[ "$wait_minutes" -gt 0 ] || exit 0
deadline=$(($(date +%s) + wait_minutes * 60))
while :; do
  octo "$base/tasks/$task_id" >"$work/task.json" || die "task $task_id lookup failed"
  if [ "$(jq -r '.IsCompleted' "$work/task.json")" = "true" ]; then
    state="$(jq -r '.State' "$work/task.json")"
    printf 'octopus-runbook.sh: %s finished: %s\n' "$task_id" "$state" >&2
    [ "$(jq -r '.FinishedSuccessfully' "$work/task.json")" = "true" ] || exit 1
    exit 0
  fi
  [ "$(date +%s)" -lt "$deadline" ] || die "task $task_id did not finish within $wait_minutes minute(s)"
  sleep 20
done
