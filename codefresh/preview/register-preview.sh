#!/usr/bin/env bash
# Phase 0 preview: makes the Codefresh projects and pipelines of the platform
# visible before Azure exists (docs/preview-codefresh.md).
#
# Creates or replaces, by name:
#   projects   workorders, platform-env
#   pipelines  workorders/ci, workorders/release, workorders/preview,
#              workorders/ci-image, platform-env/env-checks
# Each pipeline is rendered from its committed spec with these changes:
#   - no triggers and no cron triggers: no webhook is created on any repository;
#   - no contexts and no pipeline variables: the secret contexts come at phase 1;
#   - runtimeEnvironment.name = $CF_RUNTIME for every pipeline;
#   - specTemplate kept (YAML from main of this repo through the Git integration
#     github-aisf-sample-apps), revision forced to main;
#   - a description and the tag phase-0-preview mark it as not runnable until phase 1.
# Phase 1 replaces these objects with the full specs (codefresh replace pipeline -f).
#
# Environment:
#   CF_API_KEY  Codefresh API key (required unless DRY_RUN=1; never printed)
#   CF_URL      Codefresh URL (default https://g.codefresh.io)
#   CF_RUNTIME  Runtime environment name for all five pipelines (required unless DRY_RUN=1)
#   DRY_RUN=1   Print the rendered payloads as JSON; call no API
#
# REST calls (the endpoints the codefresh CLI uses [VERIFY the project routes]):
#   GET /api/projects/name/<name>, POST /api/projects {projectName, tags}
#   GET /api/pipelines/<name>, POST /api/pipelines, PUT /api/pipelines/<name>
#   (<name> URL-encoded, so workorders/ci becomes workorders%2Fci)
# Requires: bash, python3 with PyYAML, curl (not in DRY_RUN).
set -euo pipefail

die() {
  printf 'register-preview.sh: %s\n' "$1" >&2
  exit 1
}

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"

CF_URL="${CF_URL:-https://g.codefresh.io}"
CF_URL="${CF_URL%/}"
dry_run="${DRY_RUN:-0}"

specs=(
  codefresh/workorders/specs/workorders-ci.yml
  codefresh/workorders/specs/workorders-release.yml
  codefresh/workorders/specs/workorders-preview.yml
  codefresh/workorders/specs/workorders-ci-image.yml
  codefresh/specs/platform-env-checks.yml
)
projects=(workorders platform-env)

command -v python3 >/dev/null 2>&1 || die "python3 is required"
python3 -c 'import yaml' 2>/dev/null || die "python3 PyYAML is required"

if [ "$dry_run" = "1" ]; then
  runtime="${CF_RUNTIME:-<cf-runtime>}"
else
  [ -n "${CF_API_KEY:-}" ] || die "CF_API_KEY is not set"
  [ -n "${CF_RUNTIME:-}" ] || die "CF_RUNTIME is not set"
  command -v curl >/dev/null 2>&1 || die "curl is required"
  runtime="$CF_RUNTIME"
fi

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

# Renders one spec file into the preview payload (JSON on stdout).
render() {
  python3 - "$repo_root/$1" "$runtime" <<'PY'
import json, sys, yaml
path, runtime = sys.argv[1], sys.argv[2]
with open(path) as f:
    doc = yaml.safe_load(f)
if doc.get("kind") != "pipeline":
    sys.exit(f"{path}: not a pipeline spec")
spec = doc["spec"]
for key in ("triggers", "cronTriggers", "contexts", "variables"):
    spec.pop(key, None)
spec["runtimeEnvironment"] = {"name": runtime}
spec["specTemplate"]["revision"] = "main"
meta = doc["metadata"]
meta["description"] = ("PHASE 0 PREVIEW: visible only, not runnable until phase 1 "
                       "(no triggers, no contexts; the CI image and ACR do not exist yet). "
                       "See docs/preview-codefresh.md.")
meta.setdefault("labels", {})["tags"] = ["phase-0-preview"]
json.dump(doc, sys.stdout, indent=2, sort_keys=True)
sys.stdout.write("\n")
PY
}

urlencode() {
  python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1"
}

# api METHOD PATH [BODY_FILE] -> prints the HTTP status; the body goes to $work/response.
api() {
  local method="$1" path="$2" body="${3:-}"
  local -a args=(-sS -o "$work/response" -w '%{http_code}' -X "$method" -H @"$work/headers")
  if [ -n "$body" ]; then
    args+=(--data-binary @"$body")
  fi
  curl "${args[@]}" "$CF_URL/api$path"
}

if [ "$dry_run" != "1" ]; then
  # Headers from a private file, so the key never appears in the process list.
  (umask 077 && printf 'Authorization: %s\nContent-Type: application/json\n' "$CF_API_KEY" >"$work/headers")
fi

for project in "${projects[@]}"; do
  payload="$work/project-$project.json"
  python3 -c 'import json, sys; json.dump({"projectName": sys.argv[1], "tags": ["phase-0-preview"]}, sys.stdout)' "$project" >"$payload"
  if [ "$dry_run" = "1" ]; then
    printf '### project %s\n' "$project"
    cat "$payload"
    printf '\n'
    continue
  fi
  status="$(api GET "/projects/name/$(urlencode "$project")")"
  # Codefresh answers some lookups of missing objects with HTTP 500 and a "not found" body.
  if [ "$status" = "500" ] && grep -qi "not found" "$work/response"; then status=404; fi
  case "$status" in
    200) printf 'project %s: exists\n' "$project" ;;
    404)
      status="$(api POST /projects "$payload")"
      case "$status" in
        2??) printf 'project %s: created\n' "$project" ;;
        *) die "project $project: create returned HTTP $status: $(head -c 300 "$work/response")" ;;
      esac
      ;;
    *) die "project $project: lookup returned HTTP $status: $(head -c 300 "$work/response")" ;;
  esac
done

for spec_file in "${specs[@]}"; do
  [ -f "$repo_root/$spec_file" ] || die "missing $spec_file"
  payload="$work/$(basename "$spec_file" .yml).json"
  render "$spec_file" >"$payload"
  name="$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["metadata"]["name"])' "$payload")"
  if [ "$dry_run" = "1" ]; then
    printf '### pipeline %s (from %s)\n' "$name" "$spec_file"
    cat "$payload"
    continue
  fi
  encoded="$(urlencode "$name")"
  status="$(api GET "/pipelines/$encoded")"
  # Codefresh answers some lookups of missing objects with HTTP 500 and a "not found" body.
  if [ "$status" = "500" ] && grep -qi "not found" "$work/response"; then status=404; fi
  case "$status" in
    200) method=PUT path="/pipelines/$encoded" verb=replaced ;;
    404) method=POST path=/pipelines verb=created ;;
    *) die "pipeline $name: lookup returned HTTP $status: $(head -c 300 "$work/response")" ;;
  esac
  status="$(api "$method" "$path" "$payload")"
  case "$status" in
    2??) printf 'pipeline %s: %s\n' "$name" "$verb" ;;
    *) die "pipeline $name: $method returned HTTP $status: $(head -c 300 "$work/response")" ;;
  esac
done

if [ "$dry_run" = "1" ]; then
  printf '### DRY_RUN: no API call made\n'
else
  printf 'done: 2 projects, %d pipelines (phase 0 preview)\n' "${#specs[@]}"
fi
