#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    One-time setup of the operator identity's Azure side: a service principal the AI sessions sign in as, so no
    person's login runs the demo-environment skill.

.DESCRIPTION
    Run it once, in the shell of the operator account (the Linux user the AI sessions run as, operator-identity.md),
    after a person has signed in there with az login as Owner of the subscription and an Entra role that may grant
    application permissions (Privileged Role Administrator or Global Administrator):
      1. App registration -Name and its service principal.
      2. The service principal owns its own app registration and holds the Graph permission
         Application.ReadWrite.OwnedBy, so it can renew its secret itself (update-operator-secret.ps1).
      3. Owner on the subscription (the seed creates resource groups and role assignments).
      4. A first client secret, created by the person.
      5. The person's login leaves this az profile; az signs in as the service principal with that secret.
      6. update-operator-secret.ps1 -Force: the service principal replaces that secret itself, which proves step 2.
    The secret is never printed, passed as an argument or kept outside az's profile. Identifiers go to
    ~/.config/demo-environment/operator.json. Safe to re-run: every step finds what an earlier run created.

.EXAMPLE
    pwsh -NoProfile -File .claude/skills/demo-environment/scripts/new-operator-identity.ps1 -SubscriptionId <id>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SubscriptionId,
    [string] $Name = 'cm-ai-ops',
    [int] $LifetimeDays = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'demo-common.ps1')

$graphAppId = '00000003-0000-0000-c000-000000000000'
$configFolder = Join-Path $HOME '.config' 'demo-environment'
$operatorFile = Join-Path $configFolder 'operator.json'
New-Item -ItemType Directory -Path $configFolder -Force | Out-Null
if (-not $IsWindows) { chmod 700 $configFolder }

$account = az account show --subscription $SubscriptionId --output json | ConvertFrom-Json -AsHashtable
if ($account.user.type -eq 'servicePrincipal') {
    if ((Test-Path -LiteralPath $operatorFile) -and (Get-Content -LiteralPath $operatorFile -Raw | ConvertFrom-Json).appId -eq $account.user.name) {
        Write-Pass "az is already signed in as the operator identity $Name ($($account.user.name))"
        & (Join-Path $PSScriptRoot 'update-operator-secret.ps1')
        return
    }
    throw "az is signed in as the service principal $($account.user.name); sign in as a person (az login) for the one-time setup."
}
$tenantId = [string] $account.tenantId
Write-Step "Operator identity $Name in tenant $tenantId, set up by $($account.user.name)"

Write-Step 'App registration and service principal'
$application = @(az ad app list --display-name $Name --output json | ConvertFrom-Json -AsHashtable) | Select-Object -First 1
if (-not $application) {
    $application = az ad app create --display-name $Name --sign-in-audience AzureADMyOrg --output json | ConvertFrom-Json -AsHashtable
    Write-Pass "created app registration $Name ($($application.appId))"
}
else {
    Write-Pass "app registration $Name ($($application.appId))"
}
$appId = [string] $application.appId
$appObjectId = [string] $application.id
$principal = @(az ad sp list --filter "appId eq '$appId'" --output json | ConvertFrom-Json -AsHashtable) | Select-Object -First 1
if (-not $principal) {
    $principal = az ad sp create --id $appId --output json | ConvertFrom-Json -AsHashtable
    Write-Pass 'created the service principal'
}
else {
    Write-Pass 'service principal'
}
$principalId = [string] $principal.id

Write-Step 'Self-renewal: owner of its app registration, Application.ReadWrite.OwnedBy'
$owners = @(az ad app owner list --id $appId --query '[].id' --output json | ConvertFrom-Json)
if ($owners -notcontains $principalId) {
    az ad app owner add --id $appId --owner-object-id $principalId
}
Write-Pass 'the service principal owns its app registration'
$graphPrincipal = az ad sp show --id $graphAppId --output json | ConvertFrom-Json -AsHashtable
$roleId = @($graphPrincipal.appRoles | Where-Object { $_.value -eq 'Application.ReadWrite.OwnedBy' })[0].id
$assignments = az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$principalId/appRoleAssignments" --output json |
    ConvertFrom-Json -AsHashtable
if (-not @($assignments.value | Where-Object { $_.appRoleId -eq $roleId -and $_.resourceId -eq $graphPrincipal.id })) {
    $grant = @{ principalId = $principalId; resourceId = $graphPrincipal.id; appRoleId = $roleId } | ConvertTo-Json -Compress
    az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$principalId/appRoleAssignments" `
        --headers 'Content-Type=application/json' --body $grant --output none
}
Write-Pass 'Graph Application.ReadWrite.OwnedBy granted (admin consent)'

Write-Step "Owner on subscription $($account.name)"
$scope = "/subscriptions/$SubscriptionId"
$owner = az role assignment list --scope $scope --role Owner --fill-principal-name false `
    --query "[?principalId=='$principalId'] | length(@)" --output tsv
if ([int] $owner -lt 1) {
    az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
        --role Owner --scope $scope --output none
}
Write-Pass "Owner on $scope"

@{
    name           = $Name
    appId          = $appId
    appObjectId    = $appObjectId
    principalId    = $principalId
    tenantId       = $tenantId
    subscriptionId = $SubscriptionId
} | ConvertTo-Json | Set-Content -LiteralPath $operatorFile -Encoding utf8NoBOM
Write-Pass "identifiers in $operatorFile"

Write-Step 'First secret, then az as the operator identity'
$end = [datetimeoffset]::UtcNow.AddDays($LifetimeDays)
$body = @{ passwordCredential = @{ displayName = 'operator setup'; endDateTime = $end.ToString('o') } } | ConvertTo-Json -Compress
$added = az rest --method POST --uri "https://graph.microsoft.com/v1.0/applications/$appObjectId/addPassword" `
    --headers 'Content-Type=application/json' --body $body --output json | ConvertFrom-Json -AsHashtable
$person = [string] $account.user.name
az account clear
Write-Pass "$person signed out of this az profile"
Connect-AzServicePrincipal -AppId $appId -TenantId $tenantId -SubscriptionId $SubscriptionId -Secret $added.secretText
$added.secretText = $null
Write-Pass "az signed in as $Name"

Write-Step 'Self-renewal test: the operator identity replaces its first secret'
& (Join-Path $PSScriptRoot 'update-operator-secret.ps1') -Force -LifetimeDays $LifetimeDays
Write-Host "Operator identity $Name ($appId) is ready. Next: set-operator-octopus-key.ps1 and the GitHub login (operator-identity.md)."
