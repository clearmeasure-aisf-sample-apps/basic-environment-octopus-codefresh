#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Phase 4 of the demo-environment skill: the first app repository, a snapshot of the bootcamp repository.

.DESCRIPTION
    1. Copies the bootcamp repository at its ref (the demo file's "bootcamp") without its history into
       ~/.demo-environment/<slug>/app: a template copy, so each demo has an independent repository whose first
       commit records the bootcamp commit it started from.
    2. Adapts the delivery files to the system:
         - keeps build.yml (private build gates, integration builds, analysis, acceptance tests, "Build result"), but
           removes its jobs docker-build-image-for-churchbulletin-ui and publish-octopus and the step "Mark PR as
           ready for review", which use stored secrets (AZURE_CREDENTIALS, OCTO_API_KEY, COPILOT_PAT);
         - removes deploy.yml and the bootcamp's own Octopus config-as-code (.octopus, .octopus_original_from_od);
         - adds templates/app/.github/workflows/release.yml: image to the system's registry, database package and
           release to Octopus, all through OIDC.
       Fails if any workflow still names one of those secrets.
    3. Creates the public repository, its environment "release" and its variables, pushes master (the first Build
       runs), adds the ruleset (pull requests with the "Build result" check) and links it to the board.
    Run it after the first <slug>-system deployment succeeded (get-demo-status.ps1 -Wait): the first release of the
    app deploys to an environment that must exist. A repository that already exists is left as it is.
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

function Remove-WorkflowJob {
    # Removes top-level jobs (two-space keys under "jobs:") with everything up to the next job.
    param([string[]] $Lines, [string[]] $Jobs)
    $result = [Collections.Generic.List[string]]::new()
    $inJobs = $false
    $skipping = $false
    foreach ($line in $Lines) {
        if ($line -cmatch '^jobs:\s*$') { $inJobs = $true; $skipping = $false; $result.Add($line); continue }
        if ($inJobs -and $line -cmatch '^  ([A-Za-z0-9_-]+):\s*$') { $skipping = $Jobs -ccontains $Matches[1] }
        elseif ($inJobs -and $line -cmatch '^\S') { $inJobs = $false; $skipping = $false }
        if (-not $skipping) { $result.Add($line) }
    }
    return , $result.ToArray()
}

function Remove-WorkflowStep {
    # Removes the step whose "- name:" equals $Name, up to the next step or the end of its job.
    param([string[]] $Lines, [string] $Name)
    $result = [Collections.Generic.List[string]]::new()
    $indent = -1
    foreach ($line in $Lines) {
        if ($indent -lt 0 -and $line -cmatch "^(\s*)- name:\s*$([regex]::Escape($Name))\s*$") {
            $indent = $Matches[1].Length
            continue
        }
        if ($indent -ge 0) {
            $lead = ($line -replace '^(\s*).*$', '$1').Length
            $isNextStep = $line -cmatch '^\s*- ' -and $lead -eq $indent
            $isOutside = $line.Trim() -and $lead -lt $indent
            if (-not ($isNextStep -or $isOutside)) { continue }
            $indent = -1
        }
        $result.Add($line)
    }
    return , $result.ToArray()
}

$demo = Read-DemoConfig -Path $Config
$names = Get-DemoName -Config $demo
$state = Read-DemoState -Config $demo
foreach ($phase in 'octopus', 'seed', 'system') {
    if (-not $state.ContainsKey($phase)) {
        throw "Phase '$phase' has not run yet."
    }
}
$fullName = "$($demo.githubOrg)/$($names.AppRepository)"
if (Test-GitHubRepository -FullName $fullName) {
    Write-Skip "$fullName already exists; nothing is changed."
    return
}

Write-Step "Snapshot of $($demo.bootcamp.repository)@$($demo.bootcamp.ref)"
$root = Split-Path -Parent (Get-DemoStatePath -Config $demo)
$clone = Join-Path $root 'bootcamp'
$work = Join-Path $root 'app'
Remove-Item -LiteralPath $clone, $work -Recurse -Force -ErrorAction SilentlyContinue
$env:GIT_LFS_SKIP_SMUDGE = '1'
git clone --quiet --depth 1 --branch $demo.bootcamp.ref "https://github.com/$($demo.bootcamp.repository).git" $clone
$sha = (git -C $clone rev-parse HEAD).Trim()
Remove-Item -LiteralPath (Join-Path $clone '.git') -Recurse -Force
Move-Item -LiteralPath $clone -Destination $work

Write-Step 'Adapting the workflows'
$workflows = Join-Path $work '.github' 'workflows'
foreach ($path in (Join-Path $workflows 'deploy.yml'), (Join-Path $work '.octopus'), (Join-Path $work '.octopus_original_from_od')) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
        Write-Pass "removed $([IO.Path]::GetRelativePath($work, $path))"
    }
}
$buildFile = Join-Path $workflows 'build.yml'
$lines = Get-Content -LiteralPath $buildFile
$lines = Remove-WorkflowJob -Lines $lines -Jobs @('docker-build-image-for-churchbulletin-ui', 'publish-octopus')
$lines = Remove-WorkflowStep -Lines $lines -Name 'Mark PR as ready for review'
Set-Content -LiteralPath $buildFile -Value $lines -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $SkillRoot 'templates' 'app' '.github' 'workflows' 'release.yml') -Destination $workflows
Write-Pass 'build.yml adapted, release.yml added'

$forbidden = Select-String -Path (Join-Path $workflows '*.yml') -Pattern 'secrets\.(AZURE_CREDENTIALS|OCTO_API_KEY|OCTOPUS_URL|COPILOT_PAT)\b'
if ($forbidden) {
    throw "Stored secrets are still named in the workflows:`n$($forbidden -join "`n")"
}
Write-Pass 'no workflow names a stored Azure, Octopus or personal token secret'

git -C $work init --quiet --initial-branch master
Initialize-GitIdentity -Directory $work
git -C $work add --all
git -C $work commit --quiet --message "Initial import from $($demo.bootcamp.repository)@$sha" `
    --message "Snapshot (no history) by the demo-environment skill for system $($names.Slug). Delivery: build.yml (CI), release.yml (release candidate to Octopus); deployments are Octopus's, desired state is $($state.system.repository)."

Write-Step "Creating $fullName"
gh repo create $fullName --public --description "$($demo.name): the work-order app (from $($demo.bootcamp.repository)), deployable $($demo.deployable.name) of $($state.system.repository)."
$seed = $state.seed
Set-GitHubRepositorySetting -FullName $fullName -Environments @('release') -Variables ([ordered] @{
        AZURE_TENANT_ID            = $seed.tenantId
        AZURE_SUBSCRIPTION_ID      = $seed.subscriptionId
        AZURE_CLIENT_ID_ACR_PUSH   = $seed.identities.acrPush.clientId
        ACR_NAME                   = $seed.registry.name
        ACR_LOGIN_SERVER           = $seed.registry.loginServer
        SYSTEM_SLUG                = $names.Slug
        DEPLOYABLE_NAME            = [string] $demo.deployable.name
        OCTOPUS_URL                = $state.octopus.url
        OCTOPUS_SPACE_ID           = $state.octopus.spaceId
        OCTOPUS_SERVICE_ACCOUNT_ID = $state.octopus.serviceAccountId
    })

Write-Step 'Pushing master (starts Build, then Release)'
git -C $work remote add origin "https://github.com/$fullName.git"
Invoke-GitWithGh -Directory $work -Arguments @('push', '--quiet', '--set-upstream', 'origin', 'master')
New-GitHubRuleset -FullName $fullName -RequiredCheck 'Build result'

if ($state.system.boardNumber) {
    gh project link $state.system.boardNumber --owner $demo.githubOrg --repo $fullName
    Write-Pass "linked to board $($state.system.boardUrl)"
}

Save-DemoState -Config $demo -Phase 'app' -Values @{
    repository = $fullName
    url        = "https://github.com/$fullName"
    bootcamp   = "$($demo.bootcamp.repository)@$sha"
}
Write-Pass "$fullName pushed; follow Build and Release at https://github.com/$fullName/actions"
