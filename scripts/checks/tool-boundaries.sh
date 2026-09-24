#!/usr/bin/env bash
# scripts/checks/tool-boundaries.sh
#
# One verb per tool (design ADR-D2, ADR-IR34): Codefresh builds; Octopus releases, promotes, approves and runs
# runbooks; Argo CD applies; GitHub enforces merge rules. This lint fails when a platform file lets a tool leave its
# lane, when a platform secret reaches an app project, or when a platform file names an app.
#
# Usage
#   tool-boundaries.sh [--root DIR]
#       Static lint of the environment repo, which holds every platform file and every app's pipelines, OCL and
#       desired state. App repositories hold none.
#   tool-boundaries.sh --audit-bot-commits [--root DIR] [--range REV_RANGE] [--bot-author REGEX]
#       Bot-path audit (design §6.2): fails when a commit on main made by the platform-bots machine user changes
#       anything other than a pin field under gitops/apps/<app>/envs/<env>/<deployable>/.
#
# Environment
#   PLATFORM_BOT_AUTHORS  Extended regex matched against "Name <email>" of each commit's author and committer: the
#                         identity of the Octopus Git credential's machine user [VERIFY the identity Octopus writes].
#                         --bot-author overrides it.
#   AUDIT_DEPTH           First-parent commits audited when --range is absent (default 20).
#   CI                    "true" turns a missing bot identity into a failure.
#
# Exit codes: 0 pass, 1 violation, 2 usage error, 3 nothing to check.
# Paths absent from a partial tree are reported as SKIP, never as failures.
# Markdown files are never scanned: docs describe the forbidden features.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
MODE="lint"
RANGE=""
BOT_AUTHOR="${PLATFORM_BOT_AUTHORS:-}"
DEPTH="${AUDIT_DEPTH:-20}"

FAILS=0
WARNS=0
CHECKED=0
SKIPS=0

# Matches a single or double quote in extended regexes.
Q="[\"']"

# The Codefresh runtime every spec names (design §7.0, trust boundary TB2).
CF_RUNTIME_RE='^(aks-platform-build/codefresh|<cf-runtime>)$'

usage() {
  cat <<'EOF'
Usage:
  tool-boundaries.sh [--root DIR]
  tool-boundaries.sh --audit-bot-commits [--root DIR] [--range REV_RANGE] [--bot-author REGEX]
EOF
}

is_ci() {
  case "${CI:-}" in
    true | TRUE | True | 1 | yes) return 0 ;;
    *) return 1 ;;
  esac
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --root | --range | --bot-author)
      if [ "$#" -lt 2 ]; then
        echo "tool-boundaries: $1 needs a value" >&2
        exit 2
      fi
      case "$1" in
        --root) ROOT="$2" ;;
        --range) RANGE="$2" ;;
        --bot-author) BOT_AUTHOR="$2" ;;
      esac
      shift 2
      ;;
    --audit-bot-commits)
      MODE="audit"
      shift
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      echo "tool-boundaries: unknown argument '$1'" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [ ! -d "$ROOT" ]; then
  echo "tool-boundaries: root '$ROOT' not found" >&2
  exit 2
fi
ROOT="$(cd "$ROOT" && pwd)"

say() {
  printf '%-4s %-5s %s\n' "$1" "$2" "$3"
}

# Prints the absolute paths that exist for each spec (an environment-repo path).
targets() {
  local spec
  for spec in "$@"; do
    if [ -e "$ROOT/$spec" ]; then
      printf '%s\n' "$ROOT/$spec"
    fi
  done
}

# Rewrites absolute paths in grep output to repo-relative ones.
relativize() {
  sed -e "s|^$ROOT/||"
}

# Drops matches whose content is a comment (YAML, HCL, OCL, shell).
strip_comments() {
  awk '{ line = $0; sub(/^[^:]*:[0-9]+:/, "", line); if (line ~ /^[[:space:]]*(#|\/\/)/) next; print }'
}

# grep over the given absolute paths; prints file:line:content.
search() {
  local pattern="$1"
  shift
  grep -rnIE \
    --exclude-dir=.git --exclude-dir=.terraform --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj \
    --exclude='*.md' \
    -e "$pattern" "$@" 2>/dev/null
}

report() {
  local id="$1" desc="$2" hits="$3"
  if [ -n "$hits" ]; then
    say FAIL "$id" "$desc"
    printf '%s\n' "$hits" | sed 's/^/        /'
    FAILS=$((FAILS + 1))
  else
    say PASS "$id" "$desc"
  fi
}

skip() {
  say SKIP "$1" "$2 (absent: $3)"
  SKIPS=$((SKIPS + 1))
}

# rule ID DESCRIPTION PATTERN [--with-comments] -- SPEC...
rule() {
  local id="$1" desc="$2" pattern="$3"
  shift 3
  local comments="no"
  if [ "${1:-}" = "--with-comments" ]; then
    comments="yes"
    shift
  fi
  if [ "${1:-}" = "--" ]; then
    shift
  fi
  local -a paths=()
  mapfile -t paths < <(targets "$@")
  if [ "${#paths[@]}" -eq 0 ]; then
    skip "$id" "$desc" "$*"
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits
  if [ "$comments" = "yes" ]; then
    hits="$(search "$pattern" "${paths[@]}" | relativize)"
  else
    hits="$(search "$pattern" "${paths[@]}" | strip_comments | relativize)"
  fi
  report "$id" "$desc" "$hits"
}

# Lists the regular files under the given specs, without Markdown, VCS, build output or Terraform caches.
files_in() {
  local d
  while IFS= read -r d; do
    if [ -f "$d" ]; then
      printf '%s\n' "$d"
    else
      find "$d" \( -name .git -o -name .terraform -o -name node_modules -o -name bin -o -name obj \) -prune -o \
        -type f ! -name '*.md' -print 2>/dev/null
    fi
  done < <(targets "$@") | sort
}

# ctx_map FILE: prints "<line><TAB><context>" for every line. The context is the enclosing `step "<slug>"` block of
# an OCL file (heredoc bodies belong to their step), the chain of ancestor keys of a YAML file
# ("/steps/wake_nonprod/commands"), or "-".
ctx_map() {
  case "$1" in
    *.ocl)
      awk '
        BEGIN { depth = 0; sp = 0; hd = ""; pending = "" }
        {
          line = $0
          if (hd != "") {
            t = line; gsub(/^[ \t]+|[ \t]+$/, "", t)
            printf "%d\t%s\n", NR, (sp > 0 ? slug[sp] : "-")
            if (t == hd) hd = ""
            next
          }
          if (match(line, /^[ \t]*step[ \t]+"[^"]+"/)) {
            s = substr(line, RSTART, RLENGTH); sub(/^[ \t]*step[ \t]+"/, "", s); sub(/"$/, "", s); pending = s
          }
          n = length(line); instr = 0
          for (i = 1; i <= n; i++) {
            ch = substr(line, i, 1)
            if (instr) { if (ch == "\\") { i++; continue }; if (ch == "\"") instr = 0; continue }
            if (ch == "\"") { instr = 1; continue }
            if (ch == "#") break
            if (ch == "/" && substr(line, i + 1, 1) == "/") break
            if (ch == "{") { depth++; if (pending != "") { sp++; slug[sp] = pending; sdepth[sp] = depth; pending = "" } }
            else if (ch == "}") { if (sp > 0 && depth == sdepth[sp]) sp--; depth-- }
          }
          printf "%d\t%s\n", NR, (sp > 0 ? slug[sp] : (pending != "" ? pending : "-"))
          if (match(line, /<<-?[A-Za-z_][A-Za-z0-9_]*[ \t]*$/)) {
            hd = substr(line, RSTART, RLENGTH); sub(/^<<-?/, "", hd); gsub(/[ \t]/, "", hd)
          }
        }' "$1"
      ;;
    *.yml | *.yaml)
      awk '
        function chain(   i, c) { c = ""; for (i = 1; i <= kn; i++) c = c "/" key[i]; return (c == "" ? "-" : c) }
        BEGIN { kn = 0 }
        {
          line = $0
          if (line !~ /^[ \t]*(#|$)/ && match(line, /^ *[A-Za-z0-9_.-]+:([ \t]|$)/)) {
            ind = 0; while (substr(line, ind + 1, 1) == " ") ind++
            k = substr(line, ind + 1); sub(/:.*/, "", k)
            while (kn > 0 && kind[kn] >= ind) kn--
            kn++; key[kn] = k; kind[kn] = ind
          }
          printf "%d\t%s\n", NR, chain()
        }' "$1"
      ;;
    *) awk '{ printf "%d\t-\n", NR }' "$1" ;;
  esac
}

# hits_in_context PATTERN FILE...: prints "path:line:context:content" for every matching line that is not a comment.
hits_in_context() {
  local pattern="$1" f m
  shift
  for f in "$@"; do
    m="$(grep -nIE -e "$pattern" "$f" 2>/dev/null)" || continue
    awk -v rel="${f#"$ROOT"/}" '
      NR == FNR { split($0, a, "\t"); ctx[a[1]] = a[2]; next }
      {
        n = $0; sub(/:.*/, "", n)
        c = $0; sub(/^[0-9]+:/, "", c)
        if (c ~ /^[ \t]*(#|\/\/)/) next
        print rel ":" n ":" ctx[n] ":" c
      }' <(ctx_map "$f") <(printf '%s\n' "$m")
  done
}

# Fields of a hits_in_context line.
hit_file() { printf '%s' "${1%%:*}"; }
hit_ctx() {
  local rest="${1#*:}"
  rest="${rest#*:}"
  printf '%s' "${rest%%:*}"
}

# is_release_pipeline REL: an app or starter release pipeline (release.yml or release-<x>.yml).
is_release_pipeline() {
  case "$1" in
    codefresh/apps/*/pipelines/release.yml | codefresh/apps/*/pipelines/release-*.yml) return 0 ;;
    codefresh/templates/*/pipelines/release.yml | codefresh/templates/*/pipelines/release-*.yml) return 0 ;;
    *) return 1 ;;
  esac
}

# is_conformance_pipeline REL: the platform pipelines of the .NET harness and the scripts only they run
# (single-operator exception, ADR-IR34 test harness): arming and publishing push to <sandbox-app-repo>, and the runbook
# helper force-sleeps and wakes the app clusters through env-sleep and env-wake.
is_conformance_pipeline() {
  case "$1" in
    codefresh/platform/pipelines/conformance*.yml) return 0 ;;
    codefresh/platform/scripts/conformance-*.sh | codefresh/platform/scripts/sandbox-git.sh) return 0 ;;
    codefresh/platform/scripts/octopus-runbook.sh) return 0 ;;
    *) return 1 ;;
  esac
}

# is_context_definition REL: files that declare Codefresh contexts by variable name, never by value.
is_context_definition() {
  case "$1" in
    codefresh/register.sh | codefresh/platform/integrations.yaml | codefresh/apps/*/integrations.yaml) return 0 ;;
    *) return 1 ;;
  esac
}

# TB09: Octopus scoping annotations are rendered only by the tenant chart (§7.0 "Wake and pins"); no tenant
# annotation anywhere (ADR-C8).
check_octopus_annotations() {
  local id="TB09" desc="Octopus annotations only in the tenant chart gitops/platform/tenant; no tenant annotation"
  local -a paths=()
  mapfile -t paths < <(targets argocd gitops policies terraform octopus .octopus codefresh)
  if [ "${#paths[@]}" -eq 0 ]; then
    skip "$id" "$desc" "argocd gitops policies terraform octopus .octopus codefresh"
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits="" f
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    case "$f" in
      gitops/platform/tenant/*) ;;
      *) hits+="$f: carries argo.octopus.com/* outside the tenant chart"$'\n' ;;
    esac
  done < <(search 'argo\.octopus\.com/' "${paths[@]}" | strip_comments | relativize | cut -d: -f1 | sort -u)
  hits+="$(search 'argo\.octopus\.com/tenant' "${paths[@]}" | strip_comments | relativize)"
  report "$id" "$desc" "$(printf '%s' "$hits" | sed '/^$/d')"
}

# TB13c: the stored variable sets are included in no project (design §5.3, R5).
check_library_sets() {
  local id="TB13c" desc="Stored variable sets 'Azure Runtime Provisioning' and 'GitHub AISF Sample Apps' are included in no project"
  local -a files=()
  mapfile -t files < <(files_in octopus .octopus | grep -E '\.(tf|ocl)$')
  if [ "${#files[@]}" -eq 0 ]; then
    skip "$id" "$desc" "octopus .octopus"
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits
  hits="$(awk '
    /included_library_variable_sets|included_variable_sets|IncludedLibraryVariableSet/ { collecting = 1; buf = ""; start = FNR }
    collecting {
      buf = buf " " $0
      if ($0 ~ /\]/ || $0 ~ /\)/) {
        lower = tolower(buf)
        if (lower ~ /runtime[_ -]?provisioning|aisf/) print FILENAME ":" start ":" buf
        collecting = 0
      }
    }' "${files[@]}" | relativize)"
  report "$id" "$desc" "$hits"
}

# TB14: the only Octopus API key in Codefresh is OCTOPUS_API_KEY from context platform-octopus (ADR-IR32, ADR-IR34
# decision 4), in app and starter release pipelines (handoff, wake_nonprod) and the conformance pipelines, where it
# may also go out as the X-Octopus-ApiKey header; the context definitions (integrations.yaml, register.sh) name the
# variable without a value. Everything else under codefresh/ and containers/ keeps the ban, and no file may use
# another key name, a command-line key option or a literal key.
check_octopus_api_key() {
  local id="TB14" desc="Octopus API key only as OCTOPUS_API_KEY or X-Octopus-ApiKey, in release and conformance pipelines (ADR-IR32)"
  local pattern="OCTOPUS_API_KEY|OCTO_API_KEY|X-Octopus-ApiKey|--api-?[Kk]ey([[:space:]=]|\$)|API-[A-Z0-9]{16,}"
  local -a paths=()
  mapfile -t paths < <(targets codefresh containers)
  if [ "${#paths[@]}" -eq 0 ]; then
    skip "$id" "$desc" "codefresh containers"
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits="" line file content rest
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    file="${line%%:*}"
    content="${line#*:}"
    content="${content#*:}"
    if is_release_pipeline "$file" || is_conformance_pipeline "$file" || is_context_definition "$file"; then
      rest="${content//OCTOPUS_API_KEY/}"
      rest="${rest//X-Octopus-ApiKey/}"
      if ! grep -qE -- "$pattern" <<<"$rest"; then
        continue
      fi
    fi
    hits+="$line"$'\n'
  done < <(search "$pattern" "${paths[@]}" | strip_comments | relativize)
  report "$id" "$desc" "${hits%$'\n'}"
}

# TB15: admission never rewrites desired state (ADR-D11: no mutateDigest).
check_mutate_digest() {
  local id="TB15" desc="Kyverno image verification does not mutate image references (mutateDigest)"
  local -a paths=()
  mapfile -t paths < <(targets policies gitops/platform)
  if [ "${#paths[@]}" -eq 0 ]; then
    skip "$id" "$desc" "policies gitops/platform"
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  report "$id" "$desc" "$(search 'mutateDigest:[[:space:]]*true' "${paths[@]}" | strip_comments | relativize)"
  local f
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    if ! grep -qE 'mutateDigest:[[:space:]]*false' "$f"; then
      say WARN "$id" "$(printf '%s' "$f" | relativize): verifies images without an explicit 'mutateDigest: false'"
      WARNS=$((WARNS + 1))
    fi
  done < <(grep -rlIE --exclude='*.md' 'kind:[[:space:]]*ImageValidatingPolicy|verifyImages:' "${paths[@]}" 2>/dev/null)
}

# TB16: Codefresh reads repositories and posts statuses; it never commits or pushes. Exception: the conformance
# pipelines push the run's sandbox commits and results to <sandbox-app-repo> only (ADR-IR34 test harness).
check_no_git_writes() {
  local id="TB16" desc="Codefresh never commits or pushes (conformance pipelines: <sandbox-app-repo> only)"
  local -a paths=()
  mapfile -t paths < <(targets codefresh)
  if [ "${#paths[@]}" -eq 0 ]; then
    skip "$id" "$desc" "codefresh"
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits="" line
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    is_conformance_pipeline "$(hit_file "$line")" && continue
    hits+="$line"$'\n'
  done < <(search "git[[:space:]]+(push|commit)([[:space:]]|\$)|type:[[:space:]]*${Q}?git-commit" "${paths[@]}" | strip_comments | relativize)
  report "$id" "$desc" "${hits%$'\n'}"
}

# TB17: only env-wake and env-sleep start or stop a cluster or toggle the alert suppression rule.
check_cluster_power() {
  local id="TB17" desc="az aks start/stop and alert-processing-rule toggles only in env-wake.ocl and env-sleep.ocl (sleep/wake)"
  local pattern="az[[:space:]]+aks[[:space:]]+(start|stop)([[:space:]]|\$)|(Start|Stop)-AzAksCluster|managedClusters/[^[:space:]\"'/]+/(start|stop)([^[:alnum:]]|\$)|alert-processing-rule[[:space:]]+(update|create|delete)|(Set|Update|New|Remove)-AzAlertProcessingRule|AlertsManagement/actionRules"
  local allowed=" .octopus/platform-infrastructure/runbooks/env-wake.ocl .octopus/platform-infrastructure/runbooks/env-sleep.ocl "
  local -a files=()
  mapfile -t files < <(files_in .octopus octopus terraform codefresh containers argocd gitops policies fixtures)
  if [ "${#files[@]}" -eq 0 ]; then
    skip "$id" "$desc" ".octopus octopus terraform codefresh containers argocd gitops policies fixtures"
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits="" line
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    case "$allowed" in
      *" $(hit_file "$line") "*) continue ;;
    esac
    hits+="$line"$'\n'
  done < <(hits_in_context "$pattern" "${files[@]}")
  report "$id" "$desc" "${hits%$'\n'}"
}

# TB18: Octopus REST calls that run runbooks appear only in platform-owned places: the platform-infrastructure
# runbooks, step run-env-wake of platform-wake, step wake_nonprod of app and starter release pipelines, and the
# conformance pipelines. App projects wake without a key, through a Deploy a Release of platform-wake.
check_runbook_runs() {
  local id="TB18" desc="Runbook-run REST calls only in platform-infrastructure runbooks, platform-wake run-env-wake, release wake_nonprod and conformance pipelines"
  local pattern="runbookRuns|runbook-runs|/runbooks/[^[:space:]\"']*/run([/?\"'[:space:]]|\$)|octopus[[:space:]]+runbook[[:space:]]+run|run-runbook"
  local -a files=()
  mapfile -t files < <(files_in .octopus octopus terraform codefresh containers argocd gitops policies fixtures)
  if [ "${#files[@]}" -eq 0 ]; then
    skip "$id" "$desc" ".octopus octopus terraform codefresh containers argocd gitops policies fixtures"
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits="" line rel ctx
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    rel="$(hit_file "$line")"
    ctx="$(hit_ctx "$line")"
    case "$rel" in
      .octopus/platform-infrastructure/runbooks/*.ocl) continue ;;
      .octopus/platform-wake/deployment_process.ocl)
        [ "$ctx" = "run-env-wake" ] && continue
        ;;
    esac
    is_conformance_pipeline "$rel" && continue
    if is_release_pipeline "$rel"; then
      case "$ctx" in
        */wake_nonprod | */wake_nonprod/*) continue ;;
      esac
    fi
    hits+="$line"$'\n'
  done < <(hits_in_context "$pattern" "${files[@]}")
  report "$id" "$desc" "${hits%$'\n'}"
}

# TB19: app projects and starters never hold Azure rights that can start a cluster (sleep/wake contract): no platform
# lifecycle account, no stored provisioner; app identities get no AKS-capable role and nothing on the cluster groups.
check_no_start_rights() {
  local id="TB19" desc="No platform account or Azure start right in app projects, starters or app grants"
  local -a octo=() grants=()
  mapfile -t octo < <(files_in .octopus/apps octopus/templates)
  mapfile -t grants < <(files_in terraform/apps | grep -E '\.tf$')
  if [ "${#octo[@]}" -eq 0 ] && [ "${#grants[@]}" -eq 0 ]; then
    skip "$id" "$desc" ".octopus/apps octopus/templates terraform/apps"
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits=""
  if [ "${#octo[@]}" -gt 0 ]; then
    hits+="$(search 'Azure\.LifecycleAccount|azure-platform-lifecycle-|azure-runtime-provisioner|Azure Runtime Provisioner' "${octo[@]}" | strip_comments | relativize)"
    hits+=$'\n'
  fi
  if [ "${#grants[@]}" -gt 0 ]; then
    hits+="$(awk '
      /^resource[ \t]+"azurerm_role_assignment"/ { inblock = 1; scope = ""; role = ""; start = FNR; next }
      inblock && /^[ \t]*scope[ \t]*=/ { scope = $0 }
      inblock && /^[ \t]*role_definition_name[ \t]*=/ { role = $0 }
      inblock && /^}/ {
        if (role ~ /"(Owner|User Access Administrator|Role Based Access Control Administrator|Azure Kubernetes Service[^"]*)"/ ||
            scope ~ /(_aks|-aks|managedClusters|kubernetes_cluster)/)
          print FILENAME ":" start ": grants" role " at" scope
        inblock = 0
      }' "${grants[@]}" | relativize)"
  fi
  report "$id" "$desc" "$(printf '%s' "$hits" | sed '/^$/d')"
}

# TB20: the Space Manager key stays in platform projects (ADR-IR32, ADR-IR34 decisions 17 and 24):
# Platform.OctopusApiKey only in platform-infrastructure, PlatformWake.* only in platform-wake, the X-Octopus-ApiKey
# header only in those two; never in an app project or a starter. No literal key anywhere.
check_platform_key() {
  local id="TB20" desc="Octopus key variables only in platform-infrastructure and platform-wake; PlatformWake.* never in apps; no literal API key"
  local -a octo=() rest_files=()
  mapfile -t octo < <(files_in .octopus octopus/templates)
  mapfile -t rest_files < <(files_in octopus terraform argocd gitops policies apps fixtures)
  if [ "${#octo[@]}" -eq 0 ] && [ "${#rest_files[@]}" -eq 0 ]; then
    skip "$id" "$desc" ".octopus octopus terraform argocd gitops policies apps fixtures"
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits="" line rel content
  if [ "${#octo[@]}" -gt 0 ]; then
    while IFS= read -r line; do
      [ -n "$line" ] || continue
      rel="$(hit_file "$line")"
      content="${line#*:}"
      content="${content#*:}"
      case "$rel" in
        .octopus/platform-infrastructure/*)
          grep -qE 'PlatformWake\.' <<<"$content" || continue
          ;;
        .octopus/platform-wake/*)
          grep -qE 'Platform\.OctopusApiKey' <<<"$content" || continue
          ;;
      esac
      hits+="$line"$'\n'
    done < <(hits_in_context 'Platform\.OctopusApiKey|PlatformWake\.|X-Octopus-ApiKey' "${octo[@]}")
  fi
  if [ "$((${#octo[@]} + ${#rest_files[@]}))" -gt 0 ]; then
    hits+="$(search 'API-[A-Z0-9]{16,}' "${octo[@]}" "${rest_files[@]}" | relativize)"
  fi
  report "$id" "$desc" "$(printf '%s' "$hits" | sed '/^$/d')"
}

# TB21: trust boundary TB2 (design §5.1, ADR-IR34 build runner): the runner runs in aks-platform-build with one runtime,
# the build cluster holds no role assignment, and pipelines get no cloud identity; registry pushes use the tokens of
# Codefresh registry integrations. The conformance pipelines (context platform-conformance) are the recorded
# single-operator exception.
check_build_cluster() {
  local id="TB21" desc="TB2: one runtime aks-platform-build/codefresh; no grant in terraform/build; no cloud identity for app pipelines or the runner"
  local -a specs=() build=() runner=() app_pipes=()
  mapfile -t specs < <(files_in codefresh | grep -E '/specs/[^/]+\.ya?ml$')
  mapfile -t build < <(files_in terraform/build | grep -E '\.tf$')
  mapfile -t runner < <(targets codefresh/runner)
  mapfile -t app_pipes < <(targets codefresh/apps codefresh/templates)
  if [ "$((${#specs[@]} + ${#build[@]} + ${#runner[@]} + ${#app_pipes[@]}))" -eq 0 ]; then
    skip "$id" "$desc" "codefresh terraform/build"
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits="" f runtime
  for f in "${specs[@]}"; do
    runtime="$(awk '
      /^[[:space:]]*runtimeEnvironment:/ { inside = 1; next }
      inside && /^[[:space:]]*name:/ { v = $0; sub(/^[[:space:]]*name:[[:space:]]*/, "", v); gsub(/["'"'"']/, "", v); sub(/[[:space:]]+#.*$/, "", v); print v; exit }
      inside && /^[^[:space:]]/ { inside = 0 }' "$f")"
    if ! grep -qE "$CF_RUNTIME_RE" <<<"$runtime"; then
      hits+="${f#"$ROOT"/}: runtimeEnvironment.name is '${runtime:-unset}', not aks-platform-build/codefresh"$'\n'
    fi
  done
  if [ "${#build[@]}" -gt 0 ]; then
    hits+="$(search 'azurerm_role_assignment|azuread_[a-z_]*role_assignment|azurerm_federated_identity_credential' "${build[@]}" | strip_comments | relativize)"
    hits+=$'\n'
  fi
  if [ "${#runner[@]}" -gt 0 ]; then
    hits+="$(search 'azure\.workload\.identity|AZURE_CLIENT_(ID|SECRET)|ARM_CLIENT_SECRET|eks\.amazonaws\.com/role-arn|iam\.gke\.io' "${runner[@]}" | strip_comments | relativize)"
    hits+=$'\n'
  fi
  if [ "${#app_pipes[@]}" -gt 0 ]; then
    hits+="$(search 'az[[:space:]]+login|az[[:space:]]+account[[:space:]]+get-access-token|AZURE_CLIENT_SECRET|ARM_CLIENT_SECRET|ARM_USE_OIDC|azure/login|platform-conformance' "${app_pipes[@]}" | strip_comments | relativize)"
  fi
  report "$id" "$desc" "$(printf '%s' "$hits" | sed '/^$/d')"
}

# TB22: name lint (ADR-IR34, app-neutral platform): a platform file outside the app-scoped paths never names an app.
# Apps come from apps/*.yaml; the conformance fixture (a platform component) is exempt. Markdown, design, docs,
# contracts, tests and the catalogue may use an app as the labelled example; so may test fixtures inside platform roots
# (a `tests/` folder, such as the Kyverno CLI and `terraform test` fixtures, which are never deployed) and any line that
# says "for example", "e.g." or "such as". Terraform `moved` blocks and lines marked `name-lint: allow` (migration
# records) may name old objects.
check_name_lint() {
  local id="TB22" desc="Platform files name no app outside the app-scoped paths (name lint)"
  local -a apps=()
  local d name fixture="sandbox"
  for d in "$ROOT"/apps/*.yaml; do
    [ -f "$d" ] || continue
    name="$(sed -n 's/^name:[[:space:]]*\([a-z][a-z0-9]*\)[[:space:]]*\(#.*\)\{0,1\}$/\1/p' "$d" | head -n 1)"
    [ -n "$name" ] && [ "$name" != "$fixture" ] && apps+=("$name")
  done
  if [ "${#apps[@]}" -eq 0 ]; then
    skip "$id" "$desc" "apps/*.yaml"
    return 0
  fi
  local -a files=()
  mapfile -t files < <(files_in argocd gitops .octopus octopus codefresh containers policies terraform scripts tools fixtures \
    CODEOWNERS .gitleaks.toml .yamllint.yaml | grep -vE '/tools/Platform\.Onboarding\.Tests/')
  CHECKED=$((CHECKED + 1))
  local hits="" app line rel content
  for app in "${apps[@]}"; do
    while IFS= read -r line; do
      [ -n "$line" ] || continue
      rel="${line%%:*}"
      content="${line#*:}"
      content="${content#*:}"
      case "$rel" in
        "apps/$app.yaml" | "codefresh/apps/$app/"* | ".octopus/apps/$app/"* | "gitops/apps/$app/"* | "containers/apps/$app/"*) continue ;;
        */tests/*) continue ;;
        *.tf)
          grep -qE '^[[:space:]]*(from|to)[[:space:]]*=' <<<"$content" && continue
          ;;
      esac
      # A migration record carries the marker; a labelled example ("for example <app>") is allowed.
      grep -qF 'name-lint: allow' <<<"$content" && continue
      grep -qiE "(for example|e\.g\.|such as)[^.;]*(^|[^A-Za-z0-9])${app}([^A-Za-z0-9]|\$)" <<<"$content" && continue
      hits+="$line"$'\n'
    done < <(grep -nIiE "(^|[^A-Za-z0-9])${app}([^A-Za-z0-9]|\$)" "${files[@]}" 2>/dev/null | strip_comments | relativize)
  done
  report "$id" "$desc" "$(printf '%s' "$hits" | sed '/^$/d' | head -n 60)"
}

run_lint() {
  echo "tool-boundaries: root=$ROOT"

  # Codefresh builds.
  rule TB01 "Codefresh builds: no deploy, approval, helm or launch-composition steps" \
    "type:[[:space:]]*${Q}?(deploy|approval|helm|launch-composition)${Q}?([[:space:]]|#|\$)" \
    -- codefresh
  rule TB02 "Codefresh never reaches app clusters: no argocd, kubectl, helm install/upgrade or az aks credentials" \
    "(^|[^[:alnum:]_./-])(argocd[[:space:]]+(app|appset|proj|cluster|repo|login|account|admin)[[:space:]]|kubectl[[:space:]]+(apply|create|replace|patch|set|delete|edit|scale|rollout|annotate|label|exec|cp|port-forward|get|logs|describe|config)([[:space:]]|\$)|helm[[:space:]]+(install|upgrade|rollback|uninstall)([[:space:]]|\$)|az[[:space:]]+aks[[:space:]]+(get-credentials|command))|type:[[:space:]]*${Q}?[[:alnum:]_/-]*argo-?cd[[:alnum:]_/-]*" \
    -- codefresh
  rule TB03 "Codefresh GitOps Runtime and Promotions stay off" \
    "apiVersion:[[:space:]]*${Q}?codefresh\\.io/|gitops-runtime|kind:[[:space:]]*${Q}?(PromotionFlow|PromotionPolicy|PromotionTemplate|Product)${Q}?([[:space:]]|\$)" \
    -- argocd gitops policies terraform octopus .octopus codefresh

  # Argo CD applies; Octopus is the only image-tag writer and holds the calendar.
  rule TB04 "Octopus is the only image-tag writer: no Argo CD Image Updater" \
    "argocd-image-updater|image-updater\\.argoproj\\.io|kind:[[:space:]]*${Q}?ImageUpdater" \
    -- argocd gitops policies terraform
  rule TB05 "Freezes live in Octopus: no Argo CD syncWindows" \
    "syncWindows" \
    -- argocd gitops terraform
  rule TB06 "No floating 'latest' tag in desired state, deployment config or pipelines" \
    "(:latest([[:space:]\"'@]|\$)|(newTag|tag|imageTag):[[:space:]]*${Q}?latest${Q}?([[:space:]]|\$))" \
    -- gitops argocd .octopus octopus terraform codefresh containers

  # Octopus releases, promotes and runs runbooks; Argo CD applies Kubernetes state.
  rule TB07 "Octopus never applies Kubernetes state: no kubectl apply/set image/patch, Helm or Kubernetes deploy steps, no argocd app sync" \
    "kubectl[[:space:]]+(apply|set[[:space:]]+image|patch|create|replace|edit|scale)([[:space:]]|\$)|helm[[:space:]]+(install|upgrade)([[:space:]]|\$)|Octopus\\.(KubernetesDeploy[[:alnum:]]*|HelmChartUpgrade|Kustomize|KubernetesRunScript)|argocd[[:space:]]+app[[:space:]]+(sync|rollback|set|patch|delete)" \
    -- .octopus octopus

  # Every grant is the provisioner's (ADR-IR34 decision 3): tier layers hold no grant, lock or policy.
  rule TB08 "Tier layers (terraform/tier, terraform/apps/tier) have no role assignments, role definitions, locks or policy assignments" \
    "azurerm_role_assignment|azurerm_role_definition|azurerm_management_lock|azurerm_[a-z_]*policy_assignment|azuread_app_role_assignment|azuread_directory_role_assignment" \
    --with-comments -- terraform/tier terraform/apps/tier

  check_octopus_annotations

  rule TB10 "Trigger sync stays off in the Argo CD step (ADR-D4) [VERIFY property name]" \
    "[Tt]rigger[._ -]?[Ss]ync[[:alnum:]._]*${Q}?[[:space:]]*[=:][[:space:]]*${Q}?[Tt]rue" \
    -- .octopus octopus/templates
  rule TB11 "Codefresh creates releases: no feed or built-in release triggers (the keyless pattern is optional and not built)" \
    "octopusdeploy_(external_feed_create_release_trigger|built_in_trigger)|auto_create_release[[:space:]]*=[[:space:]]*true" \
    -- octopus .octopus
  rule TB12 "No Octopus deployment targets on app clusters (Kubernetes workers only)" \
    "octopusdeploy_kubernetes_(agent_deployment_target|cluster_deployment_target)" \
    -- octopus terraform

  # Stored credentials stay in their lane (ADR-C10, §5.3, R5, ADR-IR34 decision 3).
  rule TB13a "No Octopus project uses the stored Azure Runtime Provisioner" \
    "azure-runtime-provisioner|Azure Runtime Provisioner" \
    -- .octopus octopus/templates
  rule TB13b "Stored Codefresh contexts github-aisf-sample-apps-token and azure-runtime-provisioner are attached to no pipeline" \
    "azure-runtime-provisioner|github-aisf-sample-apps-token" \
    -- codefresh/apps codefresh/templates codefresh/platform/specs codefresh/platform/pipelines
  check_library_sets

  check_octopus_api_key
  check_mutate_digest
  check_no_git_writes

  # Sleep by default, wake on the first job (sleep/wake contract).
  check_cluster_power
  check_runbook_runs
  check_no_start_rights
  check_platform_key

  # ADR-IR34.
  check_build_cluster
  check_name_lint

  echo "tool-boundaries: $CHECKED rules checked, $FAILS failed, $WARNS warnings, $SKIPS skipped"
  if [ "$FAILS" -gt 0 ]; then
    return 1
  fi
  if [ "$CHECKED" -eq 0 ]; then
    return 3
  fi
  return 0
}

# Bot-path audit (design §6.2).
run_audit() {
  local id="AUDIT"
  if ! command -v git >/dev/null 2>&1; then
    say FAIL "$id" "git not found"
    return 1
  fi
  local top
  if ! top="$(git -C "$ROOT" rev-parse --show-toplevel 2>/dev/null)"; then
    say SKIP "$id" "not a git work tree: $ROOT"
    return 3
  fi
  if [ -z "$BOT_AUTHOR" ]; then
    if is_ci; then
      say FAIL "$id" "PLATFORM_BOT_AUTHORS is not set, so the bot-path audit cannot run (CI=true)"
      return 1
    fi
    say SKIP "$id" "PLATFORM_BOT_AUTHORS is not set (fails when CI=true)"
    return 3
  fi
  case "$DEPTH" in
    '' | *[!0-9]*)
      echo "tool-boundaries: AUDIT_DEPTH must be a number" >&2
      return 2
      ;;
  esac

  # Path of ROOT inside its Git work tree: empty at the repository root.
  local prefix pin_re
  prefix="$(git -C "$ROOT" rev-parse --show-prefix)"
  pin_re="^${prefix//./\\.}gitops/apps/[^/]+/envs/[^/]+/[^/]+/[^/]+\\.ya?ml\$"
  # Pin lines by packaging: Kustomize newTag (and removed digests, V3); Helm tag values; raw image fields.
  local tag='"?[0-9A-Za-z][0-9A-Za-z.+_-]*"?[[:space:]]*(#.*)?$'
  local kustomize_re="^[+-][[:space:]]*newTag:[[:space:]]*${tag}|^-[[:space:]]*digest:[[:space:]]*\"?sha256:[0-9a-f]+\"?[[:space:]]*\$"
  local helm_re="^[+-][[:space:]]*[A-Za-z0-9_.-]*[Tt]ag:[[:space:]]*${tag}"
  local raw_re="^[+-][[:space:]]*(-[[:space:]]+)?image:[[:space:]]*\"?<acr-name>\\.azurecr\\.io/apps/[a-z0-9-]+/[a-z0-9-]+:[0-9A-Za-z.+_-]+\"?[[:space:]]*\$"

  local -a revs=()
  local rev_out
  if [ -n "$RANGE" ]; then
    if ! rev_out="$(git -C "$top" rev-list --first-parent --no-merges "$RANGE" 2>&1)"; then
      say FAIL "$id" "cannot list commits for range '$RANGE': $rev_out"
      return 1
    fi
  else
    if ! rev_out="$(git -C "$top" rev-list --first-parent --no-merges --max-count="$DEPTH" HEAD 2>&1)"; then
      say FAIL "$id" "cannot list commits: $rev_out"
      return 1
    fi
  fi
  if [ -n "$rev_out" ]; then
    mapfile -t revs <<<"$rev_out"
  fi
  if [ "$(git -C "$top" rev-parse --is-shallow-repository 2>/dev/null)" = "true" ]; then
    say WARN "$id" "shallow clone: only the fetched history is audited"
  fi

  local total=0 bots=0 bad_commits=0 c ident f l line_re
  local findings=""
  for c in "${revs[@]}"; do
    [ -n "$c" ] || continue
    total=$((total + 1))
    ident="$(git -C "$top" show -s --format='%an <%ae>%n%cn <%ce>' "$c")"
    if ! grep -Eq -- "$BOT_AUTHOR" <<<"$ident"; then
      continue
    fi
    bots=$((bots + 1))
    local bad=""
    while IFS= read -r f; do
      [ -n "$f" ] || continue
      if ! grep -Eq -- "$pin_re" <<<"$f"; then
        bad+="  changes $f (only pin files under gitops/apps/<app>/envs/ are allowed)"$'\n'
        continue
      fi
      case "$f" in
        */kustomization.yaml) line_re="$kustomize_re" ;;
        */values.yaml) line_re="$helm_re" ;;
        *) line_re="$raw_re" ;;
      esac
      while IFS= read -r l; do
        case "$l" in
          '+++'* | '---'*) continue ;;
        esac
        if ! grep -Eq -- "$line_re" <<<"$l"; then
          bad+="  $f: changes a line other than a pin: $l"$'\n'
        elif grep -Eq -- '^\+.*(newTag|[Tt]ag|image):.*latest' <<<"$l"; then
          bad+="  $f: writes the floating tag 'latest'"$'\n'
        fi
      done < <(git -C "$top" show --format= --unified=0 --no-color --no-ext-diff "$c" -- "$f" | grep -E '^[+-]')
    done < <(git -C "$top" diff-tree --root --no-commit-id --name-only -r --no-renames "$c")
    if [ -n "$bad" ]; then
      bad_commits=$((bad_commits + 1))
      findings+="$(git -C "$top" show -s --format='%h %an: %s' "$c")"$'\n'"$bad"
    fi
  done

  if [ "$bad_commits" -gt 0 ]; then
    say FAIL "$id" "$bad_commits of $bots platform-bots commits (of $total audited) change more than a pin"
    printf '%s' "$findings" | sed 's/^/        /'
    return 1
  fi
  say PASS "$id" "$total commits audited, $bots by platform-bots, all limited to pin lines under gitops/apps/*/envs/"
  return 0
}

case "$MODE" in
  lint) run_lint ;;
  audit) run_audit ;;
esac
exit $?
