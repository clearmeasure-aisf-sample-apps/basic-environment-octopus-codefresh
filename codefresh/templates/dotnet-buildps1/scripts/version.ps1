#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Prints the platform version for HEAD (ADR-C7, contract §7.5).

.DESCRIPTION
      release branch:   MAJOR.MINOR.<git rev-list --count --first-parent HEAD>
      any other branch: MAJOR.MINOR.<count>-ci.<sha7>   (never released)
    The release branch is RELEASE_BRANCH, default master (app #1); -ReleaseBranch overrides it.
    MAJOR and MINOR come from codefresh/apps/<app>/version.env, next to this script in
    the environment repo. The height is counted in the application checkout
    (<app-repo>): the working directory, or -Repo.
    The image tag, the Octopus package version and the Octopus release number all use
    this one string.

    Runnable locally and in Codefresh (the branch comes from CF_BRANCH there), from the
    application checkout:
      pwsh -NoProfile -File <env-repo>/codefresh/apps/<app>/scripts/version.ps1 [-Branch <name>] [-Repo <dir>]
           [-VersionFile <file>] [-NoFetch]

    A count taken from a shallow clone would mint a wrong, possibly colliding
    version, so a shallow clone is unshallowed (git fetch --unshallow) or the
    script fails. -NoFetch turns the fetch off; the script then fails on a
    shallow clone instead.

.PARAMETER Branch
    Branch to version for (default: CF_BRANCH, else the current branch).

.PARAMETER ReleaseBranch
    Branch whose commits are released (default: RELEASE_BRANCH, else master).

.PARAMETER Repo
    Application checkout (default: git rev-parse --show-toplevel).

.PARAMETER VersionFile
    MAJOR/MINOR file (default: ../version.env next to this script).

.PARAMETER NoFetch
    Never run git fetch --unshallow; fail on a shallow clone.

.OUTPUTS
    The version, one line on standard output. The log goes to standard error.

.NOTES
    Exit codes: 0 printed; 1 failed (no version file, MAJOR or MINOR missing, not numeric or set
    twice, not a git repository, a shallow clone, a part above 65534, a failing git command).
#>
[CmdletBinding()]
param(
    [string] $Branch = $env:CF_BRANCH,
    [string] $ReleaseBranch = $(if ($env:RELEASE_BRANCH) { $env:RELEASE_BRANCH } else { 'master' }),
    [string] $Repo = '',
    [string] $VersionFile = '',
    [switch] $NoFetch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# AssemblyVersion/FileVersion parts must be <= 65534.
$maxPart = [System.Numerics.BigInteger]::new(65534)

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("version.ps1: $Message")
}

function Exit-Failure([string] $Message) {
    Write-Note $Message
    exit 1
}

# Runs git; returns its exit code and its standard output as text without the trailing line feeds.
function Invoke-Git([string[]] $Arguments) {
    $PSNativeCommandUseErrorActionPreference = $false
    $output = & git @Arguments
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = (@($output) -join "`n").TrimEnd("`n") }
}

# The value of KEY=digits in the version file; parsed, never sourced: only KEY=digits lines count.
function Get-VersionPart([string[]] $Lines, [string] $Name, [string] $File) {
    $values = @(foreach ($line in $Lines) {
            if ($line -cmatch "^$Name=([0-9]+)[ \t\n\v\f\r]*\z") {
                $Matches[1]
            }
        })
    if ($values.Count -eq 0) {
        Exit-Failure "$Name is missing or not numeric in $File"
    }
    if ($values.Count -gt 1) {
        Exit-Failure "$Name is set more than once in $File"
    }
    return $values[0]
}

if (-not $VersionFile) {
    $VersionFile = Join-Path $PSScriptRoot '../version.env'
}
if (-not (Test-Path -LiteralPath $VersionFile -PathType Leaf)) {
    Exit-Failure "missing $VersionFile"
}
$VersionFile = [System.IO.Path]::GetFullPath($VersionFile, $PWD.ProviderPath)

if (-not $Repo) {
    $toplevel = Invoke-Git @('rev-parse', '--show-toplevel')
    if ($toplevel.ExitCode -ne 0) {
        Exit-Failure 'not inside a git repository'
    }
    $Repo = $toplevel.Text
}
try {
    Push-Location -LiteralPath $Repo
}
catch {
    Exit-Failure "cannot enter $Repo"
}

try {
    $lines = [System.IO.File]::ReadAllText($VersionFile) -split "`n"
    $major = Get-VersionPart $lines 'MAJOR' $VersionFile
    $minor = Get-VersionPart $lines 'MINOR' $VersionFile

    $shallow = Invoke-Git @('rev-parse', '--is-shallow-repository')
    if ($shallow.ExitCode -ne 0) {
        Exit-Failure 'cannot tell whether the clone is shallow'
    }
    if ($shallow.Text -ceq 'true') {
        if (-not $NoFetch) {
            Write-Note 'shallow clone; fetching the full history'
            if ((Invoke-Git @('fetch', '--unshallow', '--quiet')).ExitCode -ne 0) {
                Exit-Failure 'git fetch --unshallow failed'
            }
            $shallow = Invoke-Git @('rev-parse', '--is-shallow-repository')
            if ($shallow.ExitCode -ne 0) {
                Exit-Failure 'cannot tell whether the clone is shallow'
            }
        }
        if ($shallow.Text -cne 'false') {
            Exit-Failure 'shallow clone: the first-parent count would be wrong; clone with full depth'
        }
    }

    $height = (git rev-list --count --first-parent HEAD) -join ''

    foreach ($part in @($major, $minor, $height)) {
        if ([System.Numerics.BigInteger]::Parse($part, [System.Globalization.CultureInfo]::InvariantCulture) -gt $maxPart) {
            Exit-Failure "version part $part exceeds $maxPart; bump MINOR in codefresh/apps/<app>/version.env"
        }
    }

    if (-not $Branch) {
        # Detached HEAD prints "HEAD", which is not the release branch: the safe, never-released form.
        $Branch = (git rev-parse --abbrev-ref HEAD) -join ''
    }

    if ($Branch -ceq $ReleaseBranch) {
        Write-Output "$major.$minor.$height"
    }
    else {
        $headSha = (git rev-parse HEAD) -join ''
        Write-Output "$major.$minor.$height-ci.$($headSha.Substring(0, [Math]::Min(7, $headSha.Length)))"
    }
}
finally {
    Pop-Location
}
exit 0
