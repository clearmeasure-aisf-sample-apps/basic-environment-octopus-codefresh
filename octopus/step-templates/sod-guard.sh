#!/usr/bin/env bash
# platform-sod-guard: separation of duties and the intervention test mode for one manual intervention
# (ADR-IR34 decision 23, ADR-IR32; capabilities CAP-OCT-004 and CAP-OCT-005).
#
# octopus/terraform creates step template platform-sod-guard from this file. App processes may also inline it: the
# lines between "# >>> octopus/step-templates/sod-guard.sh" and "# <<< octopus/step-templates/sod-guard.sh" must
# equal this file (offline drift test in tests/Platform.Conformance.Offline/Octopus/). Bash only; no dollar-brace
# sequences, because OCL heredocs treat them as template syntax.
#
# Inputs, shell variables set by the step header (inline copy) or from the template parameters:
#   SODGUARD_APPROVAL_STEP  name of the manual intervention whose answer is checked, for example "Prod go/no-go"
#   SODGUARD_OTHER_STEPS    comma-separated names of other interventions of the deployment; checked only for answers
#                           by the automation user; interventions that did not run are skipped
#   SODGUARD_CHECK_CREATOR  "true": the Platform.SoDMode rule applies to the approval step
# Octopus variables, from library variable set Platform Environment (a project may override them by scope, as the
# sandbox channel Strict does for Platform.SoDMode):
#   Platform.SoDMode              single-operator (default): the deployment creator may approve, with a reason in
#                                 Notes; enforce: the creator may not approve
#   Platform.InterventionTestMode true: the automation user may answer, only with the reason
#                                 conformance:<run-id> or e2e:<run-id>; anything else: it may not answer
#   Platform.AutomationUsername   the automation user (AISF-Service-Account), compared case-insensitively
# Output variables: SodGuard.Result, SodGuard.Approver, SodGuard.Reason.
# Manual intervention outputs are addressed by step name (Octopus.Action[<step name>].Output.Manual.*, ADR-IR22).
# shellcheck disable=SC2154  # the inputs come from the step header
set -eu -o pipefail

lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
trim_first_line() { printf '%s\n' "$1" | sed -n '1{s/^[[:space:]]*//;s/[[:space:]]*$//;p;}'; }
manual_output() { get_octopusvariable "Octopus.Action[$1].Output.Manual.$2"; }

approval_step="$SODGUARD_APPROVAL_STEP"
other_steps="$SODGUARD_OTHER_STEPS"
check_creator="$(lower "$SODGUARD_CHECK_CREATOR")"
[ -n "$approval_step" ] || fail_step "SODGUARD_APPROVAL_STEP is empty; failing closed."

mode="$(lower "$(get_octopusvariable "Platform.SoDMode")")"
[ -n "$mode" ] || mode="single-operator"
case "$mode" in
    single-operator|enforce) ;;
    *) fail_step "Platform.SoDMode must be single-operator or enforce, not '$mode'." ;;
esac
test_mode="$(lower "$(get_octopusvariable "Platform.InterventionTestMode")")"
automation="$(lower "$(get_octopusvariable "Platform.AutomationUsername")")"
[ -n "$automation" ] || fail_step "Platform.AutomationUsername is empty (library variable set Platform Environment); failing closed."

# check_automation STEP USERNAME NOTES: the automation user answers only in intervention test mode, and only with a
# run reason in the first line of Notes.
check_automation() {
    if [ "$(lower "$2")" != "$automation" ]; then
        return 0
    fi
    if [ "$test_mode" != "true" ]; then
        fail_step "Separation of duties: '$1' was answered by the automation user $2 while Platform.InterventionTestMode is not true."
    fi
    automation_reason="$(trim_first_line "$3")"
    if ! printf '%s' "$automation_reason" | grep -Eq '^(conformance|e2e):[A-Za-z0-9._-]+$'; then
        fail_step "Separation of duties: the automation user answered '$1' without the reason conformance:<run-id> or e2e:<run-id> (Notes: '$automation_reason')."
    fi
    write_highlight "'$1' was answered by the automation user in intervention test mode: $automation_reason"
}

approver_id="$(manual_output "$approval_step" ResponsibleUser.Id)"
approver="$(manual_output "$approval_step" ResponsibleUser.Username)"
notes="$(manual_output "$approval_step" Notes)"
[ -n "$approver_id" ] || fail_step "No answer is recorded for '$approval_step'; failing closed."
check_automation "$approval_step" "$approver" "$notes"

other_list="$(printf '%s' "$other_steps" | tr ',' '\n')"
while IFS= read -r other; do
    other="$(trim_first_line "$other")"
    [ -n "$other" ] || continue
    other_user="$(manual_output "$other" ResponsibleUser.Username)"
    [ -n "$other_user" ] || continue
    check_automation "$other" "$other_user" "$(manual_output "$other" Notes)"
done <<< "$other_list"

reason="$(trim_first_line "$notes")"
if [ "$check_creator" = "true" ]; then
    creator_id="$(get_octopusvariable "Octopus.Deployment.CreatedBy.Id")"
    [ -n "$creator_id" ] || fail_step "Cannot read the creator of this deployment; failing closed."
    if [ "$approver_id" = "$creator_id" ]; then
        if [ "$mode" = "enforce" ]; then
            fail_step "Separation of duties (enforce): $approver created this deployment and answered '$approval_step'. Another member of the responsible team must answer."
        fi
        [ -n "$reason" ] || fail_step "Separation of duties (single-operator): $approver created this deployment and answered '$approval_step' without a reason in Notes."
        write_warning "Single-operator mode: $approver created this deployment and answered '$approval_step'. Reason: $reason"
    fi
fi

set_octopusvariable "SodGuard.Result" "passed"
set_octopusvariable "SodGuard.Approver" "$approver"
set_octopusvariable "SodGuard.Reason" "$reason"
echo "Separation of duties holds for '$approval_step' (Platform.SoDMode $mode): answered by $approver."
