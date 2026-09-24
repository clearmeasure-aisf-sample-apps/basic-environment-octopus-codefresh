#!/usr/bin/env bash
# scripts/checks/tool-boundaries.sh
#
# One verb per tool (design ADR-D2): Codefresh builds; Octopus releases,
# promotes, approves, migrates and runs runbooks; Argo CD reconciles; GitHub
# enforces merge rules. This lint fails when a file lets a tool leave its lane
# (the R1-P §8 deny rules, extended by design §11.5).
#
# Usage
#   tool-boundaries.sh [--root DIR]
#       Static lint of the environment repo, which holds every platform file,
#       including codefresh/ and containers/. The application repo holds none.
#   tool-boundaries.sh --audit-bot-commits [--root DIR] [--range REV_RANGE] [--bot-author REGEX]
#       Bot-path audit (design §6.2): fails when a commit on main made by the
#       platform-bots machine user changes anything other than the newTag lines
#       of gitops/workorders/envs/*/kustomization.yaml.
#
# Environment
#   PLATFORM_BOT_AUTHORS  Extended regex matched against "Name <email>" of each
#                         commit's author and committer: the identity of the
#                         Octopus Git credential's machine user [VERIFY the
#                         identity Octopus writes]. --bot-author overrides it.
#   AUDIT_DEPTH           First-parent commits audited when --range is absent
#                         (default 20). A violation keeps failing main builds
#                         until it leaves this window; the failing status is the alert.
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
    --exclude-dir=.git --exclude-dir=.terraform --exclude-dir=node_modules \
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
  local p
  while IFS= read -r p; do
    paths+=("$p")
  done < <(targets "$@")
  if [ "${#paths[@]}" -eq 0 ]; then
    say SKIP "$id" "$desc (absent: $*)"
    SKIPS=$((SKIPS + 1))
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

# TB14: the only Octopus API key in any pipeline is OCTOPUS_API_KEY from the secret context workorders-octopus,
# referenced in codefresh/workorders/pipelines/release.yml only (ADR-IR32, user directive), where the release
# handoff and wake_nonprod (sleep/wake contract) may also send it as the X-Octopus-ApiKey header, the only header
# Octopus accepts for API keys. Every other file under codefresh/ and containers/ keeps the ban, and even
# release.yml may not use another key name, a command-line key option or a literal key.
check_octopus_api_key() {
  local id="TB14" desc="Octopus API key only as OCTOPUS_API_KEY or the X-Octopus-ApiKey header, in workorders/release only (ADR-IR32)"
  local pattern="OCTOPUS_API_KEY|OCTO_API_KEY|X-Octopus-ApiKey|--api-?[Kk]ey([[:space:]=]|\$)|API-[A-Z0-9]{16,}"
  local allowed="codefresh/workorders/pipelines/release.yml"
  local -a paths=()
  local p
  while IFS= read -r p; do
    paths+=("$p")
  done < <(targets codefresh containers)
  if [ "${#paths[@]}" -eq 0 ]; then
    say SKIP "$id" "$desc (absent: codefresh containers)"
    SKIPS=$((SKIPS + 1))
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits line file content rest
  hits=""
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    file="${line%%:*}"
    content="${line#*:}"
    content="${content#*:}"
    if [ "$file" = "$allowed" ]; then
      rest="${content//OCTOPUS_API_KEY/}"
      rest="${rest//X-Octopus-ApiKey/}"
      if ! grep -qE -- "$pattern" <<<"$rest"; then
        continue
      fi
    fi
    hits="$hits$line"$'\n'
  done < <(search "$pattern" "${paths[@]}" | strip_comments | relativize)
  report "$id" "$desc" "${hits%$'\n'}"
}

# ---------------------------------------------------------------- sleep/wake (TB17-TB20)
# The sleep/wake contract: clusters sleep by default and wake on the first Codefresh or Octopus job.

# Lists the regular files under the given specs, without Markdown, VCS or Terraform caches.
files_in() {
  local d
  while IFS= read -r d; do
    if [ -f "$d" ]; then
      printf '%s\n' "$d"
    else
      find "$d" \( -name .git -o -name .terraform -o -name node_modules \) -prune -o \
        -type f ! -name '*.md' -print 2>/dev/null
    fi
  done < <(targets "$@") | sort
}

# ctx_map FILE: prints "<line><TAB><context>" for every line. The context is the enclosing
# `step "<slug>"` block of an OCL file (heredoc bodies belong to their step), the chain of
# ancestor keys of a YAML file ("/steps/wake_nonprod/commands"), or "-".
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

# hits_in_context PATTERN FILE...: prints "path:line:context:content" for every matching line
# that is not a comment. The context comes from ctx_map.
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

# TB17: only env-wake and env-sleep start or stop a cluster or toggle the alert suppression rule.
check_cluster_power() {
  local id="TB17" desc="az aks start/stop and alert-processing-rule toggles only in env-wake.ocl and env-sleep.ocl (sleep/wake)"
  local pattern="az[[:space:]]+aks[[:space:]]+(start|stop)([[:space:]]|\$)|(Start|Stop)-AzAksCluster|managedClusters/[^[:space:]\"'/]+/(start|stop)([^[:alnum:]]|\$)|alert-processing-rule[[:space:]]+(update|create|delete)|(Set|Update|New|Remove)-AzAlertProcessingRule|AlertsManagement/actionRules"
  local allowed=" .octopus/workorders-infrastructure/runbooks/env-wake.ocl .octopus/workorders-infrastructure/runbooks/env-sleep.ocl "
  local -a files=()
  mapfile -t files < <(files_in .octopus octopus terraform codefresh containers argocd gitops policies docs)
  if [ "${#files[@]}" -eq 0 ]; then
    say SKIP "$id" "$desc (absent: .octopus octopus terraform codefresh containers argocd gitops policies docs)"
    SKIPS=$((SKIPS + 1))
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits="" line rel
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    rel="${line%%:*}"
    case "$allowed" in
      *" $rel "*) continue ;;
    esac
    hits+="$line"$'\n'
  done < <(hits_in_context "$pattern" "${files[@]}")
  report "$id" "$desc" "${hits%$'\n'}"
}

# TB18: Octopus REST calls that run runbooks appear only in platform-owned places: the
# workorders-infrastructure runbooks, step run-env-wake of project platform-wake, and step
# wake_nonprod of codefresh/workorders/pipelines/release.yml. App projects (.octopus/workorders)
# wake without a key, through a Deploy a Release of platform-wake (ADR-IR33, sleep/wake contract).
check_runbook_runs() {
  local id="TB18" desc="Runbook-run REST calls only in workorders-infrastructure runbooks, platform-wake step run-env-wake and release.yml wake_nonprod"
  local pattern="runbookRuns|runbook-runs|/runbooks/[^[:space:]\"']*/run([/?\"'[:space:]]|\$)|octopus[[:space:]]+runbook[[:space:]]+run|run-runbook"
  local -a files=()
  mapfile -t files < <(files_in .octopus octopus terraform codefresh containers argocd gitops policies docs)
  if [ "${#files[@]}" -eq 0 ]; then
    say SKIP "$id" "$desc (absent: .octopus octopus terraform codefresh containers argocd gitops policies docs)"
    SKIPS=$((SKIPS + 1))
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits="" line rel rest ctx
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    rel="${line%%:*}"
    rest="${line#*:}"
    rest="${rest#*:}"
    ctx="${rest%%:*}"
    case "$rel" in
      .octopus/workorders-infrastructure/runbooks/*.ocl) continue ;;
      .octopus/platform-wake/deployment_process.ocl)
        [ "$ctx" = "run-env-wake" ] && continue
        ;;
      codefresh/workorders/pipelines/release.yml)
        case "$ctx" in
          */wake_nonprod | */wake_nonprod/*) continue ;;
        esac
        ;;
    esac
    hits+="$line"$'\n'
  done < <(hits_in_context "$pattern" "${files[@]}")
  report "$id" "$desc" "${hits%$'\n'}"
}

# TB19: the workorders project never holds Azure rights that can start a cluster; only env-wake
# (workorders-infrastructure, Azure.LifecycleAccount) does (sleep/wake contract).
check_no_start_rights() {
  local id="TB19" desc="No Azure start rights in the workorders project: no lifecycle account, no Azure wake step, no AKS-capable grant to id-octopus-deploy-*"
  local -a octo=() found=()
  mapfile -t octo < <(files_in .octopus/workorders)
  mapfile -t found < <(files_in terraform/foundation)
  if [ "${#octo[@]}" -eq 0 ] && [ "${#found[@]}" -eq 0 ]; then
    say SKIP "$id" "$desc (absent: .octopus/workorders terraform/foundation)"
    SKIPS=$((SKIPS + 1))
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits="" line rest ctx
  if [ "${#octo[@]}" -gt 0 ]; then
    # The lifecycle accounts hold Contributor on the cluster resource groups (ADR-C10, §5.2).
    hits+="$(search 'Azure\.LifecycleAccount|azure-oidc-env-lifecycle|azure-runtime-provisioner' "${octo[@]}" | strip_comments | relativize)"
    [ -n "$hits" ] && hits+=$'\n'
    # The wake step calls env-wake through the Octopus REST API; it is never an Azure step.
    while IFS= read -r line; do
      [ -n "$line" ] || continue
      rest="${line#*:}"
      rest="${rest#*:}"
      ctx="${rest%%:*}"
      [ "$ctx" = "wake-environment" ] && hits+="$line (wake-environment must not be an Azure step)"$'\n'
    done < <(hits_in_context 'Octopus\.Action\.Azure\.AccountId|Octopus\.Azure(PowerShell|Script|CLI)|AzureAccount' "${octo[@]}")
  fi
  if [ "${#found[@]}" -gt 0 ]; then
    # Deployment identities get no role that can start or stop AKS and nothing on the cluster
    # resource groups (§5.2 grants them Key Vault and SQL roles on rg-workorders-<env> only).
    hits+="$(awk '
      /=>[ \t]*\{[ \t]*$/ || /^[ \t]*\{[ \t]*$/ || /^resource[ \t]/ { scope = ""; role = "" }
      /^[ \t]*scope[ \t]*=/ { scope = $0 }
      /^[ \t]*role(_definition_name)?[ \t]*=/ { role = $0 }
      /^[ \t]*principal(_id)?[ \t]*=.*octopus_deploy/ {
        if (role ~ /"(Owner|Contributor|Azure Kubernetes Service Contributor Role|User Access Administrator|Role Based Access Control Administrator)"/ ||
            scope ~ /rg_aks|rg-workorders-aks|kubernetes_cluster|managedClusters/)
          print FILENAME ":" FNR ": grants" role " at" scope
      }' "${found[@]}" | relativize)"
  fi
  report "$id" "$desc" "$(printf '%s' "$hits" | sed '/^$/d')"
}

# TB20: the sleep/wake credential Platform.OctopusApiKey (and the X-Octopus-ApiKey header) appear
# in config-as-code only in the platform-owned steps that call the Octopus REST API: step
# run-env-wake of platform-wake, and the steps of workorders-infrastructure runbooks that the
# step-scoped variable reaches (S5; keep in sync with contracts sleepWake.credential.stepScoped,
# which C23 compares with octopus/terraform). Never in an app project. No literal key anywhere.
check_platform_key() {
  local id="TB20" desc="Platform.OctopusApiKey only in platform-wake run-env-wake and the REST-calling steps of workorders-infrastructure runbooks; never in app projects; no literal API key"
  local -a octo=() rest_files=()
  mapfile -t octo < <(files_in .octopus)
  mapfile -t rest_files < <(files_in octopus terraform argocd gitops policies)
  if [ "${#octo[@]}" -eq 0 ] && [ "${#rest_files[@]}" -eq 0 ]; then
    say SKIP "$id" "$desc (absent: .octopus octopus terraform argocd gitops policies)"
    SKIPS=$((SKIPS + 1))
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits="" line rel rest ctx
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    rel="${line%%:*}"
    rest="${line#*:}"
    rest="${rest#*:}"
    ctx="${rest%%:*}"
    case "$rel" in
      .octopus/workorders-infrastructure/runbooks/*.ocl)
        case "$ctx" in
          wake-environment | wait-for-workers-and-gateway | decide-sleep | stop-cluster) continue ;;
        esac
        ;;
      .octopus/platform-wake/deployment_process.ocl)
        [ "$ctx" = "run-env-wake" ] && continue
        ;;
    esac
    hits+="$line"$'\n'
  done < <(hits_in_context 'Platform\.OctopusApiKey|X-Octopus-ApiKey' "${octo[@]}")
  if [ "$((${#octo[@]} + ${#rest_files[@]}))" -gt 0 ]; then
    hits+="$(search 'API-[A-Z0-9]{16,}' "${octo[@]}" "${rest_files[@]}" | relativize)"
  fi
  report "$id" "$desc" "$(printf '%s' "$hits" | sed '/^$/d')"
}

# TB09: Octopus scoping annotations belong only on the three named
# Applications (design §7.3, E5); no tenant annotation anywhere (ADR-C8).
check_octopus_annotations() {
  local id="TB09" desc="Octopus annotations only on Applications workorders-tdd, workorders-uat, workorders-prod; no tenant annotation"
  local -a paths=()
  local p
  while IFS= read -r p; do
    paths+=("$p")
  done < <(targets argocd gitops policies terraform octopus .octopus codefresh)
  if [ "${#paths[@]}" -eq 0 ]; then
    say SKIP "$id" "$desc (absent: argocd gitops policies terraform octopus .octopus codefresh)"
    SKIPS=$((SKIPS + 1))
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local allowed="argocd/clusters/nonprod/apps/workorders-tdd.yaml argocd/clusters/nonprod/apps/workorders-uat.yaml argocd/clusters/prod/apps/workorders-prod.yaml"
  local hits="" f
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    case " $allowed " in
      *" $f "*) ;;
      *) hits+="$f: carries argo.octopus.com/* but is not a named-environment Application"$'\n' ;;
    esac
  done < <(search 'argo\.octopus\.com/' "${paths[@]}" | strip_comments | relativize | cut -d: -f1 | sort -u)
  local tenant
  tenant="$(search 'argo\.octopus\.com/tenant' "${paths[@]}" | strip_comments | relativize)"
  if [ -n "$tenant" ]; then
    hits+="$tenant"$'\n'
  fi
  report "$id" "$desc" "$(printf '%s' "$hits" | sed '/^$/d')"
}

# TB13c: the stored variable sets are included in no project (design §5.3, R5).
check_library_sets() {
  local id="TB13c" desc="Stored variable sets 'Azure Runtime Provisioning' and 'GitHub AISF Sample Apps' are included in no project"
  local -a files=()
  local f
  while IFS= read -r f; do
    files+=("$f")
  done < <(targets octopus .octopus | while IFS= read -r d; do
    find "$d" -type f \( -name '*.tf' -o -name '*.ocl' \) -not -path '*/.terraform/*' 2>/dev/null
  done)
  if [ "${#files[@]}" -eq 0 ]; then
    say SKIP "$id" "$desc (absent: octopus .octopus)"
    SKIPS=$((SKIPS + 1))
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

# TB15: admission never rewrites desired state (ADR-D11: no mutateDigest).
check_mutate_digest() {
  local id="TB15" desc="Kyverno image verification does not mutate image references (mutateDigest)"
  local -a paths=()
  local p
  while IFS= read -r p; do
    paths+=("$p")
  done < <(targets policies)
  if [ "${#paths[@]}" -eq 0 ]; then
    say SKIP "$id" "$desc (absent: policies)"
    SKIPS=$((SKIPS + 1))
    return 0
  fi
  CHECKED=$((CHECKED + 1))
  local hits
  hits="$(search 'mutateDigest:[[:space:]]*true' "${paths[@]}" | strip_comments | relativize)"
  report "$id" "$desc" "$hits"
  # The field defaults to true for image verification [VERIFY for
  # ImageValidatingPolicy], so a verifying policy should set false explicitly.
  local f
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    if ! grep -qE 'mutateDigest:[[:space:]]*false' "$f"; then
      say WARN "$id" "$(printf '%s' "$f" | relativize): verifies images without an explicit 'mutateDigest: false'"
      WARNS=$((WARNS + 1))
    fi
  done < <(grep -rlIE --exclude='*.md' 'kind:[[:space:]]*ImageValidatingPolicy|verifyImages:' "${paths[@]}" 2>/dev/null)
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

  # Argo CD reconciles; Octopus is the only image-tag writer and holds the calendar.
  rule TB04 "Octopus is the only image-tag writer: no Argo CD Image Updater" \
    "argocd-image-updater|image-updater\\.argoproj\\.io|kind:[[:space:]]*${Q}?ImageUpdater" \
    -- argocd gitops policies terraform
  rule TB05 "Freezes live in Octopus: no Argo CD syncWindows" \
    "syncWindows" \
    -- argocd gitops terraform
  rule TB06 "No floating 'latest' tag in desired state, deployment config or pipelines" \
    "(:latest([[:space:]\"'@]|\$)|(newTag|tag|imageTag):[[:space:]]*${Q}?latest${Q}?([[:space:]]|\$))" \
    -- gitops argocd .octopus octopus terraform codefresh containers

  # Octopus releases and migrates; Argo CD applies Kubernetes state.
  rule TB07 "Octopus never applies Kubernetes state: no kubectl apply/set image/patch, Helm or Kubernetes deploy steps, no argocd app sync" \
    "kubectl[[:space:]]+(apply|set[[:space:]]+image|patch|create|replace|edit|scale)([[:space:]]|\$)|helm[[:space:]]+(install|upgrade)([[:space:]]|\$)|Octopus\\.(KubernetesDeploy[[:alnum:]]*|HelmChartUpgrade|Kustomize|KubernetesRunScript)|argocd[[:space:]]+app[[:space:]]+(sync|rollback|set|patch|delete)" \
    -- .octopus octopus

  # Contributor cannot assign roles, lock or assign policy (ADR-D10, E36).
  rule TB08 "Environment layer has no role assignments, role definitions, locks or policy assignments" \
    "azurerm_role_assignment|azurerm_role_definition|azurerm_management_lock|azurerm_[a-z_]*policy_assignment|azuread_app_role_assignment|azuread_directory_role_assignment" \
    --with-comments -- terraform/environment

  check_octopus_annotations

  rule TB10 "Trigger sync stays off in the Argo CD step (ADR-D4) [VERIFY property name]" \
    "[Tt]rigger[._ -]?[Ss]ync[[:alnum:]._]*${Q}?[[:space:]]*[=:][[:space:]]*${Q}?[Tt]rue" \
    -- .octopus
  rule TB11 "Codefresh is the only release creator: no feed or built-in release triggers" \
    "octopusdeploy_(external_feed_create_release_trigger|built_in_trigger)|auto_create_release[[:space:]]*=[[:space:]]*true" \
    -- octopus .octopus
  rule TB12 "No Octopus deployment targets on app clusters (Kubernetes workers only)" \
    "octopusdeploy_kubernetes_(agent_deployment_target|cluster_deployment_target)" \
    -- octopus terraform

  # Stored credentials stay in their lane (ADR-C10, §5.3, R5).
  rule TB13a "The deployment project never uses the stored Azure Runtime Provisioner" \
    "azure-runtime-provisioner|Azure Runtime Provisioner" \
    -- .octopus/workorders
  rule TB13b "Stored Codefresh contexts azure-runtime-provisioner and github-aisf-sample-apps-token stay unattached" \
    "azure-runtime-provisioner|github-aisf-sample-apps-token" \
    -- codefresh
  check_library_sets

  check_octopus_api_key

  check_mutate_digest

  rule TB16 "Codefresh reads repositories and posts statuses; it never commits or pushes" \
    "git[[:space:]]+(push|commit)([[:space:]]|\$)|type:[[:space:]]*${Q}?git-commit" \
    -- codefresh

  # Sleep by default, wake on the first job (sleep/wake contract).
  check_cluster_power
  check_runbook_runs
  check_no_start_rights
  check_platform_key

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
  pin_re="^${prefix//./\\.}gitops/workorders/envs/[^/]+/kustomization\\.yaml\$"
  local newtag_re='^[+-][[:space:]]*newTag:[[:space:]]*"?[0-9A-Za-z][0-9A-Za-z.+_-]*"?[[:space:]]*(#.*)?$'

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

  local total=0 bots=0 bad_commits=0 c ident f l
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
        bad+="  changes $f (only environment pin files are allowed)"$'\n'
        continue
      fi
      while IFS= read -r l; do
        case "$l" in
          '+++'* | '---'*) continue ;;
        esac
        if ! grep -Eq -- "$newtag_re" <<<"$l"; then
          bad+="  $f: changes a line other than newTag: $l"$'\n'
        elif grep -Eq -- '^\+.*newTag:[[:space:]]*"?latest' <<<"$l"; then
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
    say FAIL "$id" "$bad_commits of $bots platform-bots commits (of $total audited) change more than an environment pin"
    printf '%s' "$findings" | sed 's/^/        /'
    return 1
  fi
  say PASS "$id" "$total commits audited, $bots by platform-bots, all limited to newTag lines of the pin files"
  return 0
}

case "$MODE" in
  lint) run_lint ;;
  audit) run_audit ;;
esac
exit $?
