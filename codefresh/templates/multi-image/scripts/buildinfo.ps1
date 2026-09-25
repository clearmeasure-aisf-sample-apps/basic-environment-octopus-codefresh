#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Writes the Octopus build-information JSON consumed by the Octopus CLI (build-information upload; contract §7.7,
    handoff 4) and, optionally, the release notes.

.DESCRIPTION
    The format documented at https://octopus.com/docs/packaging-applications/build-servers/build-information:
      BuildEnvironment, BuildNumber, BuildUrl, Branch, VcsType, VcsRoot,
      VcsCommitNumber, Commits[{Id, Comment}]
    Commits cover HEAD^1..HEAD: for a merge commit on master, the pull request's
    commits plus the merge itself. Octopus Insights measures lead time from the
    earliest commit in build information, and links "#1234" references to issues.

    Optionally also writes the release notes for the Octopus release (the release-notes file). Their
    first line is "app-commit: <sha>", which the Octopus step report-commit-status reads (contract
    §7.2 step 12). A file is used because the release notes rendered into generated YAML break on
    ": " (design §7.7, ADR-IR23).

    Run it from the application checkout (the triggering repo, CF_REPO_OWNER/CF_REPO_NAME);
    this script lives in the environment repo under codefresh/apps/<app>/scripts/.
    Relative output paths are relative to the application checkout.

    Usage: pwsh -NoProfile -File buildinfo.ps1 -Out <json-file> [-ReleaseNotesOut <file>] [-Repo <dir>]

.PARAMETER Out
    The build-information JSON file to write.

.PARAMETER ReleaseNotesOut
    The release-notes file to write (none when empty).

.PARAMETER Repo
    The application checkout (default: git rev-parse --show-toplevel).

.OUTPUTS
    The files; the log goes to standard error.

.NOTES
    Exit codes: 0 written; 1 failed (not a git repository, CF_REPO_OWNER or CF_REPO_NAME unset, a failing git
    command).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Out,
    [string] $ReleaseNotesOut = '',
    [string] $Repo = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("buildinfo.ps1: $Message")
}

function Exit-Failure([string] $Message) {
    Write-Note $Message
    exit 1
}

# Writes text as UTF-8 without a byte order mark, creating the folder first; relative paths start at the checkout.
function Write-TextFile([string] $Path, [string] $Text) {
    $full = [System.IO.Path]::GetFullPath($Path, $PWD.ProviderPath)
    $null = New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($full))
    [System.IO.File]::WriteAllText($full, $Text, [System.Text.UTF8Encoding]::new($false))
}

if (-not $Repo) {
    $PSNativeCommandUseErrorActionPreference = $false
    $toplevel = git rev-parse --show-toplevel
    $found = $LASTEXITCODE -eq 0
    $PSNativeCommandUseErrorActionPreference = $true
    if (-not $found) {
        Exit-Failure 'not inside a git repository'
    }
    $Repo = @($toplevel) -join "`n"
}
try {
    Push-Location -LiteralPath $Repo
}
catch {
    Exit-Failure "cannot enter $Repo"
}

try {
    $headSha = (git rev-parse HEAD) -join ''
    $branch = if ($env:CF_BRANCH) { $env:CF_BRANCH } else { (git rev-parse --abbrev-ref HEAD) -join '' }
    # CF_REPO_OWNER and CF_REPO_NAME name the triggering (application) repo in Codefresh.
    foreach ($variable in @('CF_REPO_OWNER', 'CF_REPO_NAME')) {
        if (-not [Environment]::GetEnvironmentVariable($variable)) {
            Exit-Failure "$variable is not set"
        }
    }
    $vcsRoot = "https://github.com/$($env:CF_REPO_OWNER)/$($env:CF_REPO_NAME)"

    $PSNativeCommandUseErrorActionPreference = $false
    $null = git rev-parse --verify --quiet 'HEAD^1'
    $range = if ($LASTEXITCODE -eq 0) { 'HEAD^1..HEAD' } else { 'HEAD' }   # root commit: only itself
    $PSNativeCommandUseErrorActionPreference = $true

    # One git call per commit, each checked, so a git failure stops the script instead of writing build
    # information with a silently empty commit list. The message goes through a file to keep its bytes.
    $commits = [System.Collections.Generic.List[object]]::new()
    $message = [System.IO.Path]::GetTempFileName()
    try {
        foreach ($sha in @(git log --format=%H $range)) {
            if (-not $sha) {
                continue
            }
            git log -1 --format=%B $sha > $message
            $comment = [System.IO.File]::ReadAllText($message, [System.Text.UTF8Encoding]::new($false)).TrimEnd("`n")
            $commits.Add([ordered]@{ Id = $sha; Comment = $comment })
        }
    }
    finally {
        Remove-Item -LiteralPath $message -Force -ErrorAction SilentlyContinue
    }

    $buildInformation = [ordered]@{
        BuildEnvironment = 'Codefresh'
        BuildNumber      = $(if ($env:CF_BUILD_ID) { $env:CF_BUILD_ID } else { 'local' })
        BuildUrl         = $(if ($env:CF_BUILD_URL) { $env:CF_BUILD_URL } else { '' })
        Branch           = $branch
        VcsType          = 'Git'
        VcsRoot          = $vcsRoot
        VcsCommitNumber  = $headSha
        Commits          = $commits
    }
    Write-TextFile $Out (($buildInformation | ConvertTo-Json -Depth 20) + "`n")
    Write-Note "wrote $Out ($($commits.Count) commit(s), range $range)"

    if ($ReleaseNotesOut) {
        $notes = "app-commit: $headSha`n"
        if ($env:CF_BUILD_URL) {
            $notes += "build: $($env:CF_BUILD_URL)`n"
        }
        Write-TextFile $ReleaseNotesOut $notes
        Write-Note "wrote $ReleaseNotesOut"
    }
}
finally {
    Pop-Location
}
exit 0
