#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Step octopus_release of workorders/release: merges a short CI summary (version, commit, pull request, builds, one
    line per test suite, image digests, where the TRX files are) into the Octopus release notes.

.DESCRIPTION
    The notes file that buildinfo.ps1 wrote stays as it is, first line "app-commit: <sha>" included (the Octopus step
    report-commit-status reads it); the summary is appended after a blank line, under the heading "### CI summary".
    A summary appended by an earlier run of the step (a restarted build) is replaced, not repeated. The file is
    written once, at the end: a failure leaves it unchanged.

    Test counts come only from the TRX files of this build (-ArtifactsDir, the gates' copies of build/test):
    one line per suite, "<suite> (<gate>): P passed, F failed, S skipped" (skipped = not executed). A gate that did
    not run here is never given counts:
      - CI_TREE_VERIFIED=true: code_analysis, build_sqlite, qodana and security_scan exited early because
        codefresh/ci passed the same tree on CI_TREE_COMMIT; the line says so and links that commit and its CI build
        (CI_TREE_URL, from ci-tree.ps1).
      - RELEASE_ACCEPTANCE other than true: acceptance did not run in the release; the line says so.
    Image references by digest come from ${ARTIFACTS_DIR}/image-digests.txt (supply-chain-step.ps1) when it exists.
    The pull request is the number in the release commit's subject ("Merge pull request #N" or a trailing "(#N)").

    Octopus releases take no file attachments (artifacts belong to deployment tasks), so the TRX files stay in the
    Codefresh build's artifact folder (artifacts/<CF_BUILD_ID>/ on the pipeline volume, the ten newest builds kept);
    the notes name that folder and link the build, whose gate step log prints the per-file TRX table.

    Environment: VERSION, CF_REVISION (else git HEAD), CF_BUILD_URL, CF_BUILD_ID, CF_REPO_OWNER, CF_REPO_NAME,
    CI_TREE_VERIFIED, CI_TREE_COMMIT, CI_TREE_URL, RELEASE_ACCEPTANCE, ARTIFACTS_DIR.

    Usage (Codefresh step octopus_release, in the application checkout):
      pwsh -NoProfile -File <env-repo>/codefresh/apps/workorders/scripts/release-notes.ps1 -Notes build/octopus-release-notes.md

.PARAMETER Notes
    The release-notes file to merge into (created when missing).

.PARAMETER ArtifactsDir
    The build's artifact folder (default: ARTIFACTS_DIR).

.PARAMETER Repo
    The application checkout, for the pull request number (default: the current folder).

.NOTES
    Exit codes: 0 written; 1 failed (no VERSION, the notes file cannot be written).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Notes,
    [string] $ArtifactsDir = $(if ($env:ARTIFACTS_DIR) { $env:ARTIFACTS_DIR } else { '' }),
    [string] $Repo = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$Heading = '### CI summary'
# Gate folder under ARTIFACTS_DIR -> the label of its build; suite folder of build.ps1 -> the suite's name.
$GateLabels = [ordered]@{ build_sql = 'SQL Server'; build_sqlite = 'SQLite'; acceptance = 'acceptance step' }
$SuiteLabels = [ordered]@{ UnitTests = 'Unit'; IntegrationTests = 'Integration'; AcceptanceTests = 'Acceptance' }

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("release-notes.ps1: $Message")
}

function Exit-Failure([string] $Message) {
    Write-Note $Message
    exit 1
}

# An environment value, or '' when it is unset, 'none' or an unresolved Codefresh ${{...}} expression.
function Get-Setting([string] $Name) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if (-not $value -or $value -ceq 'none' -or $value.Contains('${{')) {
        return ''
    }
    return $value.Trim()
}

# The pull request number in the release commit's subject, or ''.
function Get-PullRequestNumber([string] $Checkout, [string] $Commit) {
    if (-not (Get-Command -Name git -CommandType Application -ErrorAction SilentlyContinue)) {
        return ''
    }
    $PSNativeCommandUseErrorActionPreference = $false
    $subject = (@(& git -C $Checkout log -1 --format=%s $Commit 2>$null) -join "`n").Trim()
    if ($LASTEXITCODE -ne 0) {
        return ''
    }
    if ($subject -cmatch '^Merge pull request #(\d+)\b' -or $subject -cmatch '\(#(\d+)\)$') {
        return $Matches[1]
    }
    return ''
}

# One line per suite, from the TRX files under the artifact folder, grouped by gate folder and suite folder.
function Get-SuiteLine([string] $Folder) {
    if (-not $Folder -or -not (Test-Path -LiteralPath $Folder -PathType Container)) {
        return @()
    }
    $runs = @(& (Join-Path $PSScriptRoot 'trx-summary.ps1') -Path $Folder -PassThru)
    $groups = [ordered]@{}
    foreach ($run in $runs) {
        $segments = @($run.Path -split '/')
        $gate = $segments[0]
        $suiteFolder = @($segments | Where-Object { $SuiteLabels.Contains($_) } | Select-Object -First 1)
        $suite = if ($suiteFolder.Count -gt 0) { $SuiteLabels[$suiteFolder[0]] } elseif ($segments.Count -gt 2) { $segments[-2] } else { 'Tests' }
        $label = if ($GateLabels.Contains($gate)) { $GateLabels[$gate] } else { $gate }
        $key = "$suite ($label)"
        if (-not $groups.Contains($key)) {
            $groups[$key] = [pscustomobject]@{ Passed = 0; Failed = 0; Skipped = 0; Unreadable = 0 }
        }
        $group = $groups[$key]
        if (-not $run.Readable) {
            $group.Unreadable++
            continue
        }
        $group.Passed += $run.Passed
        $group.Failed += $run.Failed
        $group.Skipped += $run.NotExecuted
    }
    # Unit before Integration before Acceptance, then the rest; SQL Server before SQLite within a suite.
    $order = @($SuiteLabels.Values)
    $keys = @($groups.Keys | Sort-Object -Stable -Property @{ Expression = { $i = $order.IndexOf(($_ -split ' \(')[0]); if ($i -lt 0) { 99 } else { $i } } }, @{ Expression = { $_ } })
    foreach ($key in $keys) {
        $group = $groups[$key]
        $line = "  - ${key}: $($group.Passed) passed, $($group.Failed) failed, $($group.Skipped) skipped"
        if ($group.Unreadable -gt 0) {
            $line += " ($($group.Unreadable) TRX file(s) unreadable)"
        }
        $line
    }
}

$version = Get-Setting 'VERSION'
if (-not $version) {
    Exit-Failure 'VERSION is not set'
}
$checkout = if ($Repo) { $Repo } else { $PWD.ProviderPath }
$owner = Get-Setting 'CF_REPO_OWNER'
$name = Get-Setting 'CF_REPO_NAME'
if (-not $owner) { $owner = 'clearmeasure-aisf-sample-apps' }
if (-not $name) { $name = '20260923-001' }
$repoUrl = "https://github.com/$owner/$name"
$commit = Get-Setting 'CF_REVISION'
if (-not $commit -and (Get-Command -Name git -CommandType Application -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
    $commit = (@(& git -C $checkout rev-parse HEAD 2>$null) -join '').Trim()
    $PSNativeCommandUseErrorActionPreference = $true
}
$buildUrl = Get-Setting 'CF_BUILD_URL'
$buildId = Get-Setting 'CF_BUILD_ID'

$block = [System.Collections.Generic.List[string]]::new()
$block.Add($Heading)
$block.Add("- Version: $version")
if ($commit -cmatch '^[0-9a-f]{40}$') {
    $line = "- Commit: [$($commit.Substring(0, 7))]($repoUrl/commit/$commit)"
    $pull = Get-PullRequestNumber $checkout $commit
    if ($pull) {
        $line += ", PR [#$pull]($repoUrl/pull/$pull)"
    }
    $block.Add($line)
}
if ($buildUrl) {
    $block.Add("- Codefresh release build: [$(if ($buildId) { $buildId } else { 'build' })]($buildUrl)")
}
$verified = (Get-Setting 'CI_TREE_VERIFIED') -ceq 'true'
$ciCommit = Get-Setting 'CI_TREE_COMMIT'
$ciUrl = Get-Setting 'CI_TREE_URL'
$ciReference = ''
if ($verified -and $ciCommit -cmatch '^[0-9a-f]{40}$') {
    $ciReference = "[$($ciCommit.Substring(0, 7))]($repoUrl/commit/$ciCommit)"
    $line = "- CI: codefresh/ci passed the same tree on $ciReference"
    if ($ciUrl -and $ciUrl -cne $buildUrl) {
        $line += ", [CI build]($ciUrl)"
    }
    $block.Add($line)
}
else {
    $block.Add('- CI: no codefresh/ci result reused; every gate ran in this build')
}
$block.Add('- Tests run by this build (TRX):')
$suites = @(Get-SuiteLine $ArtifactsDir)
if ($suites.Count -eq 0) {
    $block.Add('  - no TRX file found')
}
foreach ($suite in $suites) {
    $block.Add($suite)
}
if ($ciReference) {
    $block.Add("  - SQLite build, code analysis, Qodana, security scan: not re-run; codefresh/ci passed them on $ciReference")
}
if ((Get-Setting 'RELEASE_ACCEPTANCE') -cne 'true') {
    $block.Add('  - Acceptance: not run in this build (RELEASE_ACCEPTANCE is not true); the tdd deployment runs it')
}
$digestFile = if ($ArtifactsDir) { Join-Path $ArtifactsDir 'image-digests.txt' } else { '' }
if ($digestFile -and (Test-Path -LiteralPath $digestFile -PathType Leaf)) {
    $digests = @(Get-Content -LiteralPath $digestFile | Where-Object { $_ -cmatch '^[^@\s]+/([^/@\s]+)@sha256:([0-9a-f]{64})$' } | ForEach-Object {
            if ($_ -cmatch '/([^/@\s]+)@sha256:([0-9a-f]{64})$') { "$($Matches[1])@sha256:$($Matches[2].Substring(0, 12))" }
        })
    if ($digests.Count -gt 0) {
        $block.Add("- Images: $($digests -join ', ')")
    }
}
$folder = if ($buildId) { "artifacts/$buildId/" } else { 'the build''s artifact folder' }
$block.Add("- TRX files: Codefresh pipeline volume, $folder (the 10 newest builds are kept); Octopus releases take no attachments")

# Merge: keep the existing notes up to an earlier summary, then append this one after one blank line.
$full = [System.IO.Path]::GetFullPath($Notes, $PWD.ProviderPath)
$existing = @()
if (Test-Path -LiteralPath $full -PathType Leaf) {
    $existing = @([System.IO.File]::ReadAllText($full, [System.Text.UTF8Encoding]::new($false)).Replace("`r`n", "`n").Split("`n"))
    $cut = [Array]::IndexOf($existing, $Heading)
    if ($cut -ge 0) {
        $existing = @($existing | Select-Object -First $cut)
    }
    while ($existing.Count -gt 0 -and $existing[-1] -ceq '') {
        $existing = @($existing | Select-Object -SkipLast 1)
    }
}
$lines = [System.Collections.Generic.List[string]]::new()
foreach ($line in $existing) {
    $lines.Add($line)
}
if ($lines.Count -gt 0) {
    $lines.Add('')
}
$lines.AddRange($block)
try {
    $null = New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($full))
    [System.IO.File]::WriteAllText($full, (($lines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
}
catch {
    Exit-Failure "cannot write ${Notes}: $($_.Exception.Message)"
}
Write-Note "merged the CI summary ($($block.Count) lines, $($suites.Count) suite line(s)) into $Notes"
exit 0
