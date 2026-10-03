#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Phase 1 of the demo-environment skill: the Octopus space and the service account the pipelines sign in as.

.DESCRIPTION
    Octopus cannot create its own first identity, so this is the one Octopus step the operator runs (with the
    operator's Octopus key, Get-OctopusApiKey). Everything else in Octopus is created by the system repository's
    pipeline (octopus/ Terraform) on its first run.
      1. The service account <slug>-github (a service user, no password, no API key).
      2. The space of the demo file, created with the service account and the caller (the operator identity, which
         reads and promotes in the space later) as Space Managers; an existing space with that name is reused and both
         are added to its Space Managers.
      3. Two OIDC identities on the account, so GitHub Actions can sign in without a stored key:
           repo:<org>/<slug>-system:environment:octopus       (job octopus-apply and system-release)
           repo:<org>/<app-repository>:environment:release    (the app's release workflow)
    Writes the space ID and slug and the account's external ID (the service_account_id of OctopusDeploy/login) to the
    state. Safe to re-run: existing objects are found by name.
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
$githubIssuer = 'https://token.actions.githubusercontent.com'

Write-Step "Service account $($names.ServiceAccount)"
$users = Invoke-OctopusApi -Config $demo -Path "/api/users?filter=$([uri]::EscapeDataString($names.ServiceAccount))&take=100"
$account = @($users.Items | Where-Object { $_.Username -eq $names.ServiceAccount }) | Select-Object -First 1
if (-not $account) {
    $account = Invoke-OctopusApi -Config $demo -Path '/api/users' -Method Post -Body @{
        Username    = $names.ServiceAccount
        DisplayName = "$($demo.name) (GitHub Actions)"
        IsService   = $true
        IsActive    = $true
    }
    Write-Pass "created $($account.Id)"
}
else {
    Write-Pass "exists ($($account.Id))"
}

Write-Step "Space '$($demo.octopus.spaceName)'"
$operator = Invoke-OctopusApi -Config $demo -Path '/api/users/me'
$managers = @($account.Id, $operator.Id) | Select-Object -Unique
$spaces = Invoke-OctopusApi -Config $demo -Path "/api/spaces?partialName=$([uri]::EscapeDataString($demo.octopus.spaceName))&take=100"
$space = @($spaces.Items | Where-Object { $_.Name -eq $demo.octopus.spaceName }) | Select-Object -First 1
if (-not $space) {
    $space = Invoke-OctopusApi -Config $demo -Path '/api/spaces' -Method Post -Body @{
        Name                     = $demo.octopus.spaceName
        Description              = "$($demo.name): system $($names.Slug), managed from $($demo.githubOrg)/$($names.SystemRepository)."
        SpaceManagersTeams       = @()
        SpaceManagersTeamMembers = @($managers)
        IsDefault                = $false
        TaskQueueStopped         = $false
    }
    Write-Pass "created $($space.Id) ($($space.Slug))"
}
elseif (@($managers | Where-Object { @($space.SpaceManagersTeamMembers) -notcontains $_ }).Count -gt 0) {
    $space.SpaceManagersTeamMembers = @(@($space.SpaceManagersTeamMembers) + $managers | Select-Object -Unique)
    $space = Invoke-OctopusApi -Config $demo -Path "/api/spaces/$($space.Id)" -Method Put -Body $space
    Write-Pass "exists ($($space.Id)); $($names.ServiceAccount) and $($operator.Username) added to its Space Managers"
}
else {
    Write-Pass "exists ($($space.Id)); $($names.ServiceAccount) and $($operator.Username) are Space Managers"
}

Write-Step 'OIDC identities'
$subjects = [ordered] @{
    "$($names.SystemRepository)-octopus" = "repo:$($demo.githubOrg)/$($names.SystemRepository):environment:octopus"
    "$($names.AppRepository)-release"    = "repo:$($demo.githubOrg)/$($names.AppRepository):environment:release"
}
$identities = Invoke-OctopusApi -Config $demo -Path "/api/serviceaccounts/$($account.Id)/oidcidentities/v1?skip=0&take=100"
foreach ($entry in $subjects.GetEnumerator()) {
    if (@($identities.OidcIdentities | Where-Object { $_.Subject -eq $entry.Value -and $_.Issuer -eq $githubIssuer })) {
        Write-Pass "$($entry.Value) exists"
        continue
    }
    Invoke-OctopusApi -Config $demo -Path "/api/serviceaccounts/$($account.Id)/oidcidentities/create/v1" -Method Post -Body @{
        ServiceAccountId = $account.Id
        Name             = $entry.Key
        Issuer           = $githubIssuer
        Subject          = $entry.Value
    } | Out-Null
    Write-Pass "$($entry.Value) created"
}
$identities = Invoke-OctopusApi -Config $demo -Path "/api/serviceaccounts/$($account.Id)/oidcidentities/v1?skip=0&take=100"
if (-not $identities.ExternalId) {
    throw "Octopus returned no ExternalId for $($names.ServiceAccount); open the account's OpenID Connect section and copy the service account ID shown in its OctopusDeploy/login snippet into the state."
}

Save-DemoState -Config $demo -Phase 'octopus' -Values @{
    url              = [string] $demo.octopus.url
    spaceId          = [string] $space.Id
    spaceSlug        = [string] $space.Slug
    serviceAccountId = [string] $identities.ExternalId
    serviceUserId    = [string] $account.Id
}
Write-Pass "state saved: space $($space.Id) ($($space.Slug)), service account $($identities.ExternalId)"
