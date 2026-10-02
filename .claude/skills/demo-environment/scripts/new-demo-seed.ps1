#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Phase 2 of the demo-environment skill: applies the Azure seed (templates/system/bootstrap/seed.bicep).

.DESCRIPTION
    As the operator (subscription Owner, az login):
      1. Registers the resource providers the system uses and waits for them.
      2. Deploys the seed at subscription scope: both resource groups, the registry, the Terraform state account and
         every identity with its federated credentials and grants, for all planned environments.
    The Octopus subjects need the space slug, so phase 1 (new-octopus-foothold.ps1) runs first. Writes the
    identifiers to the state for phase 3. Safe to re-run: a deployment of the same template changes nothing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Config
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
    throw 'Run new-octopus-foothold.ps1 first: the seed needs the Octopus space slug.'
}

az account set --subscription $demo.azure.subscriptionId

Write-Step 'Resource providers'
$providers = 'Microsoft.App', 'Microsoft.ContainerRegistry', 'Microsoft.KeyVault', 'Microsoft.Sql', 'Microsoft.Storage',
    'Microsoft.ManagedIdentity', 'Microsoft.OperationalInsights', 'Microsoft.Insights'
foreach ($provider in $providers) {
    $registration = (az provider show --namespace $provider --query registrationState --output tsv).Trim()
    if ($registration -ne 'Registered') {
        az provider register --namespace $provider --wait --output none
        Write-Pass "$provider registered"
    }
    else {
        Write-Pass "$provider"
    }
}

Write-Step "Seed of $($names.Slug) in $($demo.azure.location)"
$parameters = @{
    '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
    contentVersion = '1.0.0.0'
    parameters     = @{
        slug                     = @{ value = $names.Slug }
        location                 = @{ value = [string] $demo.azure.location }
        nonprodResourceGroupName = @{ value = [string] $demo.azure.resourceGroups.nonprod }
        prodResourceGroupName    = @{ value = [string] $demo.azure.resourceGroups.prod }
        githubOrg                = @{ value = [string] $demo.githubOrg }
        systemRepository         = @{ value = $names.SystemRepository }
        appRepositories          = @{ value = @($names.AppRepository) }
        octopusUrl               = @{ value = [string] $demo.octopus.url }
        octopusSpaceSlug         = @{ value = [string] $state.octopus.spaceSlug }
        octopusProjectSlugs      = @{ value = @($names.SystemProject, $names.DeployableProject) }
        environments             = @{ value = @($demo.plannedEnvironments) }
    }
}
$parametersFile = Join-Path ([IO.Path]::GetTempPath()) "seed-$($names.Slug)-$([Guid]::NewGuid().ToString('N')).json"
$parameters | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $parametersFile -Encoding utf8NoBOM
try {
    $template = Join-Path $SkillRoot 'templates' 'system' 'bootstrap' 'seed.bicep'
    $deployment = az deployment sub create --name "seed-$($names.Slug)" --location $demo.azure.location `
        --template-file $template --parameters "@$parametersFile" --output json | ConvertFrom-Json -AsHashtable
}
finally {
    Remove-Item -LiteralPath $parametersFile -Force -ErrorAction SilentlyContinue
}

$outputs = $deployment.properties.outputs
$seed = @{}
foreach ($key in $outputs.Keys) {
    $seed[$key] = $outputs[$key].value
}
Save-DemoState -Config $demo -Phase 'seed' -Values $seed
Write-Pass "registry $($seed.registry.loginServer), state account $($seed.terraformState.storageAccount)"
Write-Pass "deploy identities: $($seed.identities.deploy.nonprod.name), $($seed.identities.deploy.prod.name)"
Write-Host 'New role assignments and federated credentials take a few minutes to apply; the first pipeline run retries.'
