#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Promotes the latest release of a system project to an environment (the person's step of the lifecycle).

.DESCRIPTION
    The lifecycle deploys the first environment automatically; every later environment is a promotion. In a demo the
    operator clicks Promote in Octopus, or runs this script, which creates the same deployment through the API:
      -Project system       the environment itself (apply infra/ to it); promote this first for a new environment
      -Project deployable   the app release
    Octopus refuses a promotion the lifecycle does not allow yet. With -Wait it waits for the result.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Config,
    [Parameter(Mandatory)] [ValidateSet('system', 'deployable')] [string] $Project,
    [Parameter(Mandatory)] [string] $Environment,
    [string] $Release = '',
    [switch] $Wait
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'demo-common.ps1')

$demo = Read-DemoConfig -Path $Config
$names = Get-DemoName -Config $demo
$state = Read-DemoState -Config $demo
$spaceId = [string] $state.octopus.spaceId
$slug = if ($Project -eq 'system') { $names.SystemProject } else { $names.DeployableProject }

$projectObject = Invoke-OctopusApi -Config $demo -Path "/api/$spaceId/projects/$slug"
$releases = Invoke-OctopusApi -Config $demo -Path "/api/$spaceId/projects/$($projectObject.Id)/releases?take=20"
$releaseObject = if ($Release) { @($releases.Items | Where-Object { $_.Version -eq $Release }) | Select-Object -First 1 } else { @($releases.Items) | Select-Object -First 1 }
if (-not $releaseObject) {
    throw "No release $(if ($Release) { $Release } else { '' }) of $slug found."
}
$environments = Invoke-OctopusApi -Config $demo -Path "/api/$spaceId/environments?partialName=$Environment&take=100"
$environmentObject = @($environments.Items | Where-Object { $_.Name -eq $Environment }) | Select-Object -First 1
if (-not $environmentObject) {
    throw "Environment $Environment does not exist in Octopus yet: merge its pull request first (the system workflow creates it)."
}

$deployment = Invoke-OctopusApi -Config $demo -Path "/api/$spaceId/deployments" -Method Post -Body @{
    ReleaseId     = $releaseObject.Id
    EnvironmentId = $environmentObject.Id
    Comments      = 'Promoted by the demo-environment skill'
}
Write-Pass "$slug $($releaseObject.Version) to $Environment ($($deployment.TaskId))"

if ($Wait) {
    & (Join-Path $PSScriptRoot 'get-demo-status.ps1') -Config $Config -Project $Project -Environment $Environment -Wait
    exit $LASTEXITCODE
}
