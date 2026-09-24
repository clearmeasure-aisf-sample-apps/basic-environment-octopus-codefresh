#!/usr/bin/env bash
# scripts/checks/consistency.sh
#
# Cross-package consistency of the environment repo (design §7.0, ADR-IR34): every platform file against
# contracts/platform-contracts.yaml and the app descriptors apps/*.yaml. Each finding names the package that owns
# the file (design §11.10). App-owned files are checked from their descriptor by the onboarding tool
# (`validate-all.sh onboarding`: schema, scaffold completeness, pin shape, cross-app references, blast radius).
#
# Checks
#   C01 contracts parse        C02 Octopus annotations    C03 ApplicationSet apps     C04 AppProjects
#   C05 platform namespaces    C06 Argo CD bootstrap      C09 rendered app overlays   C10 database endpoints
#   C11 stores and vaults      C12 platform runbooks      C15 Octopus Terraform       C18 signer and Kyverno
#   C19 Terraform layers       C20 retired paths, names   C21 placeholders            C22 runbook inputs
#   C23 sleep and wake         C24 descriptors (basic)    C25 Codefresh handshake
#   Retired with ADR-IR34 (they checked app #1's shape): C07 and C08 (now the onboarding tool), C13 deployment
#   process, C14 variables, C16 Codefresh steps, C17 environment config.
#
# Usage
#   consistency.sh [--root DIR] [--contracts FILE] [--no-render]
#
#   --root       Environment-repo root (default: two levels above this script).
#   --contracts  Contracts file (default: <root>/contracts/platform-contracts.yaml).
#   --no-render  Skip `kustomize build`; C09 is skipped.
#
# Needs python3 with PyYAML. Uses `kustomize` (or $KUSTOMIZE) when present.
# Output lines: STATUS ID [owner] path: message. STATUS is PASS, FAIL, WARN or SKIP.
# Exit codes: 0 no failures, 1 failures, 2 usage error, 3 skipped (tool missing locally; with CI=true a missing
# tool fails instead).
#
# Robust to partial trees: a file whose top-level directory is absent is SKIP; a file missing from a directory
# that exists is FAIL.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CONTRACTS=""
RENDER="yes"

usage() {
  cat <<'EOF'
Usage: consistency.sh [--root DIR] [--contracts FILE] [--no-render]
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
    --root | --contracts)
      if [ "$#" -lt 2 ]; then
        echo "consistency: $1 needs a value" >&2
        exit 2
      fi
      case "$1" in
        --root) ROOT="$2" ;;
        --contracts) CONTRACTS="$2" ;;
      esac
      shift 2
      ;;
    --no-render)
      RENDER="no"
      shift
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      echo "consistency: unknown argument '$1'" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [ ! -d "$ROOT" ]; then
  echo "consistency: root '$ROOT' not found" >&2
  exit 2
fi
ROOT="$(cd "$ROOT" && pwd)"
CONTRACTS="${CONTRACTS:-$ROOT/contracts/platform-contracts.yaml}"

PYTHON_BIN="${PYTHON:-python3}"
if ! command -v "$PYTHON_BIN" >/dev/null 2>&1 || ! "$PYTHON_BIN" -c 'import yaml' >/dev/null 2>&1; then
  if is_ci; then
    echo "FAIL C00   [pragmatist] -: python3 with PyYAML is required (CI=true)"
    exit 1
  fi
  echo "SKIP C00   [pragmatist] -: python3 with PyYAML not found; consistency checks skipped locally"
  exit 3
fi

KUSTOMIZE_BIN=""
if [ "$RENDER" = "yes" ]; then
  KUSTOMIZE_BIN="$(command -v "${KUSTOMIZE:-kustomize}" 2>/dev/null || true)"
fi

exec "$PYTHON_BIN" - "$ROOT" "$CONTRACTS" "$KUSTOMIZE_BIN" "$RENDER" <<'PYEOF'
import glob
import os
import re
import subprocess
import sys

import yaml

ROOT, CONTRACTS, KUSTOMIZE, RENDER = sys.argv[1], sys.argv[2], sys.argv[3] or None, sys.argv[4] == "yes"
COUNTS = {"PASS": 0, "FAIL": 0, "WARN": 0, "SKIP": 0}
OWNERSHIP = []

# --------------------------------------------------------------------------- output

def owner(rel):
    if not rel:
        return "-"
    best = ("", "-")
    for item in OWNERSHIP:
        root = item.get("root", "")
        if rel == root.rstrip("/") or rel.startswith(root):
            if len(root) > len(best[0]):
                best = (root, item.get("package", "-"))
    return best[1]


def out(status, cid, rel, msg):
    COUNTS[status] += 1
    print(f"{status:<4} {cid:<4} [{owner(rel)}] {rel or '-'}: {msg}")


def verdict(cid, rel, errors, ok_msg, warn=False):
    if errors:
        out("WARN" if warn else "FAIL", cid, rel, "; ".join(errors))
    else:
        out("PASS", cid, rel, ok_msg)

# --------------------------------------------------------------------------- paths and files

def P(rel):
    return os.path.join(ROOT, rel)


def exists(rel):
    return os.path.exists(P(rel))


def top(rel):
    return rel.split("/")[0]


def absent(cid, rel, what):
    """A missing file: SKIP when its top-level directory is absent, FAIL otherwise."""
    if exists(top(rel)):
        out("FAIL", cid, rel, f"{what}: file missing")
    else:
        out("SKIP", cid, rel, f"{what}: directory '{top(rel)}' absent")


def walk(rel_dir, exts=(".yaml", ".yml")):
    base = P(rel_dir)
    if not os.path.isdir(base):
        return []
    found = []
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in (".git", ".terraform", "node_modules", "bin", "obj", "TestResults")]
        for name in filenames:
            if exts is None or name.endswith(exts):
                found.append(os.path.relpath(os.path.join(dirpath, name), ROOT))
    return sorted(found)


def text(rel):
    try:
        with open(P(rel), encoding="utf-8") as f:
            return f.read()
    except (OSError, UnicodeDecodeError):
        return None


COMMENT = re.compile(r"^\s*(#|//)")


def code_text(rel):
    """File text with full-line comments removed (YAML, HCL, OCL, shell, C#)."""
    t = text(rel)
    if t is None:
        return None
    return "\n".join(line for line in t.splitlines() if not COMMENT.match(line))

_DOCS = {}


def docs(rel, cid="C00"):
    if rel in _DOCS:
        return _DOCS[rel]
    result = None
    try:
        with open(P(rel), encoding="utf-8") as f:
            result = [d for d in yaml.safe_load_all(f) if d is not None]
    except OSError:
        result = None
    except yaml.YAMLError as e:
        out("FAIL", cid, rel, "YAML does not parse: " + str(e).splitlines()[0])
        result = None
    _DOCS[rel] = result
    return result


def g(d, path, default=None):
    cur = d
    for key in path:
        if isinstance(cur, dict) and key in cur:
            cur = cur[key]
        elif isinstance(cur, list) and isinstance(key, int) and key < len(cur):
            cur = cur[key]
        else:
            return default
    return cur


def all_dicts(obj):
    if isinstance(obj, dict):
        yield obj
        for v in obj.values():
            yield from all_dicts(v)
    elif isinstance(obj, list):
        for v in obj:
            yield from all_dicts(v)


def all_strings(obj):
    if isinstance(obj, str):
        yield obj
    elif isinstance(obj, dict):
        for v in obj.values():
            yield from all_strings(v)
    elif isinstance(obj, list):
        for v in obj:
            yield from all_strings(v)


def blocks(src, header_re):
    """Yield (name, body) for blocks whose opening line matches header_re (group 1 = name).
    Braces inside strings and heredocs are ignored."""
    lines = src.splitlines()
    i = 0
    rx = re.compile(header_re)
    while i < len(lines):
        m = rx.match(lines[i])
        if not m:
            i += 1
            continue
        depth, body, heredoc = 0, [], None
        j = i
        while j < len(lines):
            line = lines[j]
            body.append(line)
            if heredoc:
                if line.strip() == heredoc:
                    heredoc = None
                j += 1
                continue
            in_str, esc = False, False
            k = 0
            while k < len(line):
                ch = line[k]
                if in_str:
                    if esc:
                        esc = False
                    elif ch == "\\":
                        esc = True
                    elif ch == '"':
                        in_str = False
                elif ch == '"':
                    in_str = True
                elif ch == "#" or line.startswith("//", k):
                    break
                elif ch == "{":
                    depth += 1
                elif ch == "}":
                    depth -= 1
                elif line.startswith("<<", k):
                    hm = re.match(r"<<-?\s*([A-Za-z_][A-Za-z0-9_]*)", line[k:])
                    if hm:
                        heredoc = hm.group(1)
                        break
                k += 1
            j += 1
            if depth <= 0 and not heredoc and "{" in "".join(body):
                break
        yield m.group(1), "\n".join(body)
        i = j

# --------------------------------------------------------------------------- C01 contracts

def load_contracts():
    cid = "C01"
    rel = os.path.relpath(CONTRACTS, ROOT) if CONTRACTS.startswith(ROOT) else CONTRACTS
    try:
        with open(CONTRACTS, encoding="utf-8") as f:
            c = yaml.safe_load(f)
    except OSError:
        out("FAIL", cid, rel, "contracts file not found")
        return None
    except yaml.YAMLError as e:
        out("FAIL", cid, rel, "contracts file does not parse: " + str(e).splitlines()[0])
        return None
    need = ["expansions", "placeholders", "envRepo", "descriptors", "starters", "azure", "octopus", "codefresh",
            "registry", "argocd", "kubernetes", "sleepWake", "conformance", "retiredPaths", "retiredNames", "ownership"]
    missing = [k for k in need if k not in (c or {})]
    OWNERSHIP.extend((c or {}).get("ownership", []))
    verdict(cid, rel, [f"missing section '{k}'" for k in missing], "contracts parse; all sections present")
    return c if not missing else None


C = load_contracts()
if C is None:
    print("consistency: contracts unusable; stopping")
    sys.exit(1)

ENVS = C["expansions"]["env"]
TIERS = C["expansions"]["tier"]
TIER_OF = C["expansions"]["tierOfEnv"]
PLATFORM_CONFIG_ROOTS = ["argocd", "gitops", ".octopus", "octopus", "codefresh", "containers", "policies", "terraform"]
IN_CLUSTER_POOL = re.compile(r"k8s-(tdd|uat|prod)|Platform\.WorkerPool")


def x(s, **kw):
    for k, v in kw.items():
        s = s.replace("{" + k + "}", v)
    return s

# --------------------------------------------------------------------------- descriptors (C24)

def load_descriptors():
    cid = "C24"
    found = {}
    if not os.path.isdir(P("apps")):
        out("SKIP", cid, "apps/", "no descriptors directory")
        return found
    if not exists(C["descriptors"]["schema"]):
        out("FAIL", cid, C["descriptors"]["schema"], "the descriptor schema is missing")
    for path in sorted(glob.glob(os.path.join(P("apps"), "*.yaml"))):
        rel = os.path.relpath(path, ROOT)
        stem = os.path.basename(rel)[:-5]
        errors = []
        try:
            with open(path, encoding="utf-8") as f:
                d = yaml.safe_load(f)
        except yaml.YAMLError as e:
            out("FAIL", cid, rel, "YAML does not parse: " + str(e).splitlines()[0])
            continue
        if not isinstance(d, dict):
            out("FAIL", cid, rel, "a descriptor is a mapping")
            continue
        if d.get("schema") != C["descriptors"]["schemaVersion"]:
            errors.append(f"schema must be {C['descriptors']['schemaVersion']}")
        if d.get("name") != stem:
            errors.append(f"name '{d.get('name')}' must equal the file name '{stem}'")
        if not re.match(C["descriptors"]["appPattern"], str(d.get("name", ""))):
            errors.append("name does not match the app slug rule")
        verdict(cid, rel, errors, "descriptor parses; name and schema version agree (full validation: validate-all.sh onboarding)")
        if not errors:
            found[stem] = d
    fixture = C["descriptors"]["fixtureApp"]
    if fixture not in found:
        out("FAIL", cid, f"apps/{fixture}.yaml", "the conformance fixture descriptor is missing")
    return found


APPS = load_descriptors()


def app_envs(d):
    return [e for e in ENVS if e in (d.get("environments") or ENVS)]


def deployables(d):
    return [dep for dep in (d.get("deployables") or []) if isinstance(dep, dict)]


def namespace(app, env, part=None):
    return f"{app}-{part}-{env}" if part else f"{app}-{env}"

# --------------------------------------------------------------------------- C02 annotations

def c02_annotations():
    cid = "C02"
    chart = C["argocd"]["tenantChart"]
    if not exists(chart):
        absent(cid, chart + "/Chart.yaml", "tenant chart")
    else:
        t = "\n".join(code_text(r) or "" for r in walk(chart, exts=None))
        missing = [k for k in C["argocd"]["annotations"]["keys"] if k not in t]
        verdict(cid, chart + "/", [f"never renders {k}" for k in missing], "the tenant chart renders the Octopus annotations")
    hits = []
    for root in PLATFORM_CONFIG_ROOTS:
        for rel in walk(root, exts=None):
            if rel.endswith(".md"):
                continue
            t = code_text(rel) or ""
            for key in C["argocd"]["annotations"]["forbidden"]:
                if key in t:
                    hits.append(f"{rel}: {key}")
    verdict(cid, "-", hits, "no argo.octopus.com/tenant annotation anywhere (ADR-C8)")

# --------------------------------------------------------------------------- C03 ApplicationSet apps

def c03_appset():
    cid = "C03"
    spec = C["argocd"]["applicationSet"]
    for tier in TIERS:
        rel = x(spec["file"], tier=tier)
        if not exists(rel):
            absent(cid, rel, f"ApplicationSet {spec['name']} ({tier})")
            continue
        errors = []
        appsets = [d for d in docs(rel, cid) or [] if isinstance(d, dict) and d.get("kind") == "ApplicationSet"]
        a = next((d for d in appsets if g(d, ["metadata", "name"]) == spec["name"]), None)
        if a is None:
            out("FAIL", cid, rel, f"no ApplicationSet named {spec['name']}")
            continue
        gens = [gd for gd in all_dicts(g(a, ["spec", "generators"], [])) if "git" in gd]
        files = [f.get("path") for gd in gens for f in (g(gd, ["git", "files"], []) or []) if isinstance(f, dict)]
        if "apps/*.yaml" not in files:
            errors.append(f"git files generator must read apps/*.yaml (found {files})")
        values = [g(gd, ["git", "values"], {}) or {} for gd in gens]
        if not any(str(v.get("cluster")) == tier for v in values):
            errors.append(f"generator values must set cluster: {tier}")
        sync = g(a, ["spec", "syncPolicy"], {}) or {}
        if sync.get("applicationsSync") != spec["applicationsSync"]:
            errors.append(f"syncPolicy.applicationsSync must be {spec['applicationsSync']}")
        if sync.get("preserveResourcesOnDeletion") is not True:
            errors.append("syncPolicy.preserveResourcesOnDeletion must be true")
        tmpl = yaml.safe_dump(g(a, ["spec", "template"], {}) or {}) + str(g(a, ["spec", "templatePatch"], "") or "")
        if "platform-tenants" not in tmpl:
            errors.append("the template's project must be platform-tenants")
        if "tenant-" not in tmpl:
            errors.append("the template must name Applications tenant-<app>")
        verdict(cid, rel, errors, f"ApplicationSet {spec['name']} renders one tenant per descriptor on {tier}")

# --------------------------------------------------------------------------- C04 AppProjects

def c04_projects():
    cid = "C04"
    allowed = set(C["argocd"]["appProjects"]["platform"])
    for tier in TIERS:
        rel = f"argocd/clusters/{tier}/projects.yaml"
        if not exists(rel):
            absent(cid, rel, "AppProjects")
            continue
        projects = [d for d in docs(rel, cid) or [] if isinstance(d, dict) and d.get("kind") == "AppProject"]
        names = {g(d, ["metadata", "name"]) for d in projects}
        errors = [f"missing AppProject {n}" for n in sorted(allowed - names)]
        errors += [f"AppProject {n} is not a platform project (the tenant chart renders app-<app>)" for n in sorted(names - allowed)]
        dflt = next((d for d in projects if g(d, ["metadata", "name"]) == "default"), None)
        if dflt is not None and (g(dflt, ["spec", "sourceRepos"]) or g(dflt, ["spec", "destinations"])):
            errors.append("AppProject default must stay locked (no sourceRepos, no destinations)")
        verdict(cid, rel, errors, f"platform AppProjects {sorted(allowed)} only")

# --------------------------------------------------------------------------- C05 platform namespaces

def c05_namespaces():
    cid = "C05"
    bootstrap = set(C["kubernetes"].get("bootstrapNamespaces", []))
    want = [n for n in C["kubernetes"]["platformNamespaces"] if n not in bootstrap]
    label = C["kubernetes"]["namespaceLabels"]["platform"]
    app_ns = re.compile(r"^(" + "|".join(map(re.escape, APPS)) + r")-") if APPS else None
    for tier in TIERS:
        rel = f"argocd/clusters/{tier}/namespaces.yaml"
        if not exists(rel):
            absent(cid, rel, "platform namespaces")
            continue
        found = {g(d, ["metadata", "name"]): d for d in docs(rel, cid) or [] if isinstance(d, dict) and d.get("kind") == "Namespace"}
        errors = [f"missing namespace {n}" for n in want if n not in found]
        for n, d in found.items():
            labels = g(d, ["metadata", "labels"], {}) or {}
            if n in want and any(str(labels.get(k)) != str(v) for k, v in label.items()):
                errors.append(f"{n} lacks label tier: platform")
            if app_ns and app_ns.match(str(n)):
                errors.append(f"{n} is an app namespace; the tenant chart creates app namespaces")
        verdict(cid, rel, errors, "platform namespaces present and labelled; no app namespace")

# --------------------------------------------------------------------------- C06 bootstrap

def c06_bootstrap():
    cid = "C06"
    root = C["argocd"]["rootApplication"]
    for tier in TIERS:
        rel = x(root["valuesFile"], tier=tier)
        t = code_text(rel)
        if t is None:
            absent(cid, rel, "root application values")
        else:
            errors = [f"missing {s}" for s in (root["name"], x(root["path"], tier=tier)) if s not in t]
            verdict(cid, rel, errors, f"{root['name']} points at {x(root['path'], tier=tier)}")
        rel = x(C["argocd"]["bootstrapValues"], tier=tier)
        t = code_text(rel)
        if t is None:
            absent(cid, rel, "Argo CD bootstrap values")
            continue
        errors = []
        if not re.search(r"timeout\.reconciliation:\s*\"?120s", t):
            errors.append("timeout.reconciliation must be 120s")
        if not re.search(r"admin\.enabled:\s*\"?false", t):
            errors.append("admin.enabled must be false")
        if not re.search(r"accounts\.octopus:\s*\"?apiKey", t):
            errors.append("accounts.octopus must be apiKey")
        for policy in C["argocd"]["gatewayPolicies"]:
            if policy not in t:
                errors.append(f"RBAC lacks '{policy}'")
        verdict(cid, rel, errors, "bootstrap values match §7.0 (reconciliation, admin off, octopus account and app-*/* policies)")

# --------------------------------------------------------------------------- C09 rendered app overlays

CLUSTER_SCOPED = {"Namespace", "ClusterRole", "ClusterRoleBinding", "CustomResourceDefinition", "StorageClass",
                  "PersistentVolume", "ValidatingWebhookConfiguration", "MutatingWebhookConfiguration",
                  "ClusterSecretStore", "ClusterPolicy", "ClusterIssuer", "PriorityClass", "ClusterExternalSecret",
                  "ImageValidatingPolicy", "ValidatingPolicy", "ValidatingAdmissionPolicy", "APIService"}
DENIED_NAMESPACED = {"SecretStore", "ResourceQuota", "LimitRange", "NetworkPolicy", "Policy", "PolicyException"}


def render_dir(rel):
    try:
        r = subprocess.run([KUSTOMIZE, "build", P(rel)], capture_output=True, text=True, timeout=180)
    except (OSError, subprocess.TimeoutExpired) as ex:
        return None, str(ex)
    if r.returncode != 0:
        return None, (r.stderr.strip().splitlines() or ["kustomize failed"])[-1]
    try:
        return [d for d in yaml.safe_load_all(r.stdout) if isinstance(d, dict)], None
    except yaml.YAMLError as e:
        return None, "rendered YAML does not parse: " + str(e).splitlines()[0]


def c09_rendered():
    cid = "C09"
    if not RENDER or not KUSTOMIZE:
        out("SKIP" if not RENDER else "WARN", cid, "gitops/apps/", "kustomize not available; rendered overlays not checked")
        return
    registry = C["azure"]["registry"]["loginServer"]
    for app, d in sorted(APPS.items()):
        targets = []
        for env in app_envs(d):
            for dep in deployables(d):
                if dep.get("packaging") == "kustomize":
                    targets.append((f"gitops/apps/{app}/envs/{env}/{dep.get('name')}", env, namespace(app, env, dep.get("part"))))
            if d.get("database"):
                targets.append((f"gitops/apps/{app}/envs/{env}/db", env, namespace(app, env)))
        for rel, env, ns in targets:
            if not os.path.isfile(P(rel + "/kustomization.yaml")):
                continue
            rendered, err = render_dir(rel)
            if rendered is None:
                out("FAIL", cid, rel, f"kustomize build failed: {err}")
                continue
            errors = []
            for doc in rendered:
                kind = str(doc.get("kind"))
                name = g(doc, ["metadata", "name"])
                api = str(doc.get("apiVersion", ""))
                if kind in CLUSTER_SCOPED:
                    errors.append(f"{kind}/{name} is cluster-scoped (AppProject app-{app} denies it)")
                elif kind in DENIED_NAMESPACED or "kyverno.io" in api:
                    errors.append(f"{kind}/{name} is platform-owned (AppProject app-{app} denies it)")
                elif g(doc, ["metadata", "namespace"]) not in (None, ns):
                    errors.append(f"{kind}/{name} targets namespace {g(doc, ['metadata', 'namespace'])}, not {ns}")
                if kind == "ExternalSecret":
                    ref = g(doc, ["spec", "secretStoreRef"], {}) or {}
                    if ref.get("kind") != "ClusterSecretStore" or ref.get("name") != f"{app}-{env}":
                        errors.append(f"ExternalSecret/{name} must read ClusterSecretStore {app}-{env} (found {ref.get('kind')} {ref.get('name')})")
                for dd in all_dicts(doc):
                    image = dd.get("image")
                    if isinstance(image, str):
                        if image.endswith(":latest") or ":latest@" in image:
                            errors.append(f"{kind}/{name} uses the tag latest")
                        if image.startswith(registry + "/") and not image.startswith(f"{registry}/apps/{app}/") and not image.startswith(f"{registry}/platform/"):
                            errors.append(f"{kind}/{name} uses {image}, outside apps/{app}/")
            verdict(cid, rel, sorted(set(errors)), f"renders into {ns} inside the app fence")

# --------------------------------------------------------------------------- C10 database endpoints

SERVER = re.compile(r"(?:Server|Data Source)\s*=\s*(?:tcp:)?([A-Za-z0-9.<>{}_-]+)", re.I)


def c10_database_endpoints():
    cid = "C10"
    if not os.path.isdir(P("gitops/apps")):
        out("SKIP", cid, "gitops/apps/", "no app desired state yet")
        return
    for app in sorted(APPS):
        if not os.path.isdir(P(f"gitops/apps/{app}")):
            continue
        errors, warnings = [], []
        for rel in walk(f"gitops/apps/{app}"):
            for m in SERVER.finditer(code_text(rel) or ""):
                host = m.group(1).split(",")[0]
                labels = host.split(".")
                if host.startswith(("<", "$", "{")):
                    continue
                if len(labels) > 1 and not labels[1].startswith(f"{app}-") and not labels[1].startswith(("<", "$", "{")):
                    errors.append(f"{rel}: database host {host} is in another app's namespace")
                elif labels[0] != "db":
                    warnings.append(f"{rel}: database host {host} is not the platform database Service db")
        verdict(cid, f"gitops/apps/{app}/", errors, "connection strings stay inside the app's own namespaces")
        if warnings and not errors:
            out("WARN", cid, f"gitops/apps/{app}/", "; ".join(warnings))

# --------------------------------------------------------------------------- C11 stores and vaults

def c11_stores():
    cid = "C11"
    for tier in TIERS:
        rel = f"argocd/clusters/{tier}/platform-secrets.yaml"
        t = code_text(rel)
        if t is None:
            absent(cid, rel, "platform secrets")
            continue
        errors = []
        tv = code_text(f"terraform/tier/{tier}.tfvars") or ""
        m = re.search(r'platform_key_vault_name\s*=\s*"([^"]+)"', tv)
        vault = m.group(1) if m and not m.group(1).startswith("<") else x("<kv-platform-{tier}>", tier=tier)
        if vault not in t:
            errors.append(f"platform secrets must come from {vault} (<kv-platform-{tier}>)")
        if re.search(r"\bkv-[a-z][a-z0-9]{2,11}-[tup]-", t):
            errors.append("names an app vault; app vaults are read only through ClusterSecretStores <app>-<env>")
        verdict(cid, rel, errors, f"platform ExternalSecrets read <kv-platform-{tier}> only")
    for app in sorted(APPS):
        if not os.path.isdir(P(f"gitops/apps/{app}")):
            continue
        errors = []
        for rel in walk(f"gitops/apps/{app}"):
            for d in docs(rel, cid) or []:
                if not isinstance(d, dict):
                    continue
                if d.get("kind") in ("SecretStore", "ClusterSecretStore"):
                    errors.append(f"{rel}: defines a {d.get('kind')}; only the tenant chart does")
                if d.get("kind") == "ExternalSecret":
                    ref = g(d, ["spec", "secretStoreRef"], {}) or {}
                    store = str(ref.get("name", ""))
                    token = re.fullmatch(r"[A-Z0-9_]+", store) is not None   # a placeholder replaced per environment
                    if not store.startswith(f"{app}-") and not token and "{" not in store and "<" not in store and "$" not in store:
                        errors.append(f"{rel}: ExternalSecret reads store {store}, not {app}-<env>")
        verdict(cid, f"gitops/apps/{app}/", errors, f"app secrets come only from stores {app}-<env>")

# --------------------------------------------------------------------------- C12 platform runbooks

def c12_runbooks():
    cid = "C12"
    rb = C["octopus"]["runbooks"]
    base = ".octopus/platform-infrastructure/runbooks"
    for r in rb["list"]:
        rel = f"{base}/{r['name']}.ocl"
        t = code_text(rel)
        if t is None:
            absent(cid, rel, f"runbook {r['name']}")
            continue
        errors = []
        for var in r.get("prompted", []):
            if var not in t:
                errors.append(f"does not prompt {var}")
        if r.get("terraformDirectory") and r["terraformDirectory"] not in t:
            errors.append(f"does not run {r['terraformDirectory']}")
        for f in rb.get("forbidden", []):
            if f["name"] == r["name"]:
                for env in f["environments"]:
                    if re.search(r"environments\s*=\s*\[[^\]]*\"" + re.escape(env) + r"\"", t):
                        errors.append(f"is scoped to {env} (forbidden)")
        verdict(cid, rel, errors, f"{r['name']} matches §7.0")
    if os.path.isdir(P(base)):
        for name in rb.get("retired", []):
            rel = f"{base}/{name}.ocl"
            if exists(rel):
                out("FAIL", cid, rel, f"retired runbook {name} still present")

# --------------------------------------------------------------------------- C15 Octopus Terraform

def c15_octopus_terraform():
    cid = "C15"
    files = walk("octopus/terraform", exts=(".tf",))
    if not files:
        absent(cid, "octopus/terraform/main.tf", "Octopus Terraform")
        return
    t = "\n".join(code_text(r) or "" for r in files)
    o = C["octopus"]
    want = [lc["name"] for lc in o["lifecycles"]] + [s["name"] for s in o["libraryVariableSets"]]
    want += [f["name"] for f in o["feeds"] if f["name"] != "built-in"] + o["stepTemplates"] + [o["freeze"]["name"]]
    want += [o["projectGroups"]["platform"], "Platform.InterventionTestMode", "Platform.SoDMode"] + o["teams"]
    want += [tr["name"] for tr in o["triggers"]]
    errors = [f"never names {w}" for w in want if w not in t]
    if not re.search(r"azure-platform-lifecycle-", t):
        errors.append("never names the accounts azure-platform-lifecycle-<tier>")
    if "apps/*.yaml" not in t:
        errors.append("no for_each over apps/*.yaml (project shells come from the descriptors)")
    if not re.search(r"app-\$\{", t):
        errors.append("no project group app-${...} per descriptor")
    verdict(cid, "octopus/terraform/", errors, "space objects and per-app shells match §7.0")

# --------------------------------------------------------------------------- C18 signer and Kyverno

def c18_signer():
    cid = "C18"
    chart = C["argocd"]["tenantChart"]
    if not exists(chart):
        absent(cid, chart + "/Chart.yaml", "tenant chart (per-app signer policy)")
    else:
        t = "\n".join(text(r) or "" for r in walk(chart, exts=None))
        s = C["registry"]["signer"]
        errors = [f"signer template lacks {needle}" for needle in
                  (s["kind"], s["issuer"], "release(-[a-z0-9-]+)?", "[0-9a-f]{24}", "Audit") if needle not in t]
        if "Deny" not in t and "Enforce" not in t:
            errors.append("signer template never enforces (Deny or Enforce in prod)")
        verdict(cid, chart + "/", errors, "per-app signer policy follows §7.0 (issuer, subject pattern, Enforce and Audit)")
    files = walk("policies/kyverno")
    if not files:
        absent(cid, "policies/kyverno/base/kustomization.yaml", "Kyverno policies")
        return
    t = "\n".join(code_text(r) or "" for r in files)
    errors = []
    if "MSSQL_PID" not in t:
        errors.append("no SQL Server edition rule (require-mssql-express)")
    if "apps/" not in t:
        errors.append("no registry-path rule for apps/<app>/")
    if re.search(r"CF_RELEASE_PIPELINE_ID|CF_PREVIEW_PIPELINE_ID", t):
        errors.append("retired pipeline-ID placeholders (the tenant chart renders signer subjects)")
    verdict(cid, "policies/kyverno/", errors, "generic policies present; per-app signers left to the tenant chart")

# --------------------------------------------------------------------------- C19 Terraform layers

def c19_terraform():
    cid = "C19"
    if not os.path.isdir(P("terraform")):
        out("SKIP", cid, "terraform/", "directory absent")
        return
    runbooks = "\n".join(code_text(r) or "" for r in walk(".octopus/platform-infrastructure", exts=None))
    apps_tf = "\n".join(code_text(r) or "" for r in walk("terraform/apps", exts=(".tf", ".hcl")))
    for layer in C["azure"]["terraform"]["layers"]:
        path = layer["path"]
        if not path.startswith("terraform/"):
            continue
        files = walk(path, exts=(".tf",))
        if not files:
            out("FAIL", cid, path + "/", "layer missing")
            continue
        t = "\n".join(code_text(r) or "" for r in files)
        everything = "\n".join(text(r) or "" for r in walk(path, exts=None)) + runbooks
        errors = []
        if "azurerm_mssql_" in t or "azurerm_sql_" in t:
            errors.append("declares Azure SQL; databases run in pods (ADR-IR34)")
        if path == "terraform/apps/tier":
            if "sha1(" not in apps_tf:
                errors.append("vault name formula sha1(...) not found under terraform/apps (kv-<app>-<e>-<hash4>)")
            if "disk-" not in apps_tf:
                errors.append("database disks disk-<app>-<env>-db not found under terraform/apps")
        verdict(cid, path + "/", errors, "layer present")
        key = layer["state"].split("{")[0]
        if key not in everything:
            out("WARN", cid, path + "/", f"state key {layer['state']} not found in the layer or the runbooks (backend configuration)")

# --------------------------------------------------------------------------- C20 retired paths and names

def c20_retired():
    cid = "C20"
    for r in C["retiredPaths"]:
        if exists(r["path"]):
            out("FAIL", cid, r["path"], f"retired path still present; now {r['now']} ({r['package']})")
        else:
            out("PASS", cid, r["path"], f"retired; now {r['now']}")
    roots = PLATFORM_CONFIG_ROOTS + ["scripts", "apps", "catalogue", "tests", "tools"]
    marker = C.get("nameLintAllowMarker", "name-lint: allow")
    app_scoped = re.compile(r"^(apps/|fixtures/|(codefresh|\.octopus|gitops|containers)/apps/)")
    patterns = [(n, re.compile(r"(?<![A-Za-z0-9_-])" + re.escape(n)), False) for n in C["retiredNames"]]
    patterns += [(n, re.compile(r"(?<![A-Za-z0-9_-])" + re.escape(n)), True) for n in C.get("retiredDisplayNames", [])]
    for root in roots:
        # Findings grouped by the owning package (§11.10), so each package sees its own files.
        hits = {}
        for rel in walk(root, exts=None):
            if rel.endswith(".md") or rel == "scripts/checks/consistency.sh":
                continue
            t = code_text(rel)
            if not t:
                continue
            t = "\n".join(line for line in t.splitlines() if marker not in line)
            for name, rx, display in patterns:
                if display and app_scoped.match(rel):
                    continue
                if rx.search(t):
                    hits.setdefault(owner(rel), []).append((rel, name))
        for pkg in sorted(hits):
            found = sorted(set(hits[pkg]))
            status = "WARN" if root in ("catalogue", "tests", "tools") else "FAIL"
            out(status, cid, found[0][0], "retired names: " + "; ".join(f"{rel}: {name}" for rel, name in found[:25]))
        if not hits and os.path.isdir(P(root)):
            out("PASS", cid, root + "/", "no retired name")

# --------------------------------------------------------------------------- C21 placeholders

PLACEHOLDER = re.compile(r"<([A-Za-z0-9{][A-Za-z0-9_.{}*/ -]{0,60})>")


def expand_token(tok):
    variants = {tok}
    for env in ENVS:
        variants.add(x(tok, env=env, tier=TIER_OF[env]))
    for tier in TIERS:
        variants.add(x(tok, tier=tier))
    return variants


def c21_placeholders():
    cid = "C21"
    known = set()
    for tok in C["placeholders"] + C.get("notationTokens", []):
        known |= expand_token(tok)
    retired = set()
    for tok in C.get("retiredPlaceholders", []):
        retired |= expand_token(tok)
    patterns = [re.compile("^" + re.escape(p).replace(r"\*", r"[A-Za-z0-9._-]+") + "$") for p in C.get("placeholderPatterns", [])]

    def is_known(tok):
        return tok in known or any(p.match(tok) for p in patterns)

    for root in PLATFORM_CONFIG_ROOTS + ["apps", "fixtures", ".gitleaks.toml"]:
        rels = [root] if os.path.isfile(P(root)) else walk(root, exts=None)
        for rel in rels:
            # XML project files hold tags, not placeholders.
            if rel.endswith((".md", ".sh", ".cs", ".ps1", ".tpl", ".json", ".props", ".targets", ".csproj", ".xml", ".resx", ".config")) \
                    or "/templates/" in rel:
                continue
            t = code_text(rel) or ""
            found = {m.group(0) for m in PLACEHOLDER.finditer(t)}
            bad = sorted(found & retired)
            if bad:
                out("FAIL", cid, rel, f"retired placeholders (§7.0): {bad}")
            unknown = sorted(tok for tok in found - retired if not is_known(tok))
            if unknown:
                out("WARN", cid, rel, f"placeholders not in §7.0 or the contracts: {unknown}")

# --------------------------------------------------------------------------- C22 runbook inputs

def c22_runbook_inputs():
    """Runbooks (octopus-architect) run terraform/tier or terraform/apps/tier (sre-security): every TF_VAR_<name>
    they set must be a variable of that layer (§11.10 cross-package interface)."""
    cid = "C22"
    files = walk(".octopus/platform-infrastructure/runbooks", exts=(".ocl",))
    if not files:
        absent(cid, ".octopus/platform-infrastructure/runbooks/env-apply.ocl", "platform runbooks")
        return
    layers = {"env-": "terraform/tier", "apps-": "terraform/apps/tier"}
    for rel in files:
        name = os.path.basename(rel)
        layer = next((v for k, v in layers.items() if name.startswith(k)), None)
        if layer is None:
            continue
        tf = "\n".join(code_text(r) or "" for r in walk(layer, exts=(".tf",)))
        if not tf:
            out("SKIP", cid, rel, f"{layer} absent")
            continue
        declared = set(re.findall(r'variable\s+"([A-Za-z0-9_]+)"', tf))
        used = set(re.findall(r"TF_VAR_([A-Za-z0-9_]+)", code_text(rel) or ""))
        verdict(cid, rel, [f"TF_VAR_{v} is not a variable of {layer}" for v in sorted(used - declared)], f"every TF_VAR_* is a variable of {layer}")

# --------------------------------------------------------------------------- C23 sleep and wake

def ocl_steps(t):
    return list(blocks(t, r'^\s*step\s+"([^"]+)"\s*\{'))


def c23_sleep_wake():
    cid = "C23"
    sw = C["sleepWake"]
    for rel in sw["clusterPowerFiles"]:
        t = code_text(rel)
        if t is None:
            absent(cid, rel, "sleep/wake runbook")
            continue
        errors = []
        if sw["runbookPool"] not in t and "Hosted Ubuntu" not in t:
            errors.append(f"must run on {sw['runbookPool']} (never an in-cluster pool)")
        if rel.endswith("env-sleep.ocl"):
            for var in ("Sleep.Force", "Sleep.DryRun", "Sleep.NowOverride"):
                if var not in t:
                    errors.append(f"does not prompt {var}")
        verdict(cid, rel, errors, "sleep/wake runbook on the dynamic pool")
    wp = sw["wakeProject"]
    t = code_text(wp["process"])
    if t is None:
        absent(cid, wp["process"], "platform-wake process")
    else:
        steps = ocl_steps(t)
        errors = []
        if [s for s, _ in steps] != [wp["step"]]:
            errors.append(f"must have exactly one step, {wp['step']} (found {[s for s, _ in steps]})")
        reads = sorted({v for v in re.findall(r"#\{([A-Za-z][A-Za-z0-9_.\[\]-]*)", t) if not v.startswith(("PlatformWake.", "Octopus."))})
        if reads:
            errors.append(f"reads {reads}; platform-wake reads only PlatformWake.* and system variables (decision 17)")
        verdict(cid, wp["process"], errors, "platform-wake: one step, namespaced variables only")
    apps_root = ".octopus/apps"
    if not os.path.isdir(P(apps_root)):
        out("SKIP", cid, apps_root + "/", "no app projects yet")
        return
    for rel in walk(apps_root, exts=(".ocl",)):
        t = code_text(rel) or ""
        name = os.path.basename(rel)
        if name == "deployment_process.ocl":
            steps = ocl_steps(t)
            if not (IN_CLUSTER_POOL.search(t) or "ArgoCDUpdateImageTags" in t or "platform-pin-writer" in t):
                continue
            first = steps[0][1] if steps else ""
            ok = "Octopus.DeployRelease" in first and "platform-wake" in first and "Always" in first
            verdict(cid, rel, [] if ok else ["the first step must be a Deploy a Release of platform-wake with condition Always"],
                    "step 0 wakes the cluster through platform-wake")
        elif "/runbooks/" in rel and IN_CLUSTER_POOL.search(t):
            verdict(cid, rel, [] if "Wake.WaitMinutes" in t else ["an in-cluster runbook needs the keyless wait guard (Wake.WaitMinutes)"],
                    "waits for a sleeping cluster")

# --------------------------------------------------------------------------- C25 Codefresh handshake

def c25_codefresh():
    cid = "C25"
    cf = C["codefresh"]
    platform_contexts = {c["name"] for c in cf["contexts"]}
    specs = [r for r in walk("codefresh") if "/specs/" in r and "/templates/" not in r]
    if not specs:
        absent(cid, "codefresh/platform/specs/env-checks.yml", "Codefresh specs")
    for rel in specs:
        d = next(iter(docs(rel, cid) or []), None)
        if not isinstance(d, dict):
            continue
        errors = []
        app = rel.split("/")[2] if rel.startswith("codefresh/apps/") else None
        for trig in g(d, ["spec", "triggers"], []) or []:
            if isinstance(trig, dict) and trig.get("pullRequestAllowForkEvents") is True:
                errors.append(f"trigger {trig.get('name')} allows fork events")
        for ctx in g(d, ["spec", "contexts"], []) or []:
            ctx = str(ctx)
            if app:
                if ctx not in ("platform-octopus", "platform-registry") and not ctx.startswith(f"app-{app}-"):
                    errors.append(f"context {ctx} is not for app pipelines (platform-octopus, platform-registry, app-{app}-*)")
                if ctx == "platform-octopus" and not re.search(r"/release(-[a-z0-9-]+)?$", str(g(d, ["metadata", "name"], ""))):
                    errors.append("platform-octopus is attached to release pipelines only")
            elif ctx.startswith("app-") or ctx not in platform_contexts:
                errors.append(f"context {ctx} is not a platform context")
        verdict(cid, rel, errors, "fork events off; contexts in their lane")
    step = cf["releaseStep"]
    cli = step.get("cli") or {}
    cli_command = cli.get("command", "octopus release create")
    cli_re = re.compile(re.escape(cli_command) + r"\b")

    def release_errors(d):
        """M3 in both forms of the handoff: the typed step and the Octopus CLI command."""
        errors = []
        for dd in all_dicts(d):
            if str(dd.get("type", "")).startswith(step["type"]):
                args = dd.get("arguments") or {}
                for need in step["required"]:
                    if need not in args:
                        errors.append(f"octopus_release lacks explicit {need} (M3)")
                for bad in step["forbidden"]:
                    if bad in args:
                        errors.append(f"octopus_release passes {bad} (M3)")
        for s in all_strings(d):
            if not cli_re.search(s):
                continue
            # One command per match: join backslash continuations, then stop at the end of that command.
            joined = re.sub(r"\\[ \t]*\n[ \t]*", " ", s)
            for command in cli_re.split(joined)[1:]:
                command = command.split("\n", 1)[0]
                flags = {f.split("=", 1)[0].lower() for f in re.findall(r"(?:^|\s)(--[A-Za-z][A-Za-z0-9=-]*)", command)}
                for need in cli.get("required", []):
                    if need.lower() not in flags:
                        errors.append(f"'{cli_command}' lacks {need}: packages are explicit (M3)")
                for bad in cli.get("forbidden", []):
                    if bad.lower() in flags:
                        errors.append(f"'{cli_command}' passes {bad}: no default package version (M3)")
        return errors

    for app in sorted(APPS):
        for rel in walk(f"codefresh/apps/{app}/pipelines"):
            d = next(iter(docs(rel, cid) or []), None)
            if not isinstance(d, dict):
                continue
            errors = []
            preview = os.path.basename(rel).startswith("preview")
            prefix = f"apps-previews/{app}/" if preview else f"apps/{app}/"
            for dd in all_dicts(d):
                name = dd.get("image_name")
                if isinstance(name, str) and not name.startswith(prefix) and "${{" not in name:
                    errors.append(f"image_name {name} is outside {prefix} (M1)")
            errors += release_errors(d)
            verdict(cid, rel, errors, "handshake M1 and M3 hold")
    # Starters carry the same handshake, with tokens in place of the app (scaffolded once, then owned).
    starter_prefixes = tuple(f"apps{p}/{t}/" for p in ("", "-previews") for t in ("<app>", "__APP__"))
    for rel in walk("codefresh/templates"):
        if "/pipelines/" not in rel:
            continue
        d = next(iter(docs(rel, cid) or []), None)
        if not isinstance(d, dict):
            continue
        errors = []
        for dd in all_dicts(d):
            name = dd.get("image_name")
            if isinstance(name, str) and not name.startswith(starter_prefixes) and "${{" not in name:
                errors.append(f"image_name {name} is outside apps/<app>/ (M1)")
        errors += release_errors(d)
        verdict(cid, rel, errors, "starter keeps handshake M1 and M3")

# --------------------------------------------------------------------------- main

for check in (c02_annotations, c03_appset, c04_projects, c05_namespaces, c06_bootstrap, c09_rendered,
              c10_database_endpoints, c11_stores, c12_runbooks, c15_octopus_terraform, c18_signer, c19_terraform,
              c20_retired, c21_placeholders, c22_runbook_inputs, c23_sleep_wake, c25_codefresh):
    try:
        check()
    except Exception as ex:  # a broken check must not hide the others
        out("FAIL", "C99", "-", f"{check.__name__} crashed: {type(ex).__name__}: {ex}")

print(f"consistency: {COUNTS['PASS']} pass, {COUNTS['FAIL']} fail, {COUNTS['WARN']} warn, {COUNTS['SKIP']} skip")
sys.exit(1 if COUNTS["FAIL"] else 0)
PYEOF
