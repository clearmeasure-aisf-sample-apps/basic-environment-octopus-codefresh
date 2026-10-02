#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Library (dot-source it) of the demo-environment skill: the demo file, the local state, Octopus and GitHub calls.

.DESCRIPTION
    Every script of the skill dot-sources this file:

      Read-DemoConfig      Reads and validates the demo file (demo.example.json shows its shape).
      Get-DemoName         The names every phase derives from the slug (repositories, projects, stacks, identities).
      Read-DemoState       The state of earlier phases (identifiers only), from ~/.demo-environment/<slug>/state.json.
      Save-DemoState       Merges one phase's results into that file.
      Invoke-OctopusApi    REST call with the administrator key from OCTOPUS_ADMIN_API_KEY (never a file or argument).
      Get-GitHubToken      The GitHub CLI's token, for the one secret the system repository stores.
      Write-Step, Write-Pass, Write-Fail, Write-Skip   Log lines in the format of docs/scripting.md.
      Invoke-GitWithGh, Initialize-GitIdentity, Test-GitHubRepository, Set-GitHubRepositorySetting, New-GitHubRuleset
                           git and gh helpers of phases 3 and 4.
      New-SystemPullRequest    One change to the system repository as a pull request (the progression scripts).
      Get-OctopusProjectState  The latest deployment of a project to an environment.

    Secrets stay in the operator's environment: OCTOPUS_ADMIN_API_KEY, and the logins of az and gh. The state file
    holds names, IDs and URLs only, so a re-run continues where the last one stopped.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

$script:SkillRoot = Split-Path -Parent $PSScriptRoot

function Write-Step { param([string] $Message) Write-Host "==> $Message" }
function Write-Pass { param([string] $Message) Write-Host "PASS $Message" }
function Write-Fail { param([string] $Message) Write-Host "FAIL $Message" }
function Write-Skip { param([string] $Message) Write-Host "SKIP $Message" }

function Read-DemoConfig {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Demo file $Path not found. Copy demo.example.json next to SKILL.md and fill it in."
    }
    $config = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    $errors = [Collections.Generic.List[string]]::new()
    if ([string] $config.slug -cnotmatch '^[a-z][a-z0-9]{2,9}$') { $errors.Add('slug: 3 to 10 lowercase letters and digits, starting with a letter') }
    if (-not $config.githubOrg) { $errors.Add('githubOrg is required') }
    if (-not $config.azure.subscriptionId) { $errors.Add('azure.subscriptionId is required') }
    if (-not $config.azure.location) { $errors.Add('azure.location is required') }
    foreach ($tier in 'nonprod', 'prod') {
        if (-not $config.azure.resourceGroups[$tier]) { $errors.Add("azure.resourceGroups.$tier is required") }
    }
    if ([string] $config.octopus.url -notmatch '^https://[^/]+$') { $errors.Add('octopus.url: https://<host> without a trailing slash (it is the OIDC issuer)') }
    if (-not $config.octopus.spaceName) { $errors.Add('octopus.spaceName is required') }
    if ([string] $config.deployable.name -cnotmatch '^[a-z][a-z0-9]{1,9}$') { $errors.Add('deployable.name: 2 to 10 lowercase letters and digits') }
    $planned = @($config.plannedEnvironments | ForEach-Object { [string] $_.name })
    foreach ($environment in $config.plannedEnvironments) {
        if ([string] $environment.name -cnotmatch '^[a-z][a-z0-9]{1,7}$') { $errors.Add("plannedEnvironments: '$($environment.name)' must be 2 to 8 lowercase letters and digits") }
        if (@('nonprod', 'prod') -cnotcontains [string] $environment.tier) { $errors.Add("plannedEnvironments: '$($environment.name)' needs tier nonprod or prod") }
    }
    foreach ($name in @($config.initialEnvironments)) {
        if ($planned -notcontains $name) { $errors.Add("initialEnvironments: '$name' is not in plannedEnvironments") }
    }
    if ($errors.Count -gt 0) {
        throw "Demo file ${Path}:`n  $($errors -join "`n  ")"
    }
    return $config
}

function Get-DemoName {
    param([Parameter(Mandatory)] [hashtable] $Config)

    $slug = [string] $Config.slug
    $deployable = [string] $Config.deployable.name
    $appRepository = if ($Config.ContainsKey('appRepository') -and $Config.appRepository) { [string] $Config.appRepository } else { "$slug-workorders" }
    return @{
        Slug              = $slug
        SystemRepository  = "$slug-system"
        AppRepository     = $appRepository
        SystemProject     = "$slug-system"
        DeployableProject = "$slug-$deployable"
        ServiceAccount    = "$slug-github"
    }
}

function Get-DemoStatePath {
    param([Parameter(Mandatory)] [hashtable] $Config)
    $folder = Join-Path $HOME '.demo-environment' ([string] $Config.slug)
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    return Join-Path $folder 'state.json'
}

function Read-DemoState {
    param([Parameter(Mandatory)] [hashtable] $Config)
    $path = Get-DemoStatePath -Config $Config
    if (-not (Test-Path -LiteralPath $path)) {
        return @{}
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
}

function Save-DemoState {
    param(
        [Parameter(Mandatory)] [hashtable] $Config,
        [Parameter(Mandatory)] [string] $Phase,
        [Parameter(Mandatory)] [hashtable] $Values
    )
    $state = Read-DemoState -Config $Config
    $state[$Phase] = $Values
    $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Get-DemoStatePath -Config $Config) -Encoding utf8NoBOM
}

function Invoke-OctopusApi {
    param(
        [Parameter(Mandatory)] [hashtable] $Config,
        [Parameter(Mandatory)] [string] $Path,
        [ValidateSet('Get', 'Post', 'Put', 'Delete')] [string] $Method = 'Get',
        [object] $Body = $null
    )
    if (-not $env:OCTOPUS_ADMIN_API_KEY) {
        throw 'OCTOPUS_ADMIN_API_KEY is not set. Export the Octopus administrator API key in this shell (never in a file).'
    }
    $arguments = @{
        Uri     = "$($Config.octopus.url)$Path"
        Method  = $Method
        Headers = @{ 'X-Octopus-ApiKey' = $env:OCTOPUS_ADMIN_API_KEY }
    }
    if ($null -ne $Body) {
        $arguments.Body = $Body | ConvertTo-Json -Depth 20
        $arguments.ContentType = 'application/json'
    }
    return Invoke-RestMethod @arguments
}

function Get-GitHubToken {
    $token = (gh auth token).Trim()
    if (-not $token) {
        throw 'gh has no token: run gh auth login first.'
    }
    return $token
}

function Invoke-GitWithGh {
    # git with gh as the credential helper for this one command, so the operator's global git config stays as it is.
    param([Parameter(Mandatory)] [string] $Directory, [Parameter(Mandatory)] [string[]] $Arguments)
    & git -C $Directory -c credential.helper= -c 'credential.helper=!gh auth git-credential' @Arguments
}

function Initialize-GitIdentity {
    # A fresh repository needs an author; the operator's GitHub identity is used when git has none.
    param([Parameter(Mandatory)] [string] $Directory)
    $PSNativeCommandUseErrorActionPreference = $false
    $name = git -C $Directory config user.name
    $PSNativeCommandUseErrorActionPreference = $true
    if (-not $name) {
        $user = gh api user | ConvertFrom-Json -AsHashtable
        git -C $Directory config user.name ([string] $(if ($user.name) { $user.name } else { $user.login }))
        git -C $Directory config user.email "$($user.id)+$($user.login)@users.noreply.github.com"
    }
}

function Test-GitHubRepository {
    param([Parameter(Mandatory)] [string] $FullName)
    $PSNativeCommandUseErrorActionPreference = $false
    gh repo view $FullName --json name *> $null
    $exists = $LASTEXITCODE -eq 0
    $PSNativeCommandUseErrorActionPreference = $true
    return $exists
}

function Set-GitHubRepositorySetting {
    # Repository variables and GitHub environments; values are identifiers, never secrets.
    param(
        [Parameter(Mandatory)] [string] $FullName,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Variables,
        [string[]] $Environments = @()
    )
    foreach ($entry in $Variables.GetEnumerator()) {
        gh variable set $entry.Key --repo $FullName --body ([string] $entry.Value)
        Write-Pass "variable $($entry.Key)"
    }
    foreach ($environment in $Environments) {
        gh api --method PUT "repos/$FullName/environments/$environment" --silent
        Write-Pass "environment $environment"
    }
}

function New-GitHubRuleset {
    # Default branch: pull requests with the given required check; organization owners may bypass (the operator's
    # first push, and Octopus's pin commits made with the operator's token).
    param([Parameter(Mandatory)] [string] $FullName, [Parameter(Mandatory)] [string] $RequiredCheck)

    $existing = @(gh api "repos/$FullName/rulesets" | ConvertFrom-Json -AsHashtable | Where-Object { $_.name -eq 'default-branch' })
    if ($existing.Count -gt 0) {
        Write-Pass "ruleset default-branch exists on $FullName"
        return
    }
    $ruleset = @{
        name          = 'default-branch'
        target        = 'branch'
        enforcement   = 'active'
        conditions    = @{ ref_name = @{ include = @('~DEFAULT_BRANCH'); exclude = @() } }
        bypass_actors = @(@{ actor_id = 1; actor_type = 'OrganizationAdmin'; bypass_mode = 'always' })
        rules         = @(
            @{ type = 'deletion' },
            @{ type = 'non_fast_forward' },
            @{
                type       = 'pull_request'
                parameters = @{
                    required_approving_review_count   = 0
                    dismiss_stale_reviews_on_push     = $false
                    require_code_owner_review         = $false
                    require_last_push_approval        = $false
                    required_review_thread_resolution = $false
                }
            },
            @{
                type       = 'required_status_checks'
                parameters = @{
                    strict_required_status_checks_policy = $false
                    required_status_checks               = @(@{ context = $RequiredCheck })
                }
            }
        )
    }
    $ruleset | ConvertTo-Json -Depth 20 | gh api --method POST "repos/$FullName/rulesets" --input - --silent
    Write-Pass "ruleset default-branch on $FullName requires '$RequiredCheck'"
}

function New-SystemPullRequest {
    # One change to the system repository as a pull request: clone, branch, change, commit, push, open.
    param(
        [Parameter(Mandatory)] [hashtable] $Config,
        [Parameter(Mandatory)] [string] $Branch,
        [Parameter(Mandatory)] [string] $Title,
        [Parameter(Mandatory)] [string] $Body,
        [Parameter(Mandatory)] [scriptblock] $Change,
        [switch] $Merge
    )
    $names = Get-DemoName -Config $Config
    $fullName = "$($Config.githubOrg)/$($names.SystemRepository)"
    $work = Join-Path ([IO.Path]::GetTempPath()) "$($names.SystemRepository)-$([Guid]::NewGuid().ToString('N'))"
    gh repo clone $fullName $work -- --quiet
    try {
        Initialize-GitIdentity -Directory $work
        git -C $work switch --create $Branch --quiet
        & $Change $work
        git -C $work add --all
        git -C $work commit --quiet --message $Title --message $Body
        Invoke-GitWithGh -Directory $work -Arguments @('push', '--quiet', '--set-upstream', 'origin', $Branch)
        $url = (gh pr create --repo $fullName --head $Branch --base main --title $Title --body $Body).Trim()
        Write-Pass "pull request $url"
        if ($Merge) {
            Write-Step 'Waiting for the checks of the pull request'
            $PSNativeCommandUseErrorActionPreference = $false
            gh pr checks $url --watch --required --interval 20
            $green = $LASTEXITCODE -eq 0
            $PSNativeCommandUseErrorActionPreference = $true
            if (-not $green) {
                throw "The required checks of $url did not pass; it stays open."
            }
            gh pr merge $url --squash --delete-branch
            Write-Pass "merged $url"
        }
        return $url
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-OctopusProjectState {
    # The latest deployment of a project to an environment: release, task state and the task ID.
    param(
        [Parameter(Mandatory)] [hashtable] $Config,
        [Parameter(Mandatory)] [string] $SpaceId,
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $Environment
    )
    $projectObject = Invoke-OctopusApi -Config $Config -Path "/api/$SpaceId/projects/$Project"
    $environments = Invoke-OctopusApi -Config $Config -Path "/api/$SpaceId/environments?partialName=$Environment&take=100"
    $environmentObject = @($environments.Items | Where-Object { $_.Name -eq $Environment }) | Select-Object -First 1
    if (-not $environmentObject) {
        return @{ Project = $Project; Environment = $Environment; State = 'NoEnvironment'; Release = ''; TaskId = '' }
    }
    $deployments = Invoke-OctopusApi -Config $Config -Path "/api/$SpaceId/deployments?projects=$($projectObject.Id)&environments=$($environmentObject.Id)&take=1"
    $deployment = @($deployments.Items) | Select-Object -First 1
    if (-not $deployment) {
        return @{ Project = $Project; Environment = $Environment; State = 'NotDeployed'; Release = ''; TaskId = '' }
    }
    $task = Invoke-OctopusApi -Config $Config -Path "/api/tasks/$($deployment.TaskId)"
    $release = Invoke-OctopusApi -Config $Config -Path "/api/$SpaceId/releases/$($deployment.ReleaseId)"
    return @{ Project = $Project; Environment = $Environment; State = [string] $task.State; Release = [string] $release.Version; TaskId = [string] $task.Id }
}
