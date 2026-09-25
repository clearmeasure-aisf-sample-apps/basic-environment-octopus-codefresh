#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Step stage_dockerfiles of <app>/release: copies the Dockerfile of each image from the environment repo into the
    application checkout.

.DESCRIPTION
    The app repo holds only its source (ADR-D18); each image's Dockerfile lives in the environment repo under
    containers/apps/<app>/<image>/Dockerfile and builds from the app checkout (multi-stage builds work). The copies land
    in .platform/<image>.Dockerfile of the working directory (the application checkout), where the image build steps
    read them.

    Usage (Codefresh step stage_dockerfiles, in the application checkout):
      pwsh -NoProfile -File <env-repo>/codefresh/apps/<app>/scripts/stage-dockerfiles.ps1 -Image web,migrator

.PARAMETER Image
    The images, a comma-separated list from a shell (for example web,migrator).

.PARAMETER Containers
    The image folder of the environment repo (default: containers/apps/<app>, found from this script).

.NOTES
    Exit codes: 0 copied; 1 failed (a Dockerfile is missing).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]] $Image,
    [string] $Containers = (Join-Path $PSScriptRoot '../../../../containers/apps/<app>')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$null = New-Item -ItemType Directory -Force -Path '.platform'
foreach ($name in @($Image | ForEach-Object { $_ -split ',' } | Where-Object { $_ })) {
    Copy-Item -LiteralPath "$Containers/$name/Dockerfile" -Destination ".platform/$name.Dockerfile" -Force
}
exit 0
