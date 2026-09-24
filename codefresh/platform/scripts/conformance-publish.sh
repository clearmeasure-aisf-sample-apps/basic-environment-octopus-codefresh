#!/usr/bin/env bash
# Pushes a conformance run's results (TRX, summary.md, summary.json) to branch
# conformance-results of <sandbox-app-repo>, folder results/<UTC date>-<run id>/ (ADR-IR34
# "Reporting"): history without any write to the environment repo. Creates the branch (orphan)
# on first use. Never fails the build: a publishing problem is a warning.
#
# Usage: conformance-publish.sh <results folder>
# Environment: GITHUB_TOKEN (platform-conformance), SANDBOX_APP_REPO, PLATFORM_RUN_ID.
set -uo pipefail

warn() {
  printf 'conformance-publish.sh: WARN %s\n' "$1" >&2
  exit 0
}

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=codefresh/platform/scripts/sandbox-git.sh
. "$script_dir/sandbox-git.sh"
sandbox_require || warn "nothing published"

source_dir="${1:-}"
[ -d "$source_dir" ] || warn "no results folder '$source_dir'"
run_id="${PLATFORM_RUN_ID:-unknown}"
target="results/$(date -u +%Y-%m-%d)-${run_id}"

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
if sandbox_git clone --quiet --depth 1 --branch conformance-results "$(sandbox_url)" "$work/results" 2>/dev/null; then
  cd "$work/results" || warn "clone folder missing"
else
  sandbox_git clone --quiet --depth 1 "$(sandbox_url)" "$work/results" || warn "clone failed"
  cd "$work/results" || warn "clone folder missing"
  git checkout --quiet --orphan conformance-results
  git rm -rf --quiet . >/dev/null 2>&1 || true
  printf '# Conformance results\n\nOne folder per run of platform-env/conformance*: TRX files and the summaries.\n' >README.md
  git add README.md
fi

mkdir -p "$target"
find "$source_dir" -maxdepth 3 -type f \( -name '*.trx' -o -name 'summary.md' -o -name 'summary.json' \) -exec cp {} "$target/" \;
git add "$target"
sandbox_git commit --quiet -m "conformance ${run_id}: results" || warn "nothing to commit"
sandbox_git push --quiet origin HEAD:refs/heads/conformance-results || warn "push failed"
printf 'conformance-publish.sh: results in %s, branch conformance-results, %s\n' "$SANDBOX_APP_REPO" "$target" >&2
