#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Phase 3 of the demo-environment skill: creates the system (GitOps) repository and starts its first run.

.DESCRIPTION
    1. Renders templates/system into ~/.demo-environment/<slug>/system with a system.json built from the demo file
       and the state of phases 1 and 2, and one environments/<env>/versions.json ({}) per initial environment.
    2. Creates the public repository <org>/<slug>-system, then, before anything is pushed: its variables
       (identifiers), its GitHub environments (octopus, azure-read) and its one secret OCTOPUS_GITHUB_TOKEN (the
       operator's gh token, which Octopus uses for the pin commits).
    3. Pushes main. That push runs the system workflow: octopus-apply creates the Octopus environments, lifecycle,
       accounts and projects; system-release creates release 1.0.1 of <slug>-system, which the lifecycle deploys to
       the first environment: the first Azure environment comes from the pipeline, not from this script.
    4. Adds the ruleset (pull requests with the env-checks check on main) and, with "board": true, a GitHub Project.
    A repository that already exists is left as it is (the script reports it and stops).
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
foreach ($phase in 'octopus', 'seed') {
    if (-not $state.ContainsKey($phase)) {
        throw "Phase '$phase' has not run: run new-octopus-foothold.ps1 and new-demo-seed.ps1 first."
    }
}
$fullName = "$($demo.githubOrg)/$($names.SystemRepository)"
if (Test-GitHubRepository -FullName $fullName) {
    Write-Skip "$fullName already exists; nothing is changed. Delete it first to start over (remove-demo-environment.ps1)."
    return
}

$seed = $state.seed
$planned = @{}
foreach ($environment in $demo.plannedEnvironments) {
    $planned[[string] $environment.name] = [string] $environment.tier
}
$system = [ordered] @{
    system       = [ordered] @{
        slug       = $names.Slug
        name       = [string] $demo.name
        location   = [string] $demo.azure.location
        githubOrg  = [string] $demo.githubOrg
        repository = $names.SystemRepository
    }
    azure        = [ordered] @{
        tenantId       = [string] $seed.tenantId
        subscriptionId = [string] $seed.subscriptionId
        resourceGroups = $seed.resourceGroups
        registry       = $seed.registry
        terraformState = $seed.terraformState
        identities     = $seed.identities
    }
    octopus      = [ordered] @{
        url              = [string] $state.octopus.url
        spaceId          = [string] $state.octopus.spaceId
        spaceSlug        = [string] $state.octopus.spaceSlug
        serviceAccountId = [string] $state.octopus.serviceAccountId
    }
    deployables  = @(
        [ordered] @{
            name             = [string] $demo.deployable.name
            repository       = $names.AppRepository
            port             = [int] $demo.deployable.port
            healthPath       = [string] $demo.deployable.healthPath
            databasePackage  = 'ChurchBulletin.Database'
            databaseAssembly = 'ClearMeasure.Bootcamp.Database.dll'
        }
    )
    environments = @(
        foreach ($name in $demo.initialEnvironments) {
            [ordered] @{ name = [string] $name; tier = $planned[[string] $name]; capabilities = @('baseline') }
        }
    )
}

Write-Step "Rendering $fullName"
$work = Join-Path (Split-Path -Parent (Get-DemoStatePath -Config $demo)) 'system'
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $SkillRoot 'templates' 'system') -Destination $work -Recurse
Remove-Item -Path (Join-Path $work 'environments' '*') -Recurse -Force
foreach ($name in $demo.initialEnvironments) {
    $folder = New-Item -ItemType Directory -Path (Join-Path $work 'environments' $name) -Force
    Set-Content -LiteralPath (Join-Path $folder 'versions.json') -Value '{}' -Encoding utf8NoBOM
}
($system | ConvertTo-Json -Depth 20) + "`n" | Set-Content -LiteralPath (Join-Path $work 'system.json') -Encoding utf8NoBOM -NoNewline
& pwsh -NoProfile -File (Join-Path $work 'scripts' 'test-system.ps1') -Root $work
if ($LASTEXITCODE -ne 0) {
    throw 'The rendered system.json does not pass scripts/test-system.ps1.'
}

git -C $work init --quiet --initial-branch main
Initialize-GitIdentity -Directory $work
git -C $work add --all
git -C $work commit --quiet --message "Initial system: $($names.Slug) with $(@($demo.initialEnvironments) -join ', ')" `
    --message 'Created by the demo-environment skill. The first push configures Octopus and creates the first environment.'

Write-Step "Creating $fullName"
gh repo create $fullName --public --description "GitOps repository of $($demo.name): environments, infrastructure and deployed versions."
Set-GitHubRepositorySetting -FullName $fullName -Environments @('octopus', 'azure-read') -Variables ([ordered] @{
        AZURE_TENANT_ID                = $seed.tenantId
        AZURE_SUBSCRIPTION_ID          = $seed.subscriptionId
        AZURE_CLIENT_ID_OCTOPUS_CONFIG = $seed.identities.octopusConfig.clientId
        AZURE_CLIENT_ID_PLAN           = $seed.identities.plan.clientId
        OCTOPUS_URL                    = $state.octopus.url
        OCTOPUS_SPACE_ID               = $state.octopus.spaceId
        OCTOPUS_SERVICE_ACCOUNT_ID     = $state.octopus.serviceAccountId
    })
Get-GitHubToken | gh secret set OCTOPUS_GITHUB_TOKEN --repo $fullName
Write-Pass 'secret OCTOPUS_GITHUB_TOKEN'

Write-Step 'Pushing main (starts the system workflow)'
git -C $work remote add origin "https://github.com/$fullName.git"
Invoke-GitWithGh -Directory $work -Arguments @('push', '--quiet', '--set-upstream', 'origin', 'main')
New-GitHubRuleset -FullName $fullName -RequiredCheck 'env-checks'

$board = ''
if ($demo.ContainsKey('board') -and $demo.board) {
    $project = gh project create --owner $demo.githubOrg --title "$($demo.name)" --format json | ConvertFrom-Json -AsHashtable
    gh project link $project.number --owner $demo.githubOrg --repo $fullName
    $board = [string] $project.url
    Write-Pass "board $board"
}

Save-DemoState -Config $demo -Phase 'system' -Values @{
    repository  = $fullName
    url         = "https://github.com/$fullName"
    boardNumber = if ($board) { [string] $project.number } else { '' }
    boardUrl    = $board
}
Write-Pass "$fullName pushed; follow the run at https://github.com/$fullName/actions"
