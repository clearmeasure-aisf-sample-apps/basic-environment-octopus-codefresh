#!/usr/bin/env bash
# Supply-chain evidence for release images (ADR-D11, contract §7.5):
#   1. SBOM: Syft (SPDX JSON) per image, attached with
#      `cosign attest --type spdxjson` (keyless).
#   2. Provenance: a SLSA v1 predicate per image, attached with
#      `cosign attest --type slsaprovenance1` (keyless). It is step-authored:
#      the pipeline step, not a hardened isolated builder, writes it, so it
#      claims SLSA Build L2 at most. Kyverno keeps provenance in Audit (ADR-D11).
#   3. Tag lock: every --tag of every image gets write-enabled=false in ACR,
#      so no later push can move a released tag (ADR-C7, ADR-D11).
# Image signing itself happens in the Codefresh build steps (`cosign.sign: true`).
#
# Inputs are digests: --image <registry>/<repo>@sha256:<digest> (repeatable).
# All attestation work targets the digest; tags are only locked.
#
# Credentials:
#   - Registry: a Docker config at $DOCKER_CONFIG/config.json with an `auths`
#     entry for --registry (repository-scoped ACR token, §5.2). Syft, cosign
#     and the tag lock all use it. In a release pipeline the supply_chain step
#     writes it from ACR_TOKEN_NAME and ACR_TOKEN_PASSWORD of the secret context
#     platform-registry (ADR-IR10; token cf-apps-release).
#   - Sigstore: a fresh Codefresh OIDC ID token per attestation (they expire
#     after 5 minutes, E15), requested exactly as the marketplace step
#     obtain-oidc-id-token 1.2.3 does: GET $CF_OIDC_REQUEST_URL?audience=sigstore
#     with header "Authorization: $CF_OIDC_REQUEST_TOKEN" -> .id_token
#     (codefresh-io/steps incubating/obtain-oidc-id-token/step.yaml).
#     [VERIFY] both variables are injected into plain freestyle steps.
#
# Provenance records two sources: the application commit that was built (the
# triggering repo at CF_REVISION) and the environment repo commit whose pipeline
# YAML and scripts built it (the checkout that holds this script,
# codefresh/apps/<app>/scripts/; PIPELINE_YAML overrides the release YAML path).
#
# Usage: supply-chain.sh --registry <acr-name>.azurecr.io --image <ref@digest> [...]
#                        --tag <tag> [...] --out <dir> [--no-lock]
#        supply-chain.sh --registry <acr-name>.azurecr.io --check-reuse --repo <repo> [...] --tag <tag> [...]
#
# Reuse check (--check-reuse), run before the image builds so that a re-run of a release for the
# same commit does not rebuild images whose tags are already locked (a push to a locked tag fails).
# Prints one word on stdout:
#   reuse  every --repo holds every --tag and each tag is locked (write-enabled false). The lock is
#          the last act of a completed supply chain, so the images are signed and attested.
#   build  no tag exists yet, or a tag exists but is still unlocked (an earlier run stopped before
#          the lock; a new push may overwrite it).
# A mixed state (some tags locked, others missing or unlocked) fails: no push can repair it.
# Requires: syft, cosign, az, jq, curl, base64; git (optional) names the pipeline commit.
# The reuse check requires only az, jq and base64.
set -euo pipefail

die() {
  printf 'supply-chain.sh: %s\n' "$1" >&2
  exit 1
}

log() {
  printf 'supply-chain.sh: %s\n' "$1" >&2
}

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly ENV_REPO_URL="https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh"
# This script lives at codefresh/apps/<app>/scripts/; the release YAML next to it names the build.
app_name="$(basename "$(dirname "$script_dir")")"
pipeline_yaml="${PIPELINE_YAML:-codefresh/apps/${app_name}/pipelines/release.yml}"

registry=""
out=""
lock=true
check_reuse=false
images=()
repos=()
tags=()

while [ "$#" -gt 0 ]; do
  case "$1" in
    --registry)
      [ "$#" -ge 2 ] || die "--registry needs a value"
      registry="$2"
      shift 2
      ;;
    --image)
      [ "$#" -ge 2 ] || die "--image needs a value"
      images+=("$2")
      shift 2
      ;;
    --tag)
      [ "$#" -ge 2 ] || die "--tag needs a value"
      tags+=("$2")
      shift 2
      ;;
    --out)
      [ "$#" -ge 2 ] || die "--out needs a directory"
      out="$2"
      shift 2
      ;;
    --no-lock)
      lock=false
      shift
      ;;
    --check-reuse)
      check_reuse=true
      shift
      ;;
    --repo)
      [ "$#" -ge 2 ] || die "--repo needs a value"
      repos+=("$2")
      shift 2
      ;;
    *)
      die "unknown argument: $1"
      ;;
  esac
done

[ -n "$registry" ] || die "--registry is required"

docker_config="${DOCKER_CONFIG:-$HOME/.docker}/config.json"

# Reads "user:password" for the registry from the Docker config (never echoed).
registry_basic_auth() {
  local auth
  auth="$(jq -r --arg r "$registry" '.auths[$r].auth // empty' "$docker_config")"
  [ -n "$auth" ] || die "no auths entry for $registry in $docker_config"
  printf '%s' "$auth" | base64 -d
}

if [ "$check_reuse" = "true" ]; then
  [ "${#repos[@]}" -gt 0 ] || die "--check-reuse needs at least one --repo"
  [ "${#tags[@]}" -gt 0 ] || die "--check-reuse needs at least one --tag"
  [ "${#images[@]}" -eq 0 ] || die "--check-reuse takes --repo, not --image"
  for tool in az jq base64; do
    command -v "$tool" >/dev/null 2>&1 || die "$tool is required"
  done
  [ -f "$docker_config" ] || die "no registry credentials at $docker_config (ACR_TOKEN_NAME and ACR_TOKEN_PASSWORD, ADR-IR10)"
  credentials="$(registry_basic_auth)"
  locked=0
  unlocked=0
  missing=0
  for repo in "${repos[@]}"; do
    for tag in "${tags[@]}"; do
      # Data-plane read with the repository-scoped token (metadata read).
      if attributes="$(az acr repository show \
        --name "${registry%%.*}" \
        --image "${repo}:${tag}" \
        --username "${credentials%%:*}" \
        --password "${credentials#*:}" \
        --query changeableAttributes.writeEnabled \
        --output tsv 2>"${TMPDIR:-/tmp}/check-reuse.err")"; then
        case "$(printf '%s' "$attributes" | tr '[:upper:]' '[:lower:]')" in
          false) locked=$((locked + 1)); log "check-reuse: ${repo}:${tag} exists and is locked" ;;
          true) unlocked=$((unlocked + 1)); log "check-reuse: ${repo}:${tag} exists and is not locked" ;;
          *) die "check-reuse: unexpected writeEnabled '$attributes' for ${repo}:${tag}" ;;
        esac
      elif grep -Eqi 'not ?found|MANIFEST_UNKNOWN|NAME_UNKNOWN|does not exist' "${TMPDIR:-/tmp}/check-reuse.err"; then
        missing=$((missing + 1))
        log "check-reuse: ${repo}:${tag} does not exist"
      else
        cat "${TMPDIR:-/tmp}/check-reuse.err" >&2
        die "check-reuse: cannot read ${repo}:${tag}; failing closed"
      fi
    done
  done
  unset credentials
  rm -f "${TMPDIR:-/tmp}/check-reuse.err"
  total=$(( ${#repos[@]} * ${#tags[@]} ))
  if [ "$locked" -eq "$total" ]; then
    printf 'reuse\n'
  elif [ "$locked" -eq 0 ]; then
    printf 'build\n'
  else
    die "check-reuse: $locked of $total tags are locked, $unlocked unlocked, $missing missing; a mixed state needs an operator (no push can overwrite a locked tag)"
  fi
  exit 0
fi

[ -n "$out" ] || die "--out is required"
[ "${#images[@]}" -gt 0 ] || die "at least one --image <ref@sha256:digest> is required"
if [ "$lock" = "true" ] && [ "${#tags[@]}" -eq 0 ]; then
  die "--tag is required unless --no-lock"
fi

for tool in syft cosign jq curl base64; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool is required"
done
if [ "$lock" = "true" ]; then
  command -v az >/dev/null 2>&1 || die "az is required for the tag lock"
fi

[ -f "$docker_config" ] || die "no registry credentials at $docker_config (ACR_TOKEN_NAME and ACR_TOKEN_PASSWORD, ADR-IR10)"

mkdir -p "$out"

# The environment repo commit that supplied the pipeline definition and scripts.
pipeline_commit="$(git -C "$script_dir" rev-parse HEAD 2>/dev/null)" || pipeline_commit="unknown"

# Returns a Sigstore-audience ID token from the Codefresh OIDC provider.
sigstore_token() {
  : "${CF_OIDC_REQUEST_URL:?CF_OIDC_REQUEST_URL is not set}"
  : "${CF_OIDC_REQUEST_TOKEN:?CF_OIDC_REQUEST_TOKEN is not set}"
  local token
  token="$(curl -fsS -H "Authorization: ${CF_OIDC_REQUEST_TOKEN}" \
    "${CF_OIDC_REQUEST_URL}?audience=sigstore" | jq -r '.id_token')"
  if [ -z "$token" ] || [ "$token" = "null" ]; then
    die "the Codefresh OIDC provider returned no id_token"
  fi
  printf '%s' "$token"
}

# Writes a step-authored SLSA v1 provenance predicate for one image digest.
write_provenance() {
  local ref="$1" file="$2"
  local now
  now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  jq -n \
    --arg repo "https://github.com/${CF_REPO_OWNER:-clearmeasure-aisf-sample-apps}/${CF_REPO_NAME:-20260923-001}" \
    --arg env_repo "$ENV_REPO_URL" \
    --arg pipeline_commit "$pipeline_commit" \
    --arg revision "${CF_REVISION:-unknown}" \
    --arg branch "${CF_BRANCH:-unknown}" \
    --arg pipeline "${CF_PIPELINE_NAME:-${app_name}/release}" \
    --arg pipeline_yaml "$pipeline_yaml" \
    --arg app "$app_name" \
    --arg build_id "${CF_BUILD_ID:-local}" \
    --arg build_url "${CF_BUILD_URL:-}" \
    --arg version "${VERSION:-unknown}" \
    --arg image "$ref" \
    --arg now "$now" \
    '{
       buildDefinition: {
         buildType: ($env_repo + "/" + $pipeline_yaml + "@v1"),
         externalParameters: {
           repository: $repo,
           ref: ("refs/heads/" + $branch),
           revision: $revision,
           pipeline: $pipeline,
           workflow: { repository: $env_repo, ref: "refs/heads/main", path: $pipeline_yaml },
           version: $version
         },
         internalParameters: {
           provenanceAuthor: ("step-authored (codefresh/apps/" + $app + "/scripts/supply-chain.sh)"),
           slsaBuildLevelClaim: "L2 at most"
         },
         resolvedDependencies: [
           { uri: ("git+" + $repo + "@refs/heads/" + $branch), digest: { gitCommit: $revision } },
           { uri: ("git+" + $env_repo + "@refs/heads/main"), digest: { gitCommit: $pipeline_commit } }
         ]
       },
       runDetails: {
         builder: { id: ("https://g.codefresh.io/pipelines/" + $pipeline) },
         metadata: { invocationId: $build_url, startedOn: $now, finishedOn: $now },
         byproducts: [ { name: "image", uri: $image }, { name: "codefresh-build-id", content: $build_id } ]
       }
     }' >"$file"
}

index=0
for ref in "${images[@]}"; do
  case "$ref" in
    "$registry"/*@sha256:*) ;;
    *) die "--image must be <registry>/<repo>@sha256:<digest> on $registry: $ref" ;;
  esac
  index=$((index + 1))
  name="${ref#"$registry"/}"
  name="${name%@*}"
  safe_name="$(printf '%s' "$name" | tr '/' '_')"

  sbom="$out/${safe_name}.spdx.json"
  provenance="$out/${safe_name}.provenance.json"

  log "[$index/${#images[@]}] SBOM for $ref"
  syft scan "registry:${ref}" -o "spdx-json=${sbom}"

  # A fresh token per attestation; assigned first so that set -e stops on failure.
  log "[$index/${#images[@]}] attesting SBOM"
  token="$(sigstore_token)"
  cosign attest --yes --type spdxjson --predicate "$sbom" \
    --identity-token "$token" "$ref"

  log "[$index/${#images[@]}] attesting step-authored provenance"
  write_provenance "$ref" "$provenance"
  token="$(sigstore_token)"
  cosign attest --yes --type slsaprovenance1 --predicate "$provenance" \
    --identity-token "$token" "$ref"
  unset token

  if [ "$lock" = "true" ]; then
    credentials="$(registry_basic_auth)"
    for tag in "${tags[@]}"; do
      log "[$index/${#images[@]}] locking ${name}:${tag}"
      # Data-plane call with the repository-scoped token (metadata write) [VERIFY token scope].
      az acr repository update \
        --name "${registry%%.*}" \
        --image "${name}:${tag}" \
        --write-enabled false \
        --delete-enabled false \
        --username "${credentials%%:*}" \
        --password "${credentials#*:}" \
        --output none
    done
    unset credentials
  fi
done

log "done: ${#images[@]} image(s); evidence in $out"
