#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Stores the operator identity's Octopus API key, encrypted for this user on this machine, and checks its rights.

.DESCRIPTION
    Run it once in the shell of the operator account (operator-identity.md). It asks for the key of the Octopus
    service account without echoing it, encrypts it with systemd-creds (user-scoped: only this user on this host can
    decrypt it) into ~/.config/demo-environment/octopus-api-key.cred, then reads it back the way the skill does and
    checks that the account has the system permissions SpaceCreate and UserEdit. The key is never printed, written in
    plain text or passed as an argument.

.EXAMPLE
    pwsh -NoProfile -File .claude/skills/demo-environment/scripts/set-operator-octopus-key.ps1 -OctopusUrl https://example.octopus.app
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^https://[^/]+$')] [string] $OctopusUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'demo-common.ps1')

$credential = Get-OctopusCredentialPath
$folder = Split-Path -Parent $credential
New-Item -ItemType Directory -Path $folder -Force | Out-Null
chmod 700 $folder

$secure = Read-Host -Prompt 'API key of the Octopus service account (input hidden)' -AsSecureString
$plain = [Net.NetworkCredential]::new('', $secure).Password
if ($plain -notmatch '^API-[A-Z0-9]+$') {
    throw 'That does not look like an Octopus API key (API-...). Nothing was stored.'
}
$plain | systemd-creds encrypt --user --name=octopus-api-key - $credential
$plain = $null
chmod 600 $credential
Write-Pass "key encrypted in $credential"

$config = @{ octopus = @{ url = $OctopusUrl } }
$me = Invoke-OctopusApi -Config $config -Path '/api/users/me'
$permissions = Invoke-OctopusApi -Config $config -Path "/api/users/$($me.Id)/permissions"
$system = @($permissions.SystemPermissions)
foreach ($needed in 'SpaceCreate', 'UserEdit') {
    if ($system -notcontains $needed) {
        throw "$($me.Username) lacks $needed; add the service account to a team with that system permission (operator-identity.md)."
    }
}
Write-Pass "$($me.Username) at $OctopusUrl$(if ($me.IsService) { ' (service account)' }) with SpaceCreate and UserEdit"
