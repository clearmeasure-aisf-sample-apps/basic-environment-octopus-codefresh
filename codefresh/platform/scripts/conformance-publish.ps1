#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Pushes a conformance run's results (TRX, summary.md, summary.json) to branch conformance-results of
    <sandbox-app-repo>, folder results/<UTC date>-<run id>/.

.DESCRIPTION
    ADR-IR34 "Reporting": history without any write to the environment repository. Creates the branch (orphan, with a
    README) on first use. Copies every *.trx, summary.md and summary.json up to two folders below the results folder
    into the run's folder, commits and pushes through sandbox-git.ps1.

    Never fails the build: a publishing problem is a warning (exit 0), including a missing GITHUB_TOKEN.

    Environment: GITHUB_TOKEN (platform-conformance), SANDBOX_APP_REPO, PLATFORM_RUN_ID; SANDBOX_GIT_URL (rehearsals).
    Exit code: always 0.

.PARAMETER ResultsDirectory
    The run's results folder (CONFORMANCE_RESULTS_DIR).

.EXAMPLE
    pwsh -NoProfile -File codefresh/platform/scripts/conformance-publish.ps1 -ResultsDirectory "$CONFORMANCE_RESULTS_DIR"
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $ResultsDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. (Join-Path $PSScriptRoot 'sandbox-git.ps1')

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("conformance-publish: $Message")
}

function Exit-Warning([string] $Message) {
    Write-Note "WARN $Message"
    exit 0
}

$work = ''
$entered = $false
try {
    if (-not (Test-SandboxRequirement)) {
        Exit-Warning 'nothing published'
    }
    if (-not $ResultsDirectory -or -not (Test-Path -LiteralPath $ResultsDirectory -PathType Container)) {
        Exit-Warning "no results folder '$ResultsDirectory'"
    }
    $source = (Resolve-Path -LiteralPath $ResultsDirectory).ProviderPath
    $runId = if ($env:PLATFORM_RUN_ID) { $env:PLATFORM_RUN_ID } else { 'unknown' }
    $target = "results/$([DateTime]::UtcNow.ToString('yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture))-$runId"
    $url = Get-SandboxUrl

    $work = [System.IO.Directory]::CreateTempSubdirectory('conformance-publish-').FullName
    $clone = "$work/results"
    # Git failures are checked where they matter (clone, commit, push); the rest may fail as before.
    $PSNativeCommandUseErrorActionPreference = $false
    Invoke-SandboxGit clone --quiet --depth 1 --branch conformance-results $url $clone 2>$null
    if ($LASTEXITCODE -eq 0) {
        if (-not (Test-Path -LiteralPath $clone -PathType Container)) {
            Exit-Warning 'clone folder missing'
        }
        Push-Location -LiteralPath $clone
        $entered = $true
    }
    else {
        Invoke-SandboxGit clone --quiet --depth 1 $url $clone
        if ($LASTEXITCODE -ne 0) {
            Exit-Warning "clone of $($env:SANDBOX_APP_REPO) failed (git exit $LASTEXITCODE)"
        }
        if (-not (Test-Path -LiteralPath $clone -PathType Container)) {
            Exit-Warning 'clone folder missing'
        }
        Push-Location -LiteralPath $clone
        $entered = $true
        & git checkout --quiet --orphan conformance-results
        & git rm -rf --quiet . *> $null
        [System.IO.File]::WriteAllText((Join-Path $clone 'README.md'),
            "# Conformance results`n`nOne folder per run of platform-env/conformance*: TRX files and the summaries.`n",
            [System.Text.UTF8Encoding]::new($false))
        & git add README.md
    }

    $folder = Join-Path $clone $target
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Recurse -Depth 2 -File -Force |
        Where-Object { $_.Name -clike '*.trx' -or $_.Name -ceq 'summary.md' -or $_.Name -ceq 'summary.json' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $folder -Force -ErrorAction Continue }
    & git add $target
    Invoke-SandboxGit commit --quiet -m "conformance ${runId}: results"
    if ($LASTEXITCODE -ne 0) {
        Exit-Warning "nothing to commit (git exit $LASTEXITCODE)"
    }
    Invoke-SandboxGit push --quiet origin HEAD:refs/heads/conformance-results
    if ($LASTEXITCODE -ne 0) {
        Exit-Warning "push to $($env:SANDBOX_APP_REPO) branch conformance-results failed (git exit $LASTEXITCODE)"
    }
    Write-Note "results in $($env:SANDBOX_APP_REPO), branch conformance-results, $target"
}
catch {
    Write-Note "WARN publishing failed: $($_.Exception.Message)"
}
finally {
    if ($entered) {
        Pop-Location
    }
    if ($work) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
exit 0
