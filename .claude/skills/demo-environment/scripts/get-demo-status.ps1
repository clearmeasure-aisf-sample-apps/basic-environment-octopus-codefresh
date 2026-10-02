#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Shows, or waits for, the latest Octopus deployment of the system's projects in each environment.

.DESCRIPTION
    Without -Wait: one line per project and environment (release, task state). With -Wait, -Project and -Environment:
    polls until that deployment succeeds (exit 0), fails (exit 1) or -TimeoutMinutes passes (exit 4). -Project is
    "system" (<slug>-system) or "deployable" (<slug>-<deployable>). Reads Octopus with OCTOPUS_ADMIN_API_KEY.

.EXAMPLE
    pwsh -NoProfile -File get-demo-status.ps1 -Config ./demo.acme.json -Wait -Project system -Environment tdd
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Config,
    [ValidateSet('system', 'deployable')] [string] $Project = 'system',
    [string] $Environment = '',
    [switch] $Wait,
    [int] $TimeoutMinutes = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'demo-common.ps1')

$demo = Read-DemoConfig -Path $Config
$names = Get-DemoName -Config $demo
$state = Read-DemoState -Config $demo
if (-not $state.ContainsKey('octopus')) {
    throw 'Run new-octopus-foothold.ps1 first.'
}
$spaceId = [string] $state.octopus.spaceId
$projects = @{ system = $names.SystemProject; deployable = $names.DeployableProject }

if (-not $Wait) {
    foreach ($key in 'system', 'deployable') {
        foreach ($environment in $demo.plannedEnvironments) {
            try {
                $result = Get-OctopusProjectState -Config $demo -SpaceId $spaceId -Project $projects[$key] -Environment ([string] $environment.name)
                Write-Host ('{0,-24} {1,-6} {2,-14} {3}' -f $result.Project, $result.Environment, $result.State, $result.Release)
            }
            catch {
                Write-Host ('{0,-24} {1,-6} {2}' -f $projects[$key], $environment.name, 'not created yet')
            }
        }
    }
    return
}

if (-not $Environment) {
    throw '-Wait needs -Environment.'
}
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
Write-Step "Waiting for $($projects[$Project]) in $Environment (up to $TimeoutMinutes minutes)"
while ((Get-Date) -lt $deadline) {
    try {
        $result = Get-OctopusProjectState -Config $demo -SpaceId $spaceId -Project $projects[$Project] -Environment $Environment
    }
    catch {
        $result = @{ State = 'NotCreated'; Release = '' }
    }
    switch ($result.State) {
        'Success' {
            Write-Pass "$($projects[$Project]) $($result.Release) in $Environment"
            exit 0
        }
        { $_ -in 'Failed', 'Canceled', 'TimedOut' } {
            Write-Fail "$($projects[$Project]) $($result.Release) in ${Environment}: $($result.State) ($($demo.octopus.url)/app#/$spaceId/tasks/$($result.TaskId))"
            exit 1
        }
        default {
            Write-Host "$(Get-Date -Format HH:mm:ss) $($result.State) $($result.Release)"
        }
    }
    Start-Sleep -Seconds 30
}
Write-Fail "timed out after $TimeoutMinutes minutes"
exit 4
