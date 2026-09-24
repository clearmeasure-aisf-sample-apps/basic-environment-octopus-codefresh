#!/usr/bin/env bash
# Aggregates the gate step results with the semantics of GitHub Actions
# `build-result` (docs/ci-single-gate.md in the application repo, contract §7.7):
#   - CODE_CHANGED=false (a docs-only change set): pass without inspecting the gates.
#   - Otherwise every required gate must report "success". A gate that failed,
#     was skipped, was terminated or never reported fails the build.
#   - Advisory gates (security_scan) are reported but never fail the build.
#
# Usage: gate.sh [--advisory <gate>]... <gate>...
#   Each gate step writes "success" to $GATE_DIR/<gate> as its last command, so the
#   marker exists only when every command of the step succeeded. Codefresh has no
#   step-result variable (${{steps.<gate>.result}} stays literal text; first live
#   run, 2026-09-24). A missing marker is "failure or skipped". GATE_<gate> in the
#   environment still wins, for local runs and tests.
#
# Writes a Markdown summary to stdout and to $GATE_SUMMARY_FILE
# (default: ${CF_VOLUME_PATH}/reports/gate-summary.md, or ./gate-summary.md locally).
set -euo pipefail

die() {
  printf 'gate.sh: %s\n' "$1" >&2
  exit 2
}

advisory=" "
gates=()

while [ "$#" -gt 0 ]; do
  case "$1" in
    --advisory)
      [ "$#" -ge 2 ] || die "--advisory needs a gate name"
      advisory="${advisory}$2 "
      shift 2
      ;;
    -*)
      die "unknown option: $1"
      ;;
    *)
      gates+=("$1")
      shift
      ;;
  esac
done

[ "${#gates[@]}" -gt 0 ] || die "no gates given"

if [ -n "${CF_VOLUME_PATH:-}" ]; then
  default_summary="${CF_VOLUME_PATH}/reports/gate-summary.md"
else
  default_summary="./gate-summary.md"
fi
summary_file="${GATE_SUMMARY_FILE:-$default_summary}"
mkdir -p "$(dirname "$summary_file")"

code_changed="${CODE_CHANGED:-}"
failed=0

# The single quotes are intended: Markdown backticks, and the literal text ${{
# of a Codefresh variable that was never resolved.
# shellcheck disable=SC2016
{
  printf '## Codefresh gate (%s)\n\n' "${CF_PIPELINE_NAME:-local}"
  printf -- '- Version: `%s`\n' "${VERSION:-unknown}"
  printf -- '- Code changed: `%s`\n\n' "${code_changed:-not reported}"

  if [ "$code_changed" = "false" ]; then
    printf 'Docs-only change set: gates skipped, result **pass**.\n'
  else
    printf '| Gate | Result | Required |\n|---|---|---|\n'
    for gate in "${gates[@]}"; do
      case "$gate" in
        *[!A-Za-z0-9_]*) die "invalid gate name: $gate" ;;
      esac
      var="GATE_${gate}"
      result="${!var:-}"
      case "$result" in
        *'${{'*) result="" ;;
      esac
      if [ -z "$result" ]; then
        if [ -n "${GATE_DIR:-}" ] && [ -f "${GATE_DIR}/${gate}" ]; then
          result="$(tr -d '[:space:]' < "${GATE_DIR}/${gate}")"
        elif [ -n "${GATE_DIR:-}" ]; then
          result="failure or skipped"
        else
          result="not reported"
        fi
      fi
      case "$advisory" in
        *" $gate "*) required="advisory" ;;
        *) required="yes" ;;
      esac
      printf '| %s | %s | %s |\n' "$gate" "$result" "$required"
      if [ "$required" = "yes" ] && [ "$result" != "success" ]; then
        failed=1
      fi
    done
    printf '\n'
    if [ "$failed" -ne 0 ]; then
      printf 'Result: **fail**. A required gate did not succeed.\n'
    else
      printf 'Result: **pass**. All required gates succeeded.\n'
    fi
  fi
} | tee "$summary_file"

# The loop ran inside the pipeline's subshell; re-evaluate the verdict here.
if [ "$code_changed" = "false" ]; then
  exit 0
fi
for gate in "${gates[@]}"; do
  case "$advisory" in
    *" $gate "*) continue ;;
  esac
  var="GATE_${gate}"
  if [ "${!var:-}" != "success" ]; then
    exit 1
  fi
done
exit 0
