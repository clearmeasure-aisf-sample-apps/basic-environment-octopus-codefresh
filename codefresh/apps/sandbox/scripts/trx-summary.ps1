#!/usr/bin/env pwsh
#Requires -Version 7.4
# Summarises every TRX file under a folder as Markdown: one row per test run (counters) and
# the names of failed tests. TRX is the only test-result format of the platform (no JUnit):
# the pipelines keep the .trx files as build artifacts and print this summary in the log.
#
# Usage: pwsh -NoProfile -File trx-summary.ps1 -Path <folder> [-Out <file.md>] [-Title <text>]
# Exit code: 0 always; the gate step decides pass or fail from the step results.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Out = "",
    [string]$Title = "Test results (TRX)",
    [int]$MaxFailures = 50
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("## $Title")
$lines.Add("")

$files = @()
if (Test-Path -LiteralPath $Path) {
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -Filter "*.trx" -File | Sort-Object FullName)
}

if ($files.Count -eq 0) {
    $lines.Add("No TRX file under ``$Path``.")
}
else {
    $lines.Add("| Run | Total | Passed | Failed | Not executed |")
    $lines.Add("|---|---:|---:|---:|---:|")
    $failed = [System.Collections.Generic.List[string]]::new()
    $totals = @{ total = 0; passed = 0; failed = 0; notExecuted = 0 }
    foreach ($file in $files) {
        try {
            [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw
        }
        catch {
            $lines.Add("| $($file.Name) | unreadable | | | |")
            continue
        }
        # XPath by local name: a TRX without results (an empty run) has no Counters or Results element.
        $counters = $trx.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
        $counter = {
            param([string] $Name)
            if ($null -ne $counters -and $counters.HasAttribute($Name)) { [int] $counters.GetAttribute($Name) } else { 0 }
        }
        $total = & $counter 'total'
        $passed = & $counter 'passed'
        $failedCount = (& $counter 'failed') + (& $counter 'error') + (& $counter 'timeout') + (& $counter 'aborted')
        $notExecuted = & $counter 'notExecuted'
        $totals.total += $total
        $totals.passed += $passed
        $totals.failed += $failedCount
        $totals.notExecuted += $notExecuted
        $relative = [System.IO.Path]::GetRelativePath((Resolve-Path -LiteralPath $Path).Path, $file.FullName)
        $lines.Add("| $relative | $total | $passed | $failedCount | $notExecuted |")
        foreach ($result in $trx.SelectNodes("//*[local-name()='Results']/*[local-name()='UnitTestResult']")) {
            $outcome = $result.GetAttribute('outcome')
            if ($outcome -in @("Failed", "Error", "Timeout", "Aborted")) {
                $failed.Add("- ``$($result.GetAttribute('testName'))`` ($outcome)")
            }
        }
    }
    $lines.Add("| **All** | **$($totals.total)** | **$($totals.passed)** | **$($totals.failed)** | **$($totals.notExecuted)** |")
    if ($failed.Count -gt 0) {
        $lines.Add("")
        $lines.Add("Failed tests ($($failed.Count)):")
        foreach ($entry in ($failed | Select-Object -First $MaxFailures)) {
            $lines.Add($entry)
        }
        if ($failed.Count -gt $MaxFailures) {
            $lines.Add("- ... and $($failed.Count - $MaxFailures) more; see the TRX files.")
        }
    }
}

$text = ($lines -join [Environment]::NewLine) + [Environment]::NewLine
Write-Output $text
if ($Out) {
    $directory = Split-Path -Parent $Out
    if ($directory) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    Set-Content -LiteralPath $Out -Value $text -NoNewline
}
exit 0
