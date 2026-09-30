#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Runs the end-to-end pass of app #1 (CAP-KIT-009, EndToEndTests) outside a Codefresh build: the driver of runbook
    e2e-pass of platform-infrastructure, and of an operator's machine.

.DESCRIPTION
    The pass opens a pull request on app #1, waits for codefresh/ci, merges, waits for codefresh/release and follows the
    Octopus release to prod (tests/Platform.Conformance.Tests/Kit/EndToEndTests.cs). It mostly waits, so it runs here
    rather than in platform-env/conformance, where it would hold one of the account's three build slots for about an
    hour while the ci and release builds it starts need slots too (docs/runbooks/conformance.md, "End-to-end pass
    without a Codefresh slot").

    In order:
      1. checks the prerequisites: OCTOPUS_API_KEY and the three inputs of the GitHub App aisf-conformance set
         (AISF_CONFORMANCE_APP_ID, AISF_CONFORMANCE_APP_INSTALLATION_ID and AISF_CONFORMANCE_APP_PRIVATE_KEY or, for an
         operator, AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH), a .NET 10 SDK on PATH (tests/global.json); otherwise exits 2
         before any build, naming what is missing and never a value (the App not configured is PENDING owner setup, #44);
      2. mints PLATFORM_RUN_ID (r<yyyyMMdd>t<HHmm>-e2e) when it is absent;
      3. runs conformance-run.ps1 from the repository root with TEST_FILTER=FullyQualifiedName~EndToEndTests (an
         earlier TEST_FILTER is replaced): it mints the App's installation token (three repositories: the environment
         repository, the sandbox repository and 20260923-001; Contents rw, Pull requests rw, Commit statuses read,
         Metadata read) and re-mints it every 45 minutes, dotnet build and test of tests/Platform.Conformance.sln, the heartbeat and
         progress lines, the TRX files, summary.md and summary.json in the results folder. Outside Codefresh it records
         no annotation and exports nothing; without the conformance principal it records no power state.
    Nothing tears down afterwards: the hourly env-sleep puts the clusters back to sleep by its own rules.

    Environment: OCTOPUS_API_KEY (the Space Manager key, ADR-IR32) and AISF_CONFORMANCE_APP_ID,
    AISF_CONFORMANCE_APP_INSTALLATION_ID, AISF_CONFORMANCE_APP_PRIVATE_KEY (or _PATH) (the GitHub App aisf-conformance of
    context platform-conformance) are required; PLATFORM_RUN_ID, PLATFORM_E2E_APP, PLATFORM_E2E_REPO, PLATFORM_E2E_FILE,
    PLATFORM_ARTIFACTS_DIR and PLATFORM_SETTINGS_FILE are optional (tests/README.md). No secret is printed or put on a
    command line.

    Exit code: that of conformance-run.ps1 (0 when the pass reached prod, 1 when it failed); 2 when a prerequisite is
    missing.

.PARAMETER ResultsDirectory
    Results folder: TRX files, summaries, the harness artifacts (the Octopus task logs of each stage). Default
    tests/TestResults/e2e/<run id> under the repository root (ignored by git).

.PARAMETER HeartbeatSeconds
    Seconds between two heartbeats of conformance-run.ps1 while dotnet test runs; 300 (default).

.EXAMPLE
    OCTOPUS_API_KEY=... AISF_CONFORMANCE_APP_ID=... AISF_CONFORMANCE_APP_INSTALLATION_ID=... AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH=... pwsh -NoProfile -File codefresh/platform/scripts/conformance-e2e.ps1
#>
[CmdletBinding()]
param(
    [string] $ResultsDirectory = '',

    [ValidateRange(1, 3600)]
    [int] $HeartbeatSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'conformance-github.ps1')

$filter = 'FullyQualifiedName~EndToEndTests'

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("conformance-e2e: $Message")
}

# 1. Prerequisites, by name only: the Octopus key and the inputs of the GitHub App aisf-conformance (#44), whose
# installation token conformance-run.ps1 mints.
$missing = @(foreach ($name in 'OCTOPUS_API_KEY') {
        if (-not [Environment]::GetEnvironmentVariable($name)) {
            $name
        }
    })
$appMissing = @(Test-ConformanceGitHubAppInput)
if ($missing.Count -gt 0 -or $appMissing.Count -gt 0) {
    Write-Note "not set: $((@($missing) + $appMissing) -join ', ') (docs/runbooks/conformance.md, 'End-to-end pass without a Codefresh slot')"
    if ($appMissing.Count -gt 0) {
        Write-Note 'the GitHub App aisf-conformance is not configured (AISF_CONFORMANCE_APP_ID / _INSTALLATION_ID / _PRIVATE_KEY, or _PRIVATE_KEY_PATH): PENDING owner setup, #44'
    }
    exit 2
}
$dotnet = Get-Command -Name dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $dotnet) {
    Write-Note 'dotnet is not on PATH: install the .NET 10 SDK (tests/global.json)'
    exit 2
}
$PSNativeCommandUseErrorActionPreference = $false
$sdks = @(& $dotnet.Source --list-sdks 2>$null)
$listed = $LASTEXITCODE -eq 0
$PSNativeCommandUseErrorActionPreference = $true
if (-not $listed -or -not @($sdks | Where-Object { "$_" -cmatch '\A10\.' })) {
    Write-Note "no .NET 10 SDK on PATH ($($dotnet.Source) lists: $(if ($sdks) { ($sdks | ForEach-Object { ("$_" -split ' ')[0] }) -join ', ' } else { 'none' })); install it (tests/global.json)"
    exit 2
}

# 2. The run ID: names the pull request branch e2e/<run id> and the intervention notes e2e:<run id>.
if (-not $env:PLATFORM_RUN_ID) {
    $env:PLATFORM_RUN_ID = 'r' + [DateTime]::UtcNow.ToString("yyyyMMdd't'HHmm", [System.Globalization.CultureInfo]::InvariantCulture) + '-e2e'
}
if ($env:TEST_FILTER -and $env:TEST_FILTER -cne $filter) {
    Write-Note "TEST_FILTER '$($env:TEST_FILTER)' replaced by $filter"
}
$env:TEST_FILTER = $filter

# 3. The suite, from the repository root (conformance-run.ps1 names its paths from there).
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..' '..'))
if (-not $ResultsDirectory) {
    $ResultsDirectory = Join-Path $root 'tests' 'TestResults' 'e2e' $env:PLATFORM_RUN_ID
}
$ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory, (Get-Location).ProviderPath)
Write-Note "run $($env:PLATFORM_RUN_ID) with $($dotnet.Source); results in $ResultsDirectory"
Push-Location -LiteralPath $root
try {
    & (Join-Path $PSScriptRoot 'conformance-run.ps1') -ResultsDirectory $ResultsDirectory -HeartbeatSeconds $HeartbeatSeconds
    $status = $LASTEXITCODE
}
finally {
    Pop-Location
}
Write-Note "run $($env:PLATFORM_RUN_ID) ended with exit code $status; summary.md, summary.json and the TRX files are in $ResultsDirectory"
exit $status
