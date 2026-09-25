#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Stages the lean Docker build contexts of the sandbox fixture app.

.DESCRIPTION
    Contract §7.0 "Conformance": images apps/sandbox/web and apps/sandbox/migrator.
      <out>/web/       Dockerfile (containers/apps/sandbox/web/) + publish/ (src/Sandbox.Web)
      <out>/migrator/  Dockerfile and migrate.sh (containers/apps/sandbox/migrator/) + publish/
                       (src/Sandbox.Migrator) + scripts/ (db/scripts, and db/toggles/*.sql when the
                       commit carries the marker toggles/failing-migration; CAP-GIT-010)
    Run it from the sandbox checkout after `dotnet build -c Release`.

    Usage: pwsh -NoProfile -File stage-images.ps1 [-Out <dir>] [-Repo <dir>] [-Containers <dir>]

.PARAMETER Out
    The contexts folder (default: ${CF_VOLUME_PATH}/image-contexts, else ./image-contexts).

.PARAMETER Repo
    The sandbox checkout (default: the working directory).

.PARAMETER Containers
    The image folder of the environment repo (default: containers/apps/sandbox, found from this script).

.OUTPUTS
    The contexts; the log goes to standard error.

.NOTES
    Exit codes: 0 staged; 1 failed (no Sandbox.sln, a missing build output, Dockerfile or script, a failing tool).
#>
[CmdletBinding()]
param(
    [string] $Out = "$(if ($env:CF_VOLUME_PATH) { $env:CF_VOLUME_PATH } else { '.' })/image-contexts",
    [string] $Repo = $PWD.ProviderPath,
    [string] $Containers = (Join-Path $PSScriptRoot '../../../../containers/apps/sandbox')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$executable = [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("stage-images.ps1: $Message")
}

function Exit-Failure([string] $Message) {
    Write-Note $Message
    exit 1
}

# Recreates one context folder under the output folder.
function New-ContextFolder([string] $Name) {
    $folder = Join-Path $Out $Name
    if (Test-Path -LiteralPath $folder) {
        Remove-Item -LiteralPath $folder -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Force -Path $folder
    return $folder
}

# The *.sql files of a folder, sorted like a shell glob; fails when there is none (cp with an unmatched glob).
function Copy-SqlScript([string] $Folder, [string] $Target) {
    $scripts = @(Get-ChildItem -LiteralPath $Folder -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -clike '*.sql' } | Sort-Object -Property Name -Culture ([System.Globalization.CultureInfo]::InvariantCulture))
    if ($scripts.Count -eq 0) {
        Exit-Failure "no *.sql under $Folder"
    }
    foreach ($script in $scripts) {
        Copy-Item -LiteralPath $script.FullName -Destination $Target -Force
    }
}

$Repo = (Resolve-Path -LiteralPath $Repo).ProviderPath
if (-not (Test-Path -LiteralPath (Join-Path $Repo 'Sandbox.sln') -PathType Leaf)) {
    Exit-Failure "$Repo holds no Sandbox.sln"
}
$null = New-Item -ItemType Directory -Force -Path $Out
$Out = (Resolve-Path -LiteralPath $Out).ProviderPath

$web = New-ContextFolder 'web'
dotnet publish "$Repo/src/Sandbox.Web/Sandbox.Web.csproj" --configuration Release --no-restore --no-build --nologo -o "$web/publish"
if (-not (Test-Path -LiteralPath "$web/publish/Sandbox.Web.dll" -PathType Leaf)) {
    Exit-Failure 'web: publish/ is missing Sandbox.Web.dll'
}
Copy-Item -LiteralPath "$Containers/web/Dockerfile" -Destination "$web/Dockerfile" -Force

$migrator = New-ContextFolder 'migrator'
dotnet publish "$Repo/src/Sandbox.Migrator/Sandbox.Migrator.csproj" --configuration Release --no-restore --no-build --nologo -o "$migrator/publish"
if (-not (Test-Path -LiteralPath "$migrator/publish/Sandbox.Migrator.dll" -PathType Leaf)) {
    Exit-Failure 'migrator: publish/ is missing Sandbox.Migrator.dll'
}
$null = New-Item -ItemType Directory -Force -Path "$migrator/scripts"
Copy-SqlScript "$Repo/db/scripts" "$migrator/scripts"
if (Test-Path -LiteralPath "$Repo/toggles/failing-migration" -PathType Leaf) {
    Write-Note 'toggles/failing-migration is on; the migrator image carries a failing script'
    Copy-SqlScript "$Repo/db/toggles" "$migrator/scripts"
}
Copy-Item -LiteralPath "$Containers/migrator/Dockerfile" -Destination "$migrator/Dockerfile" -Force
Copy-Item -LiteralPath "$Containers/migrator/migrate.sh" -Destination "$migrator/migrate.sh" -Force
[System.IO.File]::SetUnixFileMode("$migrator/migrate.sh", $executable)

$count = @(Get-ChildItem -LiteralPath "$migrator/scripts" -Recurse -Force -File | Where-Object { $_.Name -clike '*.sql' }).Count
Write-Note "contexts ready under ${Out}: web, migrator ($count script(s))"
exit 0
