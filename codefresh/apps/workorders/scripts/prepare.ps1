#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Step prepare of workorders/ci and workorders/release: version, change detection, SQL password, artifact folder, worktrees.

.DESCRIPTION
    Runs in the application checkout (the step's working directory) with the step's environment
    (RELEASE_BRANCH, CF_BRANCH, CF_VOLUME_PATH, CF_BUILD_ID) and exports, for the later steps:
      VERSION, BUILD_BUILDNUMBER  version.ps1 (ADR-C7); build.ps1 packs with BUILD_BUILDNUMBER.
      IS_RELEASE                  true with -Release (the release pipeline), else false.
      CODE_CHANGED                changed-paths.ps1 piped into the application repo's own classifier,
                                  bash .github/scripts/detect-code-changes.sh --from-list - (unchanged). Fail open:
                                  anything but an explicit code=false counts as a code change.
      CI_SQL_SA_PASSWORD          a throwaway sa password for this build's SQL Server containers: upper, lower, digit
                                  and symbol, as SQL Server requires; masked in the log and the variables list.
      ARTIFACTS_DIR               the per-build artifact folder ${CF_VOLUME_PATH}/artifacts/<build id>; the ten newest
                                  build folders are kept on the volume.
    It also creates ${CF_VOLUME_PATH}/.nuget/packages (the NuGet cache on the volume) and one git worktree per
    parallel gate under ${CF_VOLUME_PATH}/wt/: sqlite, analysis, qodana, security, acceptance.

    Exports use cf_export, which Codefresh puts on PATH in every freestyle step; the value travels in the
    environment, never on a command line. Without cf_export an unmasked value is appended to
    ${CF_VOLUME_PATH}/env_vars_to_export, and a masked one fails the step.

    Usage (Codefresh step prepare, in the application checkout):
      pwsh -NoProfile -File <env-repo>/codefresh/apps/workorders/scripts/prepare.ps1 [-Release]

.PARAMETER Release
    The build is a release build: IS_RELEASE=true.

.NOTES
    Exit codes: 0 prepared; 1 failed (CF_VOLUME_PATH or CF_BUILD_ID unset, version.ps1 failed, an export or a
    worktree failed).
#>
[CmdletBinding()]
param(
    [switch] $Release
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$worktrees = @('sqlite', 'analysis', 'qodana', 'security', 'acceptance')

function Exit-Failure([string] $Message) {
    [Console]::Error.WriteLine("prepare.ps1: $Message")
    exit 1
}

# Exports a variable to the later steps: cf_export NAME when Codefresh put it on PATH (the value in the environment),
# else a NAME=value line in ${CF_VOLUME_PATH}/env_vars_to_export (the file cf_export writes). Logs the path, never the value.
function Export-CodefreshVariable([string] $Name, [string] $Value, [switch] $Mask) {
    $cfExport = Get-Command -Name cf_export -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    # Codefresh's cf_export has no shebang line, so pwsh cannot start it ("An error occurred trying to start process"): run it through a shell.
    $cfShell = (Get-Command -Name bash, sh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if ($cfExport) {
        [Environment]::SetEnvironmentVariable($Name, $Value)
        if ($Mask) {
            & $cfShell $cfExport.Source $Name --mask
        }
        else {
            & $cfShell $cfExport.Source $Name
        }
        Write-Host "prepare.ps1: exported $Name with cf_export$(if ($Mask) { ' (masked)' })"
        return
    }
    if ($Mask) {
        Exit-Failure "cf_export is not on PATH, so the masked variable $Name cannot be exported"
    }
    Add-Content -LiteralPath (Join-Path $env:CF_VOLUME_PATH 'env_vars_to_export') -Value "$Name=$Value"
    Write-Host "prepare.ps1: exported $Name to env_vars_to_export (no cf_export on PATH)"
}

# Runs a script of this folder in this process and returns its output; fails when it fails.
function Invoke-SiblingScript([string] $Name) {
    $output = & (Join-Path $PSScriptRoot $Name)
    if ($LASTEXITCODE -ne 0) {
        Exit-Failure "$Name failed"
    }
    return $output
}

# CODE_CHANGED: 'false' only when the classifier answers exactly code=false; anything else, a failure included, is 'true'.
function Get-CodeChanged {
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        $paths = @(& (Join-Path $PSScriptRoot 'changed-paths.ps1'))
        if ($LASTEXITCODE -ne 0) {
            return 'true'
        }
        $answer = @($paths | bash .github/scripts/detect-code-changes.sh --from-list -)
        if ($LASTEXITCODE -ne 0) {
            return 'true'
        }
        $codes = foreach ($line in $answer) {
            if ($line -cmatch '^code=(.*)$') {
                $Matches[1]
            }
        }
        if ((@($codes) -join "`n").TrimEnd("`n") -ceq 'false') {
            return 'false'
        }
        return 'true'
    }
    catch {
        return 'true'
    }
}

foreach ($name in @('CF_VOLUME_PATH', 'CF_BUILD_ID')) {
    if (-not [Environment]::GetEnvironmentVariable($name)) {
        Exit-Failure "$name is not set (Codefresh sets it in every step)"
    }
}

$version = @(Invoke-SiblingScript 'version.ps1') -join "`n"
Export-CodefreshVariable 'VERSION' $version
Export-CodefreshVariable 'BUILD_BUILDNUMBER' $version
Export-CodefreshVariable 'IS_RELEASE' $(if ($Release) { 'true' } else { 'false' })

$codeChanged = Get-CodeChanged
Write-Host "Version $version; code changed: $codeChanged"
Export-CodefreshVariable 'CODE_CHANGED' $codeChanged

$alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
$random = -join (1..24 | ForEach-Object { $alphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)] })
Export-CodefreshVariable 'CI_SQL_SA_PASSWORD' "Cf1-$random" -Mask

$volume = $env:CF_VOLUME_PATH
$artifacts = "$volume/artifacts/$($env:CF_BUILD_ID)"
$null = New-Item -ItemType Directory -Force -Path "$volume/.nuget/packages", $artifacts
Get-ChildItem -LiteralPath "$volume/artifacts" -Directory |
    Sort-Object -Property @{ Expression = 'LastWriteTimeUtc'; Descending = $true }, @{ Expression = 'Name'; Descending = $false } |
    Select-Object -Skip 10 |
    Remove-Item -Recurse -Force
Export-CodefreshVariable 'ARTIFACTS_DIR' $artifacts

foreach ($worktree in $worktrees) {
    if (Test-Path -LiteralPath "$volume/wt/$worktree") {
        Remove-Item -LiteralPath "$volume/wt/$worktree" -Recurse -Force
    }
}
git worktree prune
foreach ($worktree in $worktrees) {
    git worktree add --force --detach "$volume/wt/$worktree" HEAD
}
exit 0
