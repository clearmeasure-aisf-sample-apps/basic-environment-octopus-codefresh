#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Phase 0 of the demo-environment skill: checks the tools, the three logins and their rights before anything is created.

.DESCRIPTION
    Read-only. Prints PASS, FAIL or SKIP per check and exits 1 when a check fails.
      Tools     git, gh, az (2.61 or later, for deployment stacks), pwsh 7.4 or later
      GitHub    gh is logged in with the scopes the skill needs, as an owner of the organization
      Azure     az is logged in to the subscription of the demo file, with Owner on it
      Octopus   OCTOPUS_ADMIN_API_KEY reaches the instance and belongs to an administrator
    It also warns about the free-offer limit of Azure SQL (10 free databases per subscription, one region).

.EXAMPLE
    pwsh -NoProfile -File .claude/skills/demo-environment/scripts/test-demo-prerequisites.ps1 -Config ./demo.acme.json
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
$failed = 0
function Test-Check {
    param([string] $Name, [scriptblock] $Check)
    try {
        $detail = & $Check
        Write-Pass "$Name$(if ($detail) { ": $detail" })"
    }
    catch {
        Write-Fail "${Name}: $($_.Exception.Message)"
        $script:failed++
    }
}

Write-Step 'Tools'
foreach ($tool in 'git', 'gh', 'az') {
    Test-Check "tool $tool" {
        $command = Get-Command $tool -ErrorAction SilentlyContinue
        if (-not $command) { throw "$tool is not on PATH" }
        $command.Source
    }
}
Test-Check 'pwsh 7.4 or later' {
    if ($PSVersionTable.PSVersion -lt [version] '7.4') { throw "running $($PSVersionTable.PSVersion)" }
    "$($PSVersionTable.PSVersion)"
}
Test-Check 'az 2.61 or later (deployment stacks)' {
    $version = [version] ((az version --output json | ConvertFrom-Json -AsHashtable)['azure-cli'])
    if ($version -lt [version] '2.61.0') { throw "az $version; run az upgrade" }
    "$version"
}

Write-Step 'GitHub'
Test-Check 'gh login and scopes' {
    $PSNativeCommandUseErrorActionPreference = $false
    $status = (gh auth status --hostname github.com 2>&1 | Out-String)
    $PSNativeCommandUseErrorActionPreference = $true
    if ($LASTEXITCODE -ne 0) { throw 'gh is not logged in: gh auth login --web --scopes admin:org,repo,workflow,project,delete_repo' }
    $missing = @('admin:org', 'repo', 'workflow', 'project') | Where-Object { $status -notmatch [regex]::Escape("'$_'") }
    if ($missing) { throw "missing scopes: $($missing -join ', ') (gh auth refresh --scopes $($missing -join ','))" }
    if ($status -notmatch "'delete_repo'") { Write-Warning 'gh has no delete_repo scope: teardown will not be able to delete the repositories.' }
    'admin:org, repo, workflow, project'
}
Test-Check "owner of $($demo.githubOrg)" {
    $login = (gh api user --jq .login).Trim()
    $role = (gh api "orgs/$($demo.githubOrg)/memberships/$login" --jq .role).Trim()
    if ($role -ne 'admin') { throw "$login has role '$role'; the skill needs an organization owner" }
    $login
}

Write-Step 'Azure'
Test-Check 'az login and subscription' {
    $account = az account show --subscription $demo.azure.subscriptionId --output json | ConvertFrom-Json -AsHashtable
    "$($account.name) ($($account.id)) as $($account.user.name)"
}
Test-Check 'Owner on the subscription' {
    $assignee = (az ad signed-in-user show --query id --output tsv).Trim()
    $owner = az role assignment list --assignee $assignee --scope "/subscriptions/$($demo.azure.subscriptionId)" `
        --include-inherited --include-groups --query "[?roleDefinitionName=='Owner'] | length(@)" --output tsv
    if ([int] $owner -lt 1) { throw 'the signed-in user is not Owner of the subscription (the seed creates role assignments and resource groups)' }
    'Owner'
}
Test-Check 'Azure SQL free offer allowance' {
    $count = @(az resource list --subscription $demo.azure.subscriptionId --resource-type 'Microsoft.Sql/servers/databases' --query '[].id' --output json | ConvertFrom-Json).Count
    if ($count -ge 10) { Write-Warning "$count SQL databases exist in the subscription; the free offer allows 10 free databases, all in one region." }
    "$count database(s) in the subscription"
}

Write-Step 'Octopus'
Test-Check 'administrator API key' {
    $me = Invoke-OctopusApi -Config $demo -Path '/api/users/me'
    $permissions = Invoke-OctopusApi -Config $demo -Path "/api/users/$($me.Id)/permissions"
    $system = @($permissions.SystemPermissions)
    foreach ($needed in 'SpaceCreate', 'UserEdit') {
        if ($system -notcontains $needed) { throw "$($me.Username) lacks ${needed}: the foothold creates the space and the service account" }
    }
    "$($me.Username) at $($demo.octopus.url)"
}

if ($failed -gt 0) {
    Write-Host "$failed check(s) failed."
    exit 1
}
Write-Host 'Every prerequisite is in place.'
