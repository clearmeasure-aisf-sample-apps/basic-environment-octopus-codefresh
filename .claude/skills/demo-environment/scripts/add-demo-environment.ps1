#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Progression step: adds an environment to the system as a pull request to the system repository.

.DESCRIPTION
    The demo's "a new environment is a change" step. The pull request appends the environment (from the demo file's
    plannedEnvironments, so the seed already made its identities) to system.json with the baseline capability and
    adds environments/<env>/versions.json with {}. Its checks preview the new environment. After the merge the system
    workflow creates the Octopus environment and lifecycle phase and a new <slug>-system release; then promote:
      invoke-demo-promotion.ps1 -Project system -Environment <env> -Wait       the environment appears in Azure
      invoke-demo-promotion.ps1 -Project deployable -Environment <env> -Wait   the app runs there
    With -Merge the script waits for the required checks and merges.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Config,
    [Parameter(Mandatory)] [string] $Environment,
    [switch] $Merge
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'demo-common.ps1')

$demo = Read-DemoConfig -Path $Config
$planned = @($demo.plannedEnvironments | Where-Object { $_.name -eq $Environment }) | Select-Object -First 1
if (-not $planned) {
    throw "$Environment is not in plannedEnvironments: the seed made identities only for those (add it there and re-run new-demo-seed.ps1)."
}

$change = {
    param([string] $Directory)
    $file = Join-Path $Directory 'system.json'
    $system = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
    if (@($system.environments | Where-Object { $_.name -eq $Environment })) {
        throw "$Environment is already in system.json."
    }
    $system.environments = @($system.environments) + [ordered] @{ name = $Environment; tier = [string] $planned.tier; capabilities = @('baseline') }
    ($system | ConvertTo-Json -Depth 20) + "`n" | Set-Content -LiteralPath $file -Encoding utf8NoBOM -NoNewline
    $folder = New-Item -ItemType Directory -Path (Join-Path $Directory 'environments' $Environment) -Force
    Set-Content -LiteralPath (Join-Path $folder 'versions.json') -Value '{}' -Encoding utf8NoBOM
}.GetNewClosure()

New-SystemPullRequest -Config $demo -Branch "add-environment-$Environment" -Title "Add environment $Environment ($($planned.tier))" `
    -Body "Adds $Environment to system.json with the baseline capability and an empty environments/$Environment/versions.json. After the merge, promote the new $($demo.slug)-system release to $Environment, then the app release." `
    -Change $change -Merge:$Merge | Out-Null
