#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Tears a demo system down: Azure, GitHub and Octopus, in the order that leaves nothing behind.

.DESCRIPTION
    Destructive. Without -Delete it only prints what it would delete. In order:
      1. Azure: every deployment stack stack-<slug>-<env> (deleting its resources; the stacks' deny settings would
         otherwise block the resource-group delete), then both resource groups, then a purge of the deleted vaults of
         the system (their names stay reserved for 7 days otherwise). Deleting the databases frees their free-offer
         allowance.
      2. GitHub: the app and system repositories (gh needs the delete_repo scope) and the board.
      3. Octopus: the space, when this demo created it (-KeepSpace keeps it): its task queue is stopped first, as
         Octopus requires.
      4. The local state ~/.demo-environment/<slug>.
    The operator's own credentials stay untouched; revoke them separately when the class is over.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Config,
    [switch] $Delete,
    [switch] $KeepSpace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'demo-common.ps1')

$demo = Read-DemoConfig -Path $Config
$names = Get-DemoName -Config $demo
$state = Read-DemoState -Config $demo
$groups = @([string] $demo.azure.resourceGroups.nonprod, [string] $demo.azure.resourceGroups.prod)
$repositories = @("$($demo.githubOrg)/$($names.AppRepository)", "$($demo.githubOrg)/$($names.SystemRepository)")

if (-not $Delete) {
    Write-Host "Would delete, for system $($names.Slug):"
    Write-Host "  Azure     stacks stack-$($names.Slug)-<env>; resource groups $($groups -join ', '); deleted vaults tagged system=$($names.Slug)"
    Write-Host "  GitHub    $($repositories -join ', ')$(if ($state.ContainsKey('system') -and $state.system.boardNumber) { "; board $($state.system.boardUrl)" })"
    Write-Host "  Octopus   $(if ($KeepSpace) { 'nothing (-KeepSpace)' } else { "space '$($demo.octopus.spaceName)'" })"
    Write-Host "  Local     ~/.demo-environment/$($names.Slug)"
    Write-Host 'Run again with -Delete to delete.'
    return
}

az account set --subscription $demo.azure.subscriptionId

Write-Step 'Azure: deployment stacks'
foreach ($group in $groups) {
    $PSNativeCommandUseErrorActionPreference = $false
    $stacks = az stack group list --resource-group $group --query "[?starts_with(name, 'stack-$($names.Slug)-')].name" --output tsv 2>$null
    $PSNativeCommandUseErrorActionPreference = $true
    foreach ($stack in @($stacks | Where-Object { $_ })) {
        az stack group delete --name $stack --resource-group $group --action-on-unmanage deleteResources --yes --output none
        Write-Pass "deleted stack $stack"
    }
}

Write-Step 'Azure: resource groups'
foreach ($group in $groups) {
    $exists = (az group exists --name $group).Trim() -eq 'true'
    if ($exists) {
        az group delete --name $group --yes --output none
        Write-Pass "deleted $group"
    }
    else {
        Write-Skip "$group does not exist"
    }
}

Write-Step 'Azure: deleted vaults'
$deleted = az keyvault list-deleted --resource-type vault --output json | ConvertFrom-Json -AsHashtable
foreach ($vault in @($deleted | Where-Object { $_.properties.tags -and $_.properties.tags['system'] -eq $names.Slug })) {
    az keyvault purge --name $vault.name --location $vault.properties.location --output none
    Write-Pass "purged $($vault.name)"
}

Write-Step 'GitHub'
foreach ($repository in $repositories) {
    if (Test-GitHubRepository -FullName $repository) {
        gh repo delete $repository --yes
        Write-Pass "deleted $repository"
    }
    else {
        Write-Skip "$repository does not exist"
    }
}
if ($state.ContainsKey('system') -and $state.system.boardNumber) {
    gh project delete $state.system.boardNumber --owner $demo.githubOrg
    Write-Pass "deleted board $($state.system.boardUrl)"
}

Write-Step 'Octopus'
if ($KeepSpace -or -not $state.ContainsKey('octopus')) {
    Write-Skip 'space kept'
}
else {
    $space = Invoke-OctopusApi -Config $demo -Path "/api/spaces/$($state.octopus.spaceId)"
    $space.TaskQueueStopped = $true
    Invoke-OctopusApi -Config $demo -Path "/api/spaces/$($space.Id)" -Method Put -Body $space | Out-Null
    Invoke-OctopusApi -Config $demo -Path "/api/spaces/$($space.Id)" -Method Delete | Out-Null
    Write-Pass "deleted space $($space.Name) ($($space.Id))"
    $user = $state.octopus.serviceUserId
    if ($user) {
        Invoke-OctopusApi -Config $demo -Path "/api/users/$user" -Method Delete | Out-Null
        Write-Pass "deleted service account $($names.ServiceAccount)"
    }
}

Remove-Item -LiteralPath (Split-Path -Parent (Get-DemoStatePath -Config $demo)) -Recurse -Force
Write-Pass "removed local state of $($names.Slug)"
