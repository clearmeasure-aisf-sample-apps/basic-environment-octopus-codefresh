#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Progression step: turns a capability on or off in one environment, as a pull request to the system repository.

.DESCRIPTION
    A capability is a module infra/modules/<capability>.bicep plus one condition in infra/main.bicep; "baseline" is
    always on. The template ships "telemetry" (Log Analytics and Application Insights; the app's OpenTelemetry export
    starts once APPLICATIONINSIGHTS_CONNECTION_STRING is set). The pull request changes the environment's
    capabilities in system.json; its preview shows the resources the capability adds or removes. After the merge the
    new <slug>-system release reaches the first environment by itself and later ones by promotion.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Config,
    [Parameter(Mandatory)] [string] $Environment,
    [Parameter(Mandatory)] [string] $Capability,
    [switch] $Remove,
    [switch] $Merge
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'demo-common.ps1')

$demo = Read-DemoConfig -Path $Config
if ($Capability -eq 'baseline') {
    throw 'baseline is always on.'
}
$verb = if ($Remove) { 'Remove' } else { 'Add' }

$change = {
    param([string] $Directory)
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'infra' 'modules' "$Capability.bicep"))) {
        throw "The system repository has no infra/modules/$Capability.bicep: add the module in its own pull request first."
    }
    $file = Join-Path $Directory 'system.json'
    $system = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
    $entry = @($system.environments | Where-Object { $_.name -eq $Environment }) | Select-Object -First 1
    if (-not $entry) {
        throw "$Environment is not in system.json."
    }
    $current = @($entry.capabilities)
    $entry.capabilities = if ($Remove) { @($current | Where-Object { $_ -ne $Capability }) } else { @($current + $Capability | Select-Object -Unique) }
    ($system | ConvertTo-Json -Depth 20) + "`n" | Set-Content -LiteralPath $file -Encoding utf8NoBOM -NoNewline
}.GetNewClosure()

New-SystemPullRequest -Config $demo -Branch "$($verb.ToLowerInvariant())-$Capability-$Environment" `
    -Title "$verb capability $Capability in $Environment" `
    -Body "$(if ($Remove) { 'Removes' } else { 'Adds' }) $Capability $(if ($Remove) { 'from' } else { 'to' }) the capabilities of $Environment in system.json. The env-checks preview lists the Azure resources this changes." `
    -Change $change -Merge:$Merge | Out-Null
