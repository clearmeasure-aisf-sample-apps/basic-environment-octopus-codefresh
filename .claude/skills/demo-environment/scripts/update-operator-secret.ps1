#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Renews the operator identity's Azure client secret before it expires; run by the operator identity itself.

.DESCRIPTION
    Signed in to az as the operator identity (new-operator-identity.ps1, operator-identity.md):
      1. Reads the app registration's secrets (Microsoft Graph, Application.ReadWrite.OwnedBy on an app it owns).
      2. Returns when the newest secret is valid for more than -RenewDays, unless -Force.
      3. Adds a new secret, signs in to az with it, and removes every other secret of the app.
    The new secret goes from Graph's response to az's profile without being printed, written to a lasting file or
    passed as an argument. Run it weekly from a timer (operator-identity.md shows one).

.EXAMPLE
    pwsh -NoProfile -File .claude/skills/demo-environment/scripts/update-operator-secret.ps1
#>
[CmdletBinding()]
param(
    [int] $RenewDays = 45,
    [int] $LifetimeDays = 180,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'demo-common.ps1')

$operatorFile = Join-Path $HOME '.config' 'demo-environment' 'operator.json'
if (-not (Test-Path -LiteralPath $operatorFile)) {
    throw "$operatorFile not found: run new-operator-identity.ps1 first."
}
$operator = Get-Content -LiteralPath $operatorFile -Raw | ConvertFrom-Json -AsHashtable
$account = az account show --output json | ConvertFrom-Json -AsHashtable
if ($account.user.type -ne 'servicePrincipal' -or $account.user.name -ne $operator.appId) {
    throw "az is signed in as $($account.user.name), not as the operator identity $($operator.appId)."
}

$graph = "https://graph.microsoft.com/v1.0/applications/$($operator.appObjectId)"
$application = az rest --method GET --uri "$graph`?`$select=passwordCredentials" --output json | ConvertFrom-Json -AsHashtable
$secrets = @($application.passwordCredentials)
$newest = $secrets | Sort-Object { [datetimeoffset] $_.endDateTime } -Descending | Select-Object -First 1
if ($newest -and -not $Force) {
    $daysLeft = ([datetimeoffset] $newest.endDateTime - [datetimeoffset]::UtcNow).TotalDays
    if ($daysLeft -gt $RenewDays) {
        Write-Pass "secret of $($operator.name) valid until $(([datetimeoffset] $newest.endDateTime).ToString('yyyy-MM-dd')); nothing to renew"
        return
    }
}

Write-Step "New secret for $($operator.name)"
$end = [datetimeoffset]::UtcNow.AddDays($LifetimeDays)
$body = @{ passwordCredential = @{ displayName = "operator $([datetimeoffset]::UtcNow.ToString('yyyy-MM-dd'))"; endDateTime = $end.ToString('o') } } |
    ConvertTo-Json -Compress
# A Graph permission granted minutes ago may not be in force yet (first run from new-operator-identity.ps1).
$added = $null
for ($attempt = 1; $attempt -le 10 -and -not $added; $attempt++) {
    $PSNativeCommandUseErrorActionPreference = $false
    $response = az rest --method POST --uri "$graph/addPassword" --headers 'Content-Type=application/json' --body $body --output json 2>$null
    $ok = $LASTEXITCODE -eq 0
    $PSNativeCommandUseErrorActionPreference = $true
    if ($ok) {
        $added = $response | ConvertFrom-Json -AsHashtable
    }
    elseif ($attempt -eq 10) {
        throw "Graph refused addPassword for $($operator.name) for five minutes: the identity must own its app registration and hold Application.ReadWrite.OwnedBy (operator-identity.md)."
    }
    else {
        Start-Sleep -Seconds 30
    }
}

Connect-AzServicePrincipal -AppId $operator.appId -TenantId $operator.tenantId -SubscriptionId $operator.subscriptionId -Secret $added.secretText
$added.secretText = $null
Write-Pass "az signed in with the new secret (valid until $($end.ToString('yyyy-MM-dd')))"

foreach ($old in $secrets | Where-Object { $_.keyId -ne $added.keyId }) {
    $removeBody = @{ keyId = $old.keyId } | ConvertTo-Json -Compress
    az rest --method POST --uri "$graph/removePassword" --headers 'Content-Type=application/json' --body $removeBody --output none
    Write-Pass "removed the secret '$($old.displayName)'"
}
