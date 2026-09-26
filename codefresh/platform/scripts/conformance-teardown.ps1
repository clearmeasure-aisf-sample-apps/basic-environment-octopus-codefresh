#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Teardown of platform-env/conformance and platform-env/conformance-destructive: releases the run's sleep hold and
    force-sleeps every app cluster the run woke.

.DESCRIPTION
    First releases the sleep hold that conformance-arm.ps1 set: runbook sleep-hold of platform-infrastructure with
    Sleep.HoldMinutes=0 in infra-nonprod and infra-prod (octopus-runbook.ps1, one after the other, up to 5 minutes
    each), also when CONFORMANCE_SLEEP_AFTER=false, so the hourly env-sleep applies its normal rules again. A hold that
    is not released ends by itself (at most 12 hours after the arm).

    Then, ADR-IR34 "Cost control": runs runbook env-sleep of platform-infrastructure (Sleep.Force=true) through
    octopus-runbook.ps1, in parallel, for each tier that was not Running when the run started
    (<results>/power-before.txt, written by conformance-run.ps1); without that record both tiers are slept. A tier that
    conformance-arm.ps1 stopped (CONFORMANCE_ARM_STOPPED, for example 'nonprod,prod') counts as stopped before the
    run whatever power-before.txt says: the sandbox builds that run between the arm and the suite wake nonprod (early
    env-wake) before power-before.txt is written. A tier recorded as 'unconfigured' is skipped. env-sleep's busy rule keeps a cluster up while a deployment or runbook
    run is queued or executing, so the teardown never stops a cluster under a task.

    CONFORMANCE_SLEEP_AFTER=false keeps the clusters up for debugging (the hold is still released). Never fails the
    build: a runbook that does not finish is a warning, and the hourly env-sleep retries.

    Environment: OCTOPUS_URL, OCTOPUS_SPACE_ID, OCTOPUS_API_KEY (platform-octopus); PLATFORM_RUN_ID;
    CONFORMANCE_SLEEP_AFTER; CONFORMANCE_ARM_STOPPED (set by conformance-arm.ps1).
    Exit code: always 0.

.PARAMETER ResultsDirectory
    The run's results folder (CONFORMANCE_RESULTS_DIR); empty or missing: both tiers are slept.

.EXAMPLE
    pwsh -NoProfile -File codefresh/platform/scripts/conformance-teardown.ps1 -ResultsDirectory "$CONFORMANCE_RESULTS_DIR"
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $ResultsDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("conformance-teardown: $Message")
}

# The PowerShell that runs this script, to start octopus-runbook.ps1 with (the .NET global tool runs as 'dotnet pwsh.dll').
function Get-PwshCommand {
    $processPath = [Environment]::ProcessPath
    if (-not $processPath) {
        return , @('pwsh')
    }
    if ([System.IO.Path]::GetFileNameWithoutExtension($processPath) -eq 'dotnet') {
        return , @($processPath, (Join-Path $PSHOME 'pwsh.dll'))
    }
    return , @($processPath)
}

# Starts octopus-runbook.ps1 in its own process: standard error goes to the log, standard output (the task ID) is dropped.
function Start-EnvSleep([string] $Environment, [string] $Notes) {
    $command = Get-PwshCommand
    $start = [System.Diagnostics.ProcessStartInfo]::new($command[0])
    $arguments = @($command | Select-Object -Skip 1) + @(
        '-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'octopus-runbook.ps1'),
        '-Project', 'platform-infrastructure', '-Runbook', 'env-sleep', '-Environment', $Environment,
        '-Prompt', 'Sleep.Force=true', '-Notes', $Notes, '-WaitMinutes', '30')
    foreach ($argument in $arguments) {
        $start.ArgumentList.Add($argument)
    }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.WorkingDirectory = (Get-Location).ProviderPath
    $process = [System.Diagnostics.Process]::Start($start)
    $process.BeginOutputReadLine()
    return $process
}

# Releases the sleep hold of one environment: runbook sleep-hold with Sleep.HoldMinutes=0 through octopus-runbook.ps1, in
# this process (two prompted values do not fit 'pwsh -File'). A failure is a warning: the hold ends by itself.
function Clear-SleepHold([string] $Environment, [string] $RunId) {
    $global:LASTEXITCODE = 0
    try {
        & (Join-Path $PSScriptRoot 'octopus-runbook.ps1') -Project 'platform-infrastructure' -Runbook 'sleep-hold' -Environment $Environment `
            -Prompt @('Sleep.HoldMinutes=0', "Sleep.HoldBy=conformance:$RunId") -Notes "conformance:$RunId release" -WaitMinutes 5 | Out-Null
        $exitCode = $LASTEXITCODE
    }
    catch {
        Write-Note "WARN sleep-hold could not release the hold in ${Environment}: $($_.Exception.Message); it ends by itself"
        return
    }
    if ($exitCode -eq 0) {
        Write-Note "sleep hold released in $Environment"
    }
    else {
        Write-Note "WARN sleep-hold did not finish successfully in $Environment (octopus-runbook exit $exitCode); the hold ends by itself"
    }
}

try {
    $runId = if ($env:PLATFORM_RUN_ID) { $env:PLATFORM_RUN_ID } else { 'unknown' }
    foreach ($environment in 'infra-nonprod', 'infra-prod') {
        Clear-SleepHold $environment $runId
    }

    if ($env:CONFORMANCE_SLEEP_AFTER -ceq 'false') {
        Write-Note 'CONFORMANCE_SLEEP_AFTER=false; the clusters stay up'
        exit 0
    }
    $before = "$ResultsDirectory/power-before.txt"
    $recorded = if ($ResultsDirectory -and (Test-Path -LiteralPath $before -PathType Leaf)) {
        @([System.IO.File]::ReadAllLines($before))
    }
    else {
        @()
    }
    $armStopped = @(([string] $env:CONFORMANCE_ARM_STOPPED) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $runs = [System.Collections.Generic.List[object]]::new()
    foreach ($tier in 'nonprod', 'prod') {
        $line = $recorded | Where-Object { $_.StartsWith("$tier=", [StringComparison]::Ordinal) } | Select-Object -First 1
        $state = if ($line) { $line.Substring($tier.Length + 1) } else { '' }
        if ($armStopped -ccontains $tier) {
            if ($state.StartsWith('Running/', [StringComparison]::Ordinal)) {
                Write-Note "$tier was stopped by conformance-arm and woken before power-before.txt was written; sleeping it"
            }
        }
        elseif ($state.StartsWith('Running/', [StringComparison]::Ordinal)) {
            Write-Note "$tier was Running before the run; left up"
            continue
        }
        if ($state -ceq 'unconfigured') {
            Write-Note "$tier has no cluster settings; skipped"
            continue
        }
        $environment = "infra-$tier"
        try {
            $runs.Add(@{ Environment = $environment; Process = (Start-EnvSleep $environment "conformance:$runId teardown") })
        }
        catch {
            Write-Note "WARN env-sleep could not start in ${environment}: $($_.Exception.Message); the hourly env-sleep will retry"
        }
    }

    foreach ($run in $runs) {
        $run.Process.WaitForExit()
        if ($run.Process.ExitCode -eq 0) {
            Write-Note "env-sleep finished in $($run.Environment)"
        }
        else {
            Write-Note "WARN env-sleep did not finish successfully in $($run.Environment) (octopus-runbook exit $($run.Process.ExitCode)); the hourly env-sleep will retry"
        }
    }
}
catch {
    Write-Note "WARN teardown failed: $($_.Exception.Message); the hourly env-sleep will retry"
}
exit 0
