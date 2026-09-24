#!/usr/bin/env bash
# Git helpers for the conformance pipelines' writes to <sandbox-app-repo> (ADR-IR34 "Test
# harness": conformance-arm pushes the run's sandbox commits; results go to branch
# conformance-results). Sourced, not run. The token (GITHUB_TOKEN, context platform-conformance)
# reaches git through a credential helper that reads the environment at call time, never argv.
#
# Needs: SANDBOX_APP_REPO (owner/name), GITHUB_TOKEN.

sandbox_git() {
  # Single quotes on purpose: git's helper shell expands GITHUB_TOKEN when it runs.
  # shellcheck disable=SC2016
  git -c credential.helper= \
    -c 'credential.helper=!f() { echo username=x-access-token; echo "password=${GITHUB_TOKEN}"; }; f' \
    -c user.name=platform-conformance \
    -c user.email=platform-conformance@users.noreply.github.com \
    "$@"
}

# SANDBOX_GIT_URL overrides the remote (local rehearsals against a bare repository).
sandbox_url() {
  printf '%s' "${SANDBOX_GIT_URL:-https://github.com/${SANDBOX_APP_REPO:?SANDBOX_APP_REPO is not set}.git}"
}

sandbox_require() {
  : "${GITHUB_TOKEN:?GITHUB_TOKEN is not set (context platform-conformance)}"
  case "${SANDBOX_APP_REPO:-}" in
    "" | *'<'*'>'*) printf 'sandbox-git.sh: SANDBOX_APP_REPO is not set (spec variable)\n' >&2; return 1 ;;
  esac
}
