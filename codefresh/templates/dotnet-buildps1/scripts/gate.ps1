#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Aggregates the gate step results with the semantics of GitHub Actions `build-result`.

.DESCRIPTION
    The semantics of docs/ci-single-gate.md in the application repo (contract §7.7):
      - CODE_CHANGED=false (a docs-only change set): pass without inspecting the gates.
      - Otherwise every required gate must report "success". A gate that failed,
        was skipped, was terminated or never reported fails the build.
      - Advisory gates (security_scan) are reported but never fail the build.

    Each gate step writes "success" to $GATE_DIR/<gate> as its last command, so the
    marker exists only when every command of the step succeeded. Codefresh has no
    step-result variable (${{steps.<gate>.result}} stays literal text; first live
    run, 2026-09-24). A missing marker is "failure or skipped". GATE_<gate> in the
    environment still wins, for local runs and tests.

    Writes a Markdown summary to standard output and to the summary file.

    Usage: pwsh -NoProfile -File gate.ps1 [-Advisory <gate>[,<gate>...]] [-SummaryFile <file>] <gate>...

.PARAMETER Gate
    The gates, in report order: letters, digits and underscores.

.PARAMETER Advisory
    Gates that are reported but never fail the build (a comma-separated list from a shell).

.PARAMETER SummaryFile
    Where the summary goes (default: GATE_SUMMARY_FILE, else ${CF_VOLUME_PATH}/reports/gate-summary.md, or
    ./gate-summary.md locally).

.OUTPUTS
    The Markdown summary.

.NOTES
    Exit codes: 0 pass; 1 a required gate did not succeed; 2 usage (no gates, an unknown option, an invalid gate name).
#>
[CmdletBinding()]
param(
    [string[]] $Advisory = @(),
    [string] $SummaryFile = $env:GATE_SUMMARY_FILE,
    [Parameter(Position = 0, ValueFromRemainingArguments)]
    [string[]] $Gate = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Exit-Usage([string] $Message) {
    [Console]::Error.WriteLine("gate.ps1: $Message")
    exit 2
}

foreach ($name in $Gate) {
    if ($name.StartsWith('-')) {
        Exit-Usage "unknown option: $name"
    }
}
if ($Gate.Count -eq 0) {
    Exit-Usage 'no gates given'
}
$advisoryGates = @($Advisory | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })

if (-not $SummaryFile) {
    $SummaryFile = if ($env:CF_VOLUME_PATH) { "$($env:CF_VOLUME_PATH)/reports/gate-summary.md" } else { './gate-summary.md' }
}
$SummaryFile = [System.IO.Path]::GetFullPath($SummaryFile, $PWD.ProviderPath)
$summaryFolder = Split-Path -Parent $SummaryFile
if ($summaryFolder) {
    $null = New-Item -ItemType Directory -Force -Path $summaryFolder
}

$codeChanged = if ($env:CODE_CHANGED) { $env:CODE_CHANGED } else { '' }
$failed = 0
$lines = [System.Collections.Generic.List[string]]::new()

# Writes what the summary holds so far; the verdict comes from the exit code.
function Save-Summary {
    [System.IO.File]::WriteAllText($SummaryFile, (($lines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
}

$lines.Add("## Codefresh gate ($(if ($env:CF_PIPELINE_NAME) { $env:CF_PIPELINE_NAME } else { 'local' }))")
$lines.Add('')
$lines.Add('- Version: `' + $(if ($env:VERSION) { $env:VERSION } else { 'unknown' }) + '`')
$lines.Add('- Code changed: `' + $(if ($codeChanged) { $codeChanged } else { 'not reported' }) + '`')
$lines.Add('')

if ($codeChanged -ceq 'false') {
    $lines.Add('Docs-only change set: gates skipped, result **pass**.')
}
else {
    $lines.Add('| Gate | Result | Required |')
    $lines.Add('|---|---|---|')
    foreach ($name in $Gate) {
        if ($name -cmatch '[^A-Za-z0-9_]') {
            Save-Summary
            Exit-Usage "invalid gate name: $name"
        }
        $result = [Environment]::GetEnvironmentVariable("GATE_$name")
        # The literal text of a Codefresh variable that was never resolved counts as no report.
        if ($null -eq $result -or $result.Contains('${{')) {
            $result = ''
        }
        if (-not $result) {
            $marker = if ($env:GATE_DIR) { Join-Path $env:GATE_DIR $name } else { $null }
            if ($marker -and (Test-Path -LiteralPath $marker -PathType Leaf)) {
                $result = [System.IO.File]::ReadAllText($marker) -replace '[ \t\n\v\f\r]', ''
            }
            elseif ($env:GATE_DIR) {
                $result = 'failure or skipped'
            }
            else {
                $result = 'not reported'
            }
        }
        $required = if ($advisoryGates -ccontains $name) { 'advisory' } else { 'yes' }
        $lines.Add("| $name | $result | $required |")
        if ($required -eq 'yes' -and $result -cne 'success') {
            $failed = 1
        }
    }
    $lines.Add('')
    if ($failed -ne 0) {
        $lines.Add('Result: **fail**. A required gate did not succeed.')
    }
    else {
        $lines.Add('Result: **pass**. All required gates succeeded.')
    }
}

Save-Summary
Write-Output ($lines -join "`n")
exit $failed
