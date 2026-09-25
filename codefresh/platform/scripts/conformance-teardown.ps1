#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Teardown of platform-env/conformance and platform-env/conformance-destructive: force-sleeps every app cluster the
    run woke.

.DESCRIPTION
    ADR-IR34 "Cost control": runs runbook env-sleep of platform-infrastructure (Sleep.Force=true) through
    octopus-runbook.ps1, in parallel, for each tier that was not Running when the run started
    (<results>/power-before.txt, written by conformance-run.ps1); without that record both tiers are slept. A tier
    recorded as 'unconfigured' is skipped. env-sleep's busy rule keeps a cluster up while a deployment or runbook
    run is queued or executing, so the teardown never stops a cluster under a task.

    CONFORMANCE_SLEEP_AFTER=false keeps the clusters up for debugging. Never fails the build: a runbook that does not
    finish is a warning, and the hourly env-sleep retries.

    Environment: OCTOPUS_URL, OCTOPUS_SPACE_ID, OCTOPUS_API_KEY (platform-octopus); PLATFORM_RUN_ID;
    CONFORMANCE_SLEEP_AFTER.
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

try {
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
    $runId = if ($env:PLATFORM_RUN_ID) { $env:PLATFORM_RUN_ID } else { 'unknown' }

    $runs = [System.Collections.Generic.List[object]]::new()
    foreach ($tier in 'nonprod', 'prod') {
        $line = $recorded | Where-Object { $_.StartsWith("$tier=", [StringComparison]::Ordinal) } | Select-Object -First 1
        $state = if ($line) { $line.Substring($tier.Length + 1) } else { '' }
        if ($state.StartsWith('Running/', [StringComparison]::Ordinal)) {
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
