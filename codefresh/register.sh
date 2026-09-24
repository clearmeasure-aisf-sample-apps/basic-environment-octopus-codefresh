#!/usr/bin/env bash
# codefresh/register.sh: creates or replaces the platform's Codefresh objects by name
# (ADR-IR34 §11.7.1 item 6; contract §7.0 "Codefresh"). Idempotent: a second run with the
# same inputs changes nothing but the replaced payloads.
#
# Modes (exactly one):
#   --preview      Projects and every pipeline (platform-env and every app), each without
#                  triggers, cron triggers, contexts or variables, marked "preview". No
#                  context, no registry integration.
#   --full         Projects and every pipeline from the full specs (triggers, crons, contexts,
#                  variables), plus the contexts and registry integrations declared in
#                  codefresh/platform/integrations.yaml and codefresh/apps/*/integrations.yaml.
#   --app <app>    Project(s) and pipelines of one app (codefresh/apps/<app>/specs/*.yml) and
#                  the app-owned contexts of codefresh/apps/<app>/integrations.yaml. The
#                  platform contexts it attaches must already exist.
# Options:
#   --dry-run      Print the payloads (secret values masked) and call no API. Also DRY_RUN=1.
#   --recreate-missing-hooks
#                  Delete and create again every pipeline whose git trigger repository has no
#                  Codefresh webhook record, so that Codefresh installs the hook (see "Webhooks").
#   --prune        With --full: delete the superseded pipelines and contexts listed in
#                  codefresh/platform/integrations.yaml (workorders/ci-image and the retired
#                  workorders-* and azure-runtime-provisioner contexts). Run it after the first
#                  release has gone through platform-octopus.
#
# Every pipeline gets spec.runtimeEnvironment.name = $CF_RUNTIME (default
# aks-platform-build/codefresh, the account default runtime <cf-runtime>).
#
# Webhooks: Codefresh installs a repository's webhook only when it creates a pipeline whose git
# trigger names that repository; a replace (PUT) never does. A pipeline registered before its
# repository existed therefore never starts on a push (the sandbox fixture, 2026-09-24). After the
# pipelines, every git trigger repository is checked for a webhook record
# (GET /api/repos/webhooks/<owner>/<repo>/github/<context>): a missing one is a WARN naming the
# remedy, or with --recreate-missing-hooks the affected pipelines are deleted and created again
# (their build history goes with them).
#
# Secrets: none in this repository. A context or registry integration is created only when
# the operator's environment holds every value its declaration names (fromEnv); otherwise it
# is reported as pending. A spec variable whose committed value is a <placeholder> takes the
# value of the environment variable with the same name (for example PLATFORM_BOT_AUTHORS); any
# other spec value that is a whole <token> takes the environment variable TOKEN (upper case,
# dashes to underscores: <sandbox-app-repo> -> SANDBOX_APP_REPO). --full and --app refuse the
# pipeline while one is missing. Values travel only through private files
# (umask 077) that are deleted on exit; the API key goes into a header file, never into argv.
#
# Environment:
#   CF_API_KEY   Codefresh API key of the operator (required unless --dry-run; never printed)
#   CF_URL       Codefresh URL (default https://g.codefresh.io)
#   CF_RUNTIME   Runtime environment for every pipeline (default aks-platform-build/codefresh)
#   Values named by the integrations.yaml files, for example OCTOPUS_API_KEY, ACR_REGISTRY,
#   CF_APPS_RELEASE_PASSWORD, CONFORMANCE_AZURE_CLIENT_SECRET (see docs/preview-codefresh.md).
#
# REST routes (the ones the codefresh CLI uses):
#   GET /api/projects/name/<name>, POST /api/projects
#   GET|PUT /api/contexts/<name>, POST /api/contexts, DELETE /api/contexts/<name>
#   GET /api/registries, POST /api/registries, PATCH /api/registries/<id>
#   GET /api/pipelines/<name>, POST /api/pipelines, PUT /api/pipelines/<name>, DELETE /api/pipelines/<name>
# Codefresh answers some lookups of missing objects with HTTP 500 and a "not found" body;
# those count as 404. Names are URL-encoded (workorders/ci -> workorders%2Fci).
#
# Requires: bash 4+, python3 with PyYAML, curl (not with --dry-run).
set -euo pipefail

die() {
  printf 'register.sh: %s\n' "$1" >&2
  exit 1
}

log() {
  printf '%s\n' "$1"
}

usage() {
  sed -n '2,/^# Secrets:/p' "${BASH_SOURCE[0]}" | sed '$d' | sed 's/^# \{0,1\}//'
}

mode=""
app=""
dry_run="${DRY_RUN:-0}"
prune=false
recreate_hooks=false

while [ "$#" -gt 0 ]; do
  case "$1" in
    --preview | --full)
      [ -z "$mode" ] || die "one mode at a time"
      mode="${1#--}"
      shift
      ;;
    --app)
      [ -z "$mode" ] || die "one mode at a time"
      [ "$#" -ge 2 ] || die "--app needs an app name"
      mode=app
      app="$2"
      shift 2
      ;;
    --dry-run)
      dry_run=1
      shift
      ;;
    --recreate-missing-hooks)
      recreate_hooks=true
      shift
      continue
      ;;
    --prune)
      prune=true
      shift
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      usage >&2
      die "unknown argument: $1"
      ;;
  esac
done

[ -n "$mode" ] || {
  usage >&2
  die "choose --preview, --full or --app <app>"
}
if [ "$prune" = "true" ] && [ "$mode" != "full" ]; then
  die "--prune works with --full only"
fi
if [ "$mode" = "app" ]; then
  [[ "$app" =~ ^[a-z][a-z0-9]{2,11}$ ]] || die "invalid app name '$app' (^[a-z][a-z0-9]{2,11}\$)"
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
CF_URL="${CF_URL:-https://g.codefresh.io}"
CF_URL="${CF_URL%/}"
CF_RUNTIME="${CF_RUNTIME:-aks-platform-build/codefresh}"
export CF_RUNTIME

command -v python3 >/dev/null 2>&1 || die "python3 is required"
python3 -c 'import yaml' 2>/dev/null || die "python3 PyYAML is required"
if [ "$dry_run" != "1" ]; then
  [ -n "${CF_API_KEY:-}" ] || die "CF_API_KEY is not set"
  command -v curl >/dev/null 2>&1 || die "curl is required"
fi

umask 077
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

# ---------------------------------------------------------------- plan
# The renderer writes one payload file per object into $work and prints the plan, one
# tab-separated line per action:
#   project   <name>
#   context   <name>  <payload|->  <missing env vars|->  <optional 0|1>
#   registry  <name>  <payload|->  <missing env vars|->  <primary 0|1>
#   pipeline  <name>  <payload|->  <required contexts|->  <optional contexts|->  <error|->
#   prune     <pipeline|context>  <name>
#   warn      <message>
render_plan() {
  python3 - "$repo_root" "$mode" "$app" "$work" "$prune" <<'PY'
import glob, json, os, re, sys, urllib.parse
import yaml

repo, mode, app, work, prune = sys.argv[1:6]
runtime = os.environ["CF_RUNTIME"]
placeholder = re.compile(r"^<[^<>]+>$")
yaml_placeholder = re.compile(r"<(acr-name|ci-image-version|ci-image-digest|[a-z0-9-]+-digest|[a-z0-9-]+-version)>")
out = []


def emit(*fields):
    out.append("\t".join(str(f) if f not in (None, "") else "-" for f in fields))


def load(path):
    with open(path) as handle:
        return yaml.safe_load(handle) or {}


def dump(name, payload):
    target = os.path.join(work, name)
    with open(target, "w") as handle:
        json.dump(payload, handle, indent=2, sort_keys=True)
        handle.write("\n")
    return target


def substitute(node, missing):
    """Replaces whole-string <token> values (outside `variables`) from the environment."""
    if isinstance(node, dict):
        for key in list(node):
            if key != "variables":
                node[key] = substitute(node[key], missing)
    elif isinstance(node, list):
        return [substitute(item, missing) for item in node]
    elif isinstance(node, str) and placeholder.match(node):
        env_name = re.sub(r"[^A-Za-z0-9]", "_", node[1:-1]).upper()
        value = os.environ.get(env_name, "")
        if value and not placeholder.match(value):
            return value
        missing.append(env_name)
    return node


def safe(name):
    return urllib.parse.quote(name, safe="").replace("%", "_")


def resolve(spec, label):
    """{value: x} -> x; {fromEnv: V} -> env value or None (missing V)."""
    if isinstance(spec, dict) and "value" in spec:
        return str(spec["value"]), None, False
    if isinstance(spec, dict) and "fromEnv" in spec:
        var = spec["fromEnv"]
        value = os.environ.get(var, "")
        if value == "" or placeholder.match(value):
            return None, var, bool(spec.get("optional"))
        return value, None, False
    raise SystemExit(f"{label}: expected {{value: ...}} or {{fromEnv: ...}}")


# Declarations: the platform file for --full, the app files for --full and --app.
declarations = []
if mode == "full":
    declarations.append(os.path.join(repo, "codefresh/platform/integrations.yaml"))
    declarations += sorted(glob.glob(os.path.join(repo, "codefresh/apps/*/integrations.yaml")))
elif mode == "app":
    candidate = os.path.join(repo, f"codefresh/apps/{app}/integrations.yaml")
    if os.path.exists(candidate):
        declarations.append(candidate)

optional_contexts = set()
for path in declarations:
    doc = load(path)
    rel = os.path.relpath(path, repo)
    for ctx in doc.get("contexts") or []:
        name = ctx["name"]
        if ctx.get("optional"):
            optional_contexts.add(name)
        data, missing = {}, []
        for key, spec in (ctx.get("data") or {}).items():
            value, var, optional = resolve(spec, f"{rel} {name}.{key}")
            if value is not None:
                data[key] = value
            elif not optional:
                missing.append(var)
        payload = None
        if not missing:
            payload = dump(f"context-{safe(name)}.json", {
                "apiVersion": "v1",
                "kind": "context",
                "metadata": {"name": name},
                "spec": {"type": ctx.get("type", "secret"), "data": data},
            })
        emit("context", name, payload, ",".join(missing), 1 if ctx.get("optional") else 0)
    for reg in doc.get("registries") or []:
        name = reg["name"]
        fields, missing = {}, []
        for key in ("domain", "username", "password"):
            value, var, _ = resolve(reg.get(key), f"{rel} {name}.{key}")
            if value is None:
                missing.append(var)
            else:
                fields[key] = value
        payload = None
        if not missing:
            payload = dump(f"registry-{safe(name)}.json", {
                "name": name,
                "provider": "other",
                "domain": fields["domain"],
                "username": fields["username"],
                "password": fields["password"],
                "behindFirewall": False,
                "primary": bool(reg.get("primary")),
                "default": False,
                "denyCompositeDomain": False,
            })
        emit("registry", name, payload, ",".join(missing), 1 if reg.get("primary") else 0)
    if mode == "full" and prune == "true":
        superseded = doc.get("superseded") or {}
        for name in superseded.get("pipelines") or []:
            emit("prune", "pipeline", name)
        for name in superseded.get("contexts") or []:
            emit("prune", "context", name)

# Specs: platform-env and every app, or one app.
if mode == "app":
    spec_files = sorted(glob.glob(os.path.join(repo, f"codefresh/apps/{app}/specs/*.yml")))
    if not spec_files:
        raise SystemExit(f"no specs under codefresh/apps/{app}/specs/")
else:
    spec_files = sorted(glob.glob(os.path.join(repo, "codefresh/platform/specs/*.yml")))
    spec_files += sorted(glob.glob(os.path.join(repo, "codefresh/apps/*/specs/*.yml")))

projects, seen = [], set()
for path in spec_files:
    rel = os.path.relpath(path, repo)
    doc = load(path)
    if doc.get("kind") != "pipeline":
        raise SystemExit(f"{rel}: not a pipeline spec")
    meta, spec = doc["metadata"], doc["spec"]
    name, project = meta["name"], meta["project"]
    if not name.startswith(project + "/"):
        raise SystemExit(f"{rel}: pipeline {name} is not in project {project}")
    if name in seen:
        raise SystemExit(f"{rel}: pipeline {name} is declared twice")
    seen.add(name)
    if project not in projects:
        projects.append(project)
    spec["runtimeEnvironment"] = {"name": runtime}
    error = ""
    if mode == "preview":
        for key in ("triggers", "cronTriggers", "contexts", "variables"):
            spec.pop(key, None)
        meta["description"] = ("PREVIEW: visible only, not runnable (no triggers, no contexts). "
                               "Replace with codefresh/register.sh --full (docs/preview-codefresh.md).")
        meta.setdefault("labels", {})["tags"] = ["preview"]
    else:
        missing = []
        for variable in spec.get("variables") or []:
            value = str(variable.get("value", ""))
            if placeholder.match(value):
                env = os.environ.get(variable["key"], "")
                if env and not placeholder.match(env):
                    variable["value"] = env
                else:
                    missing.append(variable["key"])
        if missing:
            error = "set " + ",".join(missing) + " (spec variables still hold placeholders)"
        # Any other spec value that is a whole <token> (for example a trigger's
        # repo: "<sandbox-app-repo>") takes the environment variable TOKEN (upper case,
        # dashes to underscores: SANDBOX_APP_REPO).
        tokens = []
        substitute(spec, tokens)
        if tokens:
            error = "; ".join(filter(None, (error, "set " + ",".join(sorted(set(tokens))) + " (spec placeholders)")))
    template = (spec.get("specTemplate") or {}).get("path", "")
    yaml_path = os.path.join(repo, template.lstrip("./")) if template else ""
    if yaml_path and os.path.exists(yaml_path):
        with open(yaml_path) as handle:
            text = "\n".join(line for line in handle.read().splitlines() if not line.lstrip().startswith("#"))
        found = sorted(set(m.group(0) for m in yaml_placeholder.finditer(text)))
        if found:
            emit("warn", f"{name}: {template} still holds {' '.join(found)}; its builds fail until they are pinned")
    elif template:
        emit("warn", f"{name}: specTemplate.path {template} does not exist in this checkout")
    contexts = spec.get("contexts") or []
    required = [c for c in contexts if c not in optional_contexts]
    optional = [c for c in contexts if c in optional_contexts]
    payload = dump(f"pipeline-{safe(name)}.json", doc)
    emit("pipeline", name, payload, ",".join(required), ",".join(optional), error)

for project in projects:
    out.insert(0, "\t".join(("project", project)))
print("\n".join(out))
PY
}

plan="$work/plan.tsv"
render_plan >"$plan" || die "rendering failed"

# ---------------------------------------------------------------- API helpers
if [ "$dry_run" != "1" ]; then
  printf 'Authorization: %s\nContent-Type: application/json\n' "$CF_API_KEY" >"$work/headers"
fi

urlencode() {
  python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1"
}

# api METHOD PATH [BODY_FILE]: prints the HTTP status; the body goes to $work/response.
# HTTP 500 with "not found" counts as 404.
api() {
  local method="$1" path="$2" body="${3:-}" status
  local -a args=(-sS -o "$work/response" -w '%{http_code}' -X "$method" -H @"$work/headers")
  if [ -n "$body" ]; then
    args+=(--data-binary @"$body")
  fi
  status="$(curl "${args[@]}" "$CF_URL/api$path")" || status=000
  if [ "$status" = "500" ] && grep -qi "not found" "$work/response"; then
    status=404
  fi
  printf '%s' "$status"
}

response_head() {
  head -c 300 "$work/response" 2>/dev/null | tr '\n' ' '
}

# Prints a payload with secret values masked (dry run).
show_masked() {
  python3 - "$1" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1]))
if doc.get("kind") == "context":
    doc["spec"]["data"] = {k: "***" for k in doc["spec"].get("data", {})}
if "password" in doc:
    doc["password"] = "***"
json.dump(doc, sys.stdout, indent=2, sort_keys=True)
sys.stdout.write("\n")
PY
}

failures=0
pending=()
declare -A present_contexts=()

fail() {
  printf 'register.sh: ERROR %s\n' "$1" >&2
  failures=$((failures + 1))
}

# ---------------------------------------------------------------- warnings
while IFS=$'\t' read -r kind message; do
  [ "$kind" = "warn" ] || continue
  printf 'register.sh: WARN %s\n' "$message" >&2
done <"$plan"

# ---------------------------------------------------------------- projects
while IFS=$'\t' read -r kind name; do
  [ "$kind" = "project" ] || continue
  if [ "$dry_run" = "1" ]; then
    log "### project $name"
    continue
  fi
  status="$(api GET "/projects/name/$(urlencode "$name")")"
  case "$status" in
    200) log "project $name: exists" ;;
    404)
      python3 -c 'import json, sys; json.dump({"projectName": sys.argv[1], "tags": ["platform"]}, sys.stdout)' "$name" >"$work/project.json"
      status="$(api POST /projects "$work/project.json")"
      case "$status" in
        2??) log "project $name: created" ;;
        *) fail "project $name: create returned HTTP $status: $(response_head)" ;;
      esac
      ;;
    *) fail "project $name: lookup returned HTTP $status: $(response_head)" ;;
  esac
done <"$plan"

# ---------------------------------------------------------------- contexts
while IFS=$'\t' read -r kind name payload missing optional; do
  [ "$kind" = "context" ] || continue
  if [ "$payload" = "-" ]; then
    pending+=("context $name: set ${missing//,/ }")
    if [ "$dry_run" != "1" ]; then
      status="$(api GET "/contexts/$(urlencode "$name")")"
      [ "$status" = "200" ] && present_contexts["$name"]=1
    fi
    continue
  fi
  if [ "$dry_run" = "1" ]; then
    log "### context $name"
    show_masked "$payload"
    present_contexts["$name"]=1
    continue
  fi
  encoded="$(urlencode "$name")"
  status="$(api GET "/contexts/$encoded")"
  case "$status" in
    200) method=PUT path="/contexts/$encoded" verb=replaced ;;
    404) method=POST path=/contexts verb=created ;;
    *)
      fail "context $name: lookup returned HTTP $status: $(response_head)"
      continue
      ;;
  esac
  status="$(api "$method" "$path" "$payload")"
  case "$status" in
    2??)
      log "context $name: $verb"
      present_contexts["$name"]=1
      ;;
    *) fail "context $name: $method returned HTTP $status: $(response_head)" ;;
  esac
done <"$plan"

# ---------------------------------------------------------------- registry integrations
registries_listed=false
while IFS=$'\t' read -r kind name payload missing _primary; do
  [ "$kind" = "registry" ] || continue
  if [ "$payload" = "-" ]; then
    pending+=("registry $name: set ${missing//,/ }")
    continue
  fi
  if [ "$dry_run" = "1" ]; then
    log "### registry $name"
    show_masked "$payload"
    continue
  fi
  if [ "$registries_listed" = "false" ]; then
    status="$(api GET /registries)"
    [ "$status" = "200" ] || {
      fail "registries: list returned HTTP $status: $(response_head)"
      break
    }
    cp "$work/response" "$work/registries.json"
    registries_listed=true
  fi
  id="$(python3 - "$work/registries.json" "$name" <<'PY'
import json, sys
docs = json.load(open(sys.argv[1]))
if isinstance(docs, dict):
    docs = docs.get("docs") or docs.get("items") or []
for doc in docs:
    if doc.get("name") == sys.argv[2]:
        print(doc.get("_id") or doc.get("id") or "")
        break
PY
)"
  if [ -n "$id" ]; then
    status="$(api PATCH "/registries/$id" "$payload")"
    verb=replaced
  else
    status="$(api POST /registries "$payload")"
    verb=created
  fi
  case "$status" in
    2??) log "registry $name: $verb" ;;
    *) fail "registry $name: returned HTTP $status: $(response_head)" ;;
  esac
done <"$plan"

# ---------------------------------------------------------------- pipelines
registered_payloads=()
while IFS=$'\t' read -r kind name payload required optional error; do
  [ "$kind" = "pipeline" ] || continue
  if [ "$error" != "-" ]; then
    fail "pipeline $name: $error"
    continue
  fi
  drop=()
  if [ "$mode" != "preview" ] && [ "$dry_run" != "1" ]; then
    missing_required=()
    for context in ${required//,/ }; do
      [ "$context" = "-" ] && continue
      [ -n "${present_contexts[$context]:-}" ] && continue
      status="$(api GET "/contexts/$(urlencode "$context")")"
      if [ "$status" = "200" ]; then
        present_contexts["$context"]=1
      else
        missing_required+=("$context")
      fi
    done
    if [ "${#missing_required[@]}" -gt 0 ]; then
      fail "pipeline $name: context(s) ${missing_required[*]} do not exist; supply their values and rerun"
      continue
    fi
    for context in ${optional//,/ }; do
      [ "$context" = "-" ] && continue
      if [ -z "${present_contexts[$context]:-}" ]; then
        status="$(api GET "/contexts/$(urlencode "$context")")"
        if [ "$status" = "200" ]; then
          present_contexts["$context"]=1
        else
          drop+=("$context")
        fi
      fi
    done
  fi
  if [ "${#drop[@]}" -gt 0 ]; then
    printf 'register.sh: WARN %s: optional context(s) %s absent; attached next time they exist\n' "$name" "${drop[*]}" >&2
    python3 - "$payload" "${drop[@]}" <<'PY'
import json, sys
path, drop = sys.argv[1], set(sys.argv[2:])
doc = json.load(open(path))
doc["spec"]["contexts"] = [c for c in doc["spec"].get("contexts", []) if c not in drop]
json.dump(doc, open(path, "w"), indent=2, sort_keys=True)
PY
  fi
  if [ "$dry_run" = "1" ]; then
    log "### pipeline $name"
    cat "$payload"
    continue
  fi
  encoded="$(urlencode "$name")"
  status="$(api GET "/pipelines/$encoded")"
  case "$status" in
    200) method=PUT path="/pipelines/$encoded?disableRevisionCheck=true" verb=replaced ;;
    404) method=POST path=/pipelines verb=created ;;
    *)
      fail "pipeline $name: lookup returned HTTP $status: $(response_head)"
      continue
      ;;
  esac
  status="$(api "$method" "$path" "$payload")"
  case "$status" in
    2??)
      log "pipeline $name: $verb (runtime $CF_RUNTIME)"
      registered_payloads+=("$name"$'\t'"$payload")
      ;;
    *) fail "pipeline $name: $method returned HTTP $status: $(response_head)" ;;
  esac
done <"$plan"

# ---------------------------------------------------------------- webhooks
# Every git trigger repository of the registered pipelines needs a webhook record (see "Webhooks").
if [ "$dry_run" != "1" ] && [ "${#registered_payloads[@]}" -gt 0 ]; then
  hook_plan="$work/hooks.tsv"
  for entry in "${registered_payloads[@]}"; do
    printf '%s\n' "$entry"
  done | python3 -c '
import json, sys
for line in sys.stdin:
    name, payload = line.rstrip("\n").split("\t", 1)
    doc = json.load(open(payload))
    for trigger in doc.get("spec", {}).get("triggers", []) or []:
        if trigger.get("type", "git") == "git" and trigger.get("repo") and trigger.get("context"):
            print("\t".join([name, payload, trigger["repo"], trigger["context"]]))
' >"$hook_plan"
  declare -A hook_known=()
  while IFS=$'\t' read -r name payload repo context; do
    key="$repo $context"
    if [ -z "${hook_known[$key]:-}" ]; then
      status="$(api GET "/repos/webhooks/${repo%%/*}/${repo#*/}/github/$(urlencode "$context")")"
      if [ "$status" = "200" ] && python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); sys.exit(0 if d.get("endpoint") else 1)' "$work/response" 2>/dev/null; then
        hook_known[$key]=present
      else
        hook_known[$key]=missing
      fi
    fi
    [ "${hook_known[$key]}" = "present" ] && continue
    if [ "$recreate_hooks" = "true" ]; then
      encoded="$(urlencode "$name")"
      if [ "$(api DELETE "/pipelines/$encoded")" = "200" ] && [[ "$(api POST /pipelines "$payload")" == 2?? ]]; then
        log "pipeline $name: recreated so that Codefresh installs the webhook of $repo"
      else
        fail "pipeline $name: recreation for the webhook of $repo failed: $(response_head)"
      fi
    else
      printf 'register.sh: WARN %s: no Codefresh webhook for %s (context %s); pushes will not start it. Rerun with --recreate-missing-hooks.\n' "$name" "$repo" "$context" >&2
    fi
  done <"$hook_plan"
fi

# ---------------------------------------------------------------- prune
while IFS=$'\t' read -r kind what name; do
  [ "$kind" = "prune" ] || continue
  if [ "$dry_run" = "1" ]; then
    log "### prune $what $name"
    continue
  fi
  encoded="$(urlencode "$name")"
  case "$what" in
    pipeline) status="$(api DELETE "/pipelines/$encoded")" ;;
    context) status="$(api DELETE "/contexts/$encoded")" ;;
    *) continue ;;
  esac
  case "$status" in
    2??) log "$what $name: deleted" ;;
    404) log "$what $name: already absent" ;;
    *) fail "$what $name: delete returned HTTP $status: $(response_head)" ;;
  esac
done <"$plan"

# ---------------------------------------------------------------- summary
for item in "${pending[@]}"; do
  printf 'register.sh: PENDING %s\n' "$item" >&2
done
if [ "$dry_run" = "1" ]; then
  log "### DRY RUN ($mode): no API call made"
fi
if [ "$failures" -gt 0 ]; then
  die "$failures error(s); fix them and rerun (the script is idempotent)"
fi
log "done ($mode): $(grep -c '^pipeline' "$plan") pipeline(s), $(grep -c '^project' "$plan") project(s); runtime $CF_RUNTIME"
