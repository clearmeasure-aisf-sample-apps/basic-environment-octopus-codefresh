#!/usr/bin/env bash
# Prints the platform version for HEAD (ADR-C7, contract §7.5):
#   master:          MAJOR.MINOR.<git rev-list --count --first-parent HEAD>
#   any other branch MAJOR.MINOR.<count>-ci.<sha7>   (never released)
# MAJOR and MINOR come from codefresh/workorders/version.env, next to this script in
# the environment repo. The height is counted in the application checkout
# (clearmeasure-aisf-sample-apps/20260923-001): the working directory, or --repo.
# The image tag, the Octopus package version and the Octopus release number all use
# this one string.
#
# Runnable locally and in Codefresh (the branch comes from CF_BRANCH there), from the
# application checkout:
#   bash <env-repo>/codefresh/workorders/scripts/version.sh [--branch <name>] [--repo <dir>]
#        [--version-file <file>] [--no-fetch]
#
# A count taken from a shallow clone would mint a wrong, possibly colliding
# version, so a shallow clone is unshallowed (git fetch --unshallow) or the
# script fails. --no-fetch turns the fetch off; the script then fails on a
# shallow clone instead.
set -euo pipefail

readonly MAX_PART=65534   # AssemblyVersion/FileVersion parts must be <= 65534

die() {
  printf 'version.sh: %s\n' "$1" >&2
  exit 1
}

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  cat <<'EOF'
Usage: version.sh [--branch <name>] [--repo <dir>] [--version-file <file>] [--no-fetch]
  --branch <name>        Branch to version for (default: $CF_BRANCH, else the current branch).
  --repo <dir>           Application checkout (default: git rev-parse --show-toplevel).
  --version-file <file>  MAJOR/MINOR file (default: ../version.env next to this script).
  --no-fetch             Never run git fetch --unshallow; fail on a shallow clone.
EOF
}

branch="${CF_BRANCH:-}"
repo_root=""
env_file="$script_dir/../version.env"
fetch=true

while [ "$#" -gt 0 ]; do
  case "$1" in
    --branch)
      [ "$#" -ge 2 ] || die "--branch needs a value"
      branch="$2"
      shift 2
      ;;
    --repo)
      [ "$#" -ge 2 ] || die "--repo needs a value"
      repo_root="$2"
      shift 2
      ;;
    --version-file)
      [ "$#" -ge 2 ] || die "--version-file needs a value"
      env_file="$2"
      shift 2
      ;;
    --no-fetch)
      fetch=false
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

[ -f "$env_file" ] || die "missing $env_file"
env_file="$(cd "$(dirname "$env_file")" && pwd)/$(basename "$env_file")"

if [ -z "$repo_root" ]; then
  repo_root="$(git rev-parse --show-toplevel)" || die "not inside a git repository"
fi
cd "$repo_root" || die "cannot enter $repo_root"

# Parsed, never sourced: only KEY=digits lines count.
major="$(sed -n 's/^MAJOR=\([0-9][0-9]*\)[[:space:]]*$/\1/p' "$env_file")"
minor="$(sed -n 's/^MINOR=\([0-9][0-9]*\)[[:space:]]*$/\1/p' "$env_file")"
[ -n "$major" ] || die "MAJOR is missing or not numeric in $env_file"
[ -n "$minor" ] || die "MINOR is missing or not numeric in $env_file"

shallow="$(git rev-parse --is-shallow-repository)" || die "cannot tell whether the clone is shallow"
if [ "$shallow" = "true" ]; then
  if [ "$fetch" = "true" ]; then
    printf 'version.sh: shallow clone; fetching the full history\n' >&2
    git fetch --unshallow --quiet || die "git fetch --unshallow failed"
    shallow="$(git rev-parse --is-shallow-repository)" || die "cannot tell whether the clone is shallow"
  fi
  if [ "$shallow" != "false" ]; then
    die "shallow clone: the first-parent count would be wrong; clone with full depth"
  fi
fi

height="$(git rev-list --count --first-parent HEAD)"

for part in "$major" "$minor" "$height"; do
  if [ "$part" -gt "$MAX_PART" ]; then
    die "version part $part exceeds $MAX_PART; bump MINOR in codefresh/workorders/version.env"
  fi
done

if [ -z "$branch" ]; then
  # Detached HEAD prints "HEAD", which is not master: the safe, never-released form.
  branch="$(git rev-parse --abbrev-ref HEAD)"
fi

if [ "$branch" = "master" ]; then
  printf '%s.%s.%s\n' "$major" "$minor" "$height"
else
  head_sha="$(git rev-parse HEAD)"
  printf '%s.%s.%s-ci.%s\n' "$major" "$minor" "$height" "${head_sha:0:7}"
fi
