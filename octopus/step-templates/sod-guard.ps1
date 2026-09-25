#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    platform-sod-guard: separation of duties and the intervention test mode for one manual intervention
    (ADR-IR34 decision 23, ADR-IR32; capabilities CAP-OCT-004 and CAP-OCT-005).

.DESCRIPTION
    octopus/terraform creates step template platform-sod-guard from this file; the parameters default to the template
    parameters SodGuard.*. App processes may also inline it: the lines between
    "# >>> octopus/step-templates/sod-guard.ps1" and "# <<< octopus/step-templates/sod-guard.ps1" must equal this file
    (offline drift test in tests/Platform.Conformance.Offline/Octopus/), inside a script block that the step calls with
    its inputs. Octopus runs it with the PowerShell script functions ($OctopusParameters, Set-OctopusVariable,
    Fail-Step, Write-Highlight). No dollar-brace or percent-brace sequences (OCL heredoc template syntax) and no
    hash-brace sequences (Octopus variable substitution).

    Octopus variables, from library variable set Platform Environment (a project may override them by scope, as the
    sandbox channel Strict does for Platform.SoDMode):
      Platform.SoDMode              single-operator (default): the deployment creator may approve, with a reason in
                                    Notes; enforce: the creator may not approve
      Platform.InterventionTestMode true: the automation user may answer, only with the reason
                                    conformance:<run-id> or e2e:<run-id>; anything else: it may not answer
      Platform.AutomationUsername   the automation user (AISF-Service-Account), compared case-insensitively
    Manual intervention outputs are addressed by step name (Octopus.Action[<step name>].Output.Manual.*, ADR-IR22).

.PARAMETER ApprovalStep
    Name of the manual intervention whose answer is checked, for example "Prod go/no-go" (SodGuard.ApprovalStep).

.PARAMETER OtherSteps
    Comma-separated names of other interventions of the deployment; checked only for answers by the automation user;
    interventions that did not run are skipped (SodGuard.OtherSteps).

.PARAMETER CheckCreator
    "true": the Platform.SoDMode rule applies to the approval step (SodGuard.CheckCreator).

.OUTPUTS
    Octopus output variables SodGuard.Result, SodGuard.Approver and SodGuard.Reason. A rule that does not hold fails
    the step (Fail-Step).
#>
[CmdletBinding()]
param(
    [string]$ApprovalStep = $OctopusParameters['SodGuard.ApprovalStep'],
    [string]$OtherSteps = $OctopusParameters['SodGuard.OtherSteps'],
    [string]$CheckCreator = $OctopusParameters['SodGuard.CheckCreator']
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandArgumentPassing = 'Standard'
$PSNativeCommandUseErrorActionPreference = $true

# The first line of a text without surrounding blanks (space, tab, CR, LF, VT, FF).
function Get-SodFirstLine([string]$Text) {
    return ($Text -split "`n", 2)[0].Trim([char[]]" `t`r`n`v`f")
}

function Get-SodManualOutput([string]$Step, [string]$Name) {
    return [string]$OctopusParameters["Octopus.Action[$Step].Output.Manual.$Name"]
}

# The automation user answers only in intervention test mode, and only with a run reason in the first line of Notes.
function Assert-SodAutomationAnswer([string]$Step, [string]$Username, [string]$Notes) {
    if ($Username.ToLowerInvariant() -cne $automation) {
        return
    }
    if ($testMode -cne 'true') {
        Fail-Step "Separation of duties: '$Step' was answered by the automation user $Username while Platform.InterventionTestMode is not true."
    }
    $automationReason = Get-SodFirstLine $Notes
    if ($automationReason -cnotmatch '^(conformance|e2e):[A-Za-z0-9._-]+$') {
        Fail-Step "Separation of duties: the automation user answered '$Step' without the reason conformance:<run-id> or e2e:<run-id> (Notes: '$automationReason')."
    }
    Write-Highlight "'$Step' was answered by the automation user in intervention test mode: $automationReason"
}

$creatorRule = $CheckCreator.ToLowerInvariant()
if (-not $ApprovalStep) {
    Fail-Step 'SodGuard.ApprovalStep (-ApprovalStep) is empty; failing closed.'
}

$mode = ([string]$OctopusParameters['Platform.SoDMode']).ToLowerInvariant()
if (-not $mode) {
    $mode = 'single-operator'
}
if ($mode -cnotin 'single-operator', 'enforce') {
    Fail-Step "Platform.SoDMode must be single-operator or enforce, not '$mode'."
}
$testMode = ([string]$OctopusParameters['Platform.InterventionTestMode']).ToLowerInvariant()
$automation = ([string]$OctopusParameters['Platform.AutomationUsername']).ToLowerInvariant()
if (-not $automation) {
    Fail-Step 'Platform.AutomationUsername is empty (library variable set Platform Environment); failing closed.'
}

$approverId = Get-SodManualOutput $ApprovalStep 'ResponsibleUser.Id'
$approver = Get-SodManualOutput $ApprovalStep 'ResponsibleUser.Username'
$notes = Get-SodManualOutput $ApprovalStep 'Notes'
if (-not $approverId) {
    Fail-Step "No answer is recorded for '$ApprovalStep'; failing closed."
}
Assert-SodAutomationAnswer $ApprovalStep $approver $notes

foreach ($item in $OtherSteps -split '[,\n]') {
    $other = Get-SodFirstLine $item
    if (-not $other) {
        continue
    }
    $otherUser = Get-SodManualOutput $other 'ResponsibleUser.Username'
    if (-not $otherUser) {
        continue
    }
    Assert-SodAutomationAnswer $other $otherUser (Get-SodManualOutput $other 'Notes')
}

$reason = Get-SodFirstLine $notes
if ($creatorRule -ceq 'true') {
    $creatorId = [string]$OctopusParameters['Octopus.Deployment.CreatedBy.Id']
    if (-not $creatorId) {
        Fail-Step 'Cannot read the creator of this deployment; failing closed.'
    }
    if ($approverId -ceq $creatorId) {
        if ($mode -ceq 'enforce') {
            Fail-Step "Separation of duties (enforce): $approver created this deployment and answered '$ApprovalStep'. Another member of the responsible team must answer."
        }
        if (-not $reason) {
            Fail-Step "Separation of duties (single-operator): $approver created this deployment and answered '$ApprovalStep' without a reason in Notes."
        }
        Write-Warning "Single-operator mode: $approver created this deployment and answered '$ApprovalStep'. Reason: $reason"
    }
}

Set-OctopusVariable -name 'SodGuard.Result' -value 'passed'
Set-OctopusVariable -name 'SodGuard.Approver' -value $approver
Set-OctopusVariable -name 'SodGuard.Reason' -value $reason
Write-Host "Separation of duties holds for '$ApprovalStep' (Platform.SoDMode $mode): answered by $approver."
