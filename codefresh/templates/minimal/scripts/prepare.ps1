#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Step prepare of <app>/ci and <app>/release: version and artifact folder.

.DESCRIPTION
    Runs in the application checkout (the step's working directory) with the step's environment
    (RELEASE_BRANCH, CF_BRANCH, CF_VOLUME_PATH, CF_BUILD_ID) and exports, for the later steps:
      VERSION        version.ps1 (ADR-C7).
      ARTIFACTS_DIR  the per-build artifact folder ${CF_VOLUME_PATH}/artifacts/<build id>; the ten newest build
                     folders are kept on the volume.

    Exports use cf_export, which Codefresh puts on PATH in every freestyle step; the value travels in the
    environment, never on a command line. Without cf_export the value is appended to
    ${CF_VOLUME_PATH}/env_vars_to_export.

    Usage (Codefresh step prepare, in the application checkout):
      pwsh -NoProfile -File <env-repo>/codefresh/apps/<app>/scripts/prepare.ps1

.NOTES
    Exit codes: 0 prepared; 1 failed (CF_VOLUME_PATH or CF_BUILD_ID unset, version.ps1 failed, an export failed).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

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

foreach ($name in @('CF_VOLUME_PATH', 'CF_BUILD_ID')) {
    if (-not [Environment]::GetEnvironmentVariable($name)) {
        Exit-Failure "$name is not set (Codefresh sets it in every step)"
    }
}

$version = @(Invoke-SiblingScript 'version.ps1') -join "`n"
$volume = $env:CF_VOLUME_PATH
$artifacts = "$volume/artifacts/$($env:CF_BUILD_ID)"
Export-CodefreshVariable 'VERSION' $version
Export-CodefreshVariable 'ARTIFACTS_DIR' $artifacts
$null = New-Item -ItemType Directory -Force -Path $artifacts
Get-ChildItem -LiteralPath "$volume/artifacts" -Directory |
    Sort-Object -Property @{ Expression = 'LastWriteTimeUtc'; Descending = $true }, @{ Expression = 'Name'; Descending = $false } |
    Select-Object -Skip 10 |
    Remove-Item -Recurse -Force
Write-Host "<app> $version"
exit 0
