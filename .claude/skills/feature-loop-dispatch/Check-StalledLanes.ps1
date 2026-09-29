#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Feature-loop stall watchdog: finds work items that stopped moving on board 678 without anyone noticing.

.DESCRIPTION
    Read-only. GitHub REST only (no GraphQL, no Actions API, so it runs in cloud sessions), plus Octopus reads for
    app repositories when the API key variable named in factory-loop.json (OCTOPUS) is set. The repo's kind, CI
    contexts and Octopus project come from .claude/factory-loop.json.

    Open pull requests
      GREEN_UNMERGED    app: the CI context (codefresh/ci) is success on the head for > StaleMinutes, PR still open
      CI_FAILED         app: the CI context is failure or error
      CI_STUCK          app: the CI context is pending for > the repo's pendingLimitMinutes, or missing 30 min after the push
      IDLE_PR           repo without a PR CI context: PR not updated for > 4 x StaleMinutes
      DIRTY             the PR has merge conflicts with its base
    Recently merged pull requests (last 30) whose referenced issue (Closes/Fixes/Resolves/Refs/Part of #N, or a
    branch named issue-N / fix-N) is still open and has no open sub-issues
      RELEASE_FAILED    app: the release context (codefresh/release) failed on the merge commit
      RELEASE_STUCK     app: the release context is pending or missing for > pendingLimitMinutes
      DEPLOY_FAILED     app: the Octopus deployment of the release carrying the merge failed, was cancelled or timed out
      DEPLOY_WAITING    app: that deployment waits on a manual intervention
      DEPLOY_STUCK      app: that deployment is queued or executing for > the deploy pendingLimitMinutes
      DEPLOYED_ISSUE_OPEN  app: the last environment (prod) deployed it > StaleMinutes ago, issue still open
      MERGED_ISSUE_OPEN    environment: merged > StaleMinutes ago, issue still open (check Argo CD for gitops/ changes)
    Local sub-sessions (optional)
      LOCAL_STALL       an active task's output file under TasksDir untouched for > LocalStaleMinutes

    Exit codes: 0 no stalls, 1 stalls found, 2 usage error.

.PARAMETER Repo
    owner/repo; defaults to defaultRepo of factory-loop.json. Must be a key of its 'repos'.

.PARAMETER ConfigPath
    Path of factory-loop.json (default: ../../factory-loop.json relative to this script).

.EXAMPLE
    pwsh -NoProfile -File .claude/skills/feature-loop-dispatch/Check-StalledLanes.ps1 -Repo clearmeasure-aisf-sample-apps/20260923-001 -Json
#>
[CmdletBinding()]
param(
    [string] $Repo = '',
    [string] $ConfigPath = '',
    [int] $StaleMinutes = 15,
    [string] $TasksDir = '',
    [string[]] $ActiveIds = @(),
    [int] $LocalStaleMinutes = 25,
    [switch] $Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot '..' '..' 'factory-loop.json'
}
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    Write-Host "Check-StalledLanes: config '$ConfigPath' not found"
    exit 2
}
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -AsHashtable
if (-not $Repo) {
    $Repo = $config['defaultRepo']
}
if (-not $config['repos'].ContainsKey($Repo)) {
    Write-Host "Check-StalledLanes: repo '$Repo' is not in 'repos' of $ConfigPath"
    exit 2
}
$repoConfig = $config['repos'][$Repo]
$kind = $repoConfig['kind']
$now = [DateTimeOffset]::UtcNow
$stalls = [System.Collections.Generic.List[object]]::new()

# ---- GitHub REST ----
$token = @($env:GITHUB_SAMPLE_APPS_PAT, $env:GH_TOKEN, $env:GITHUB_TOKEN) | Where-Object { $_ } | Select-Object -First 1
if (-not $token) {
    $token = ''
}
if (-not $token -and (Get-Command -Name gh -CommandType Application -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
    $token = (gh auth token 2>$null | Out-String).Trim()
    $PSNativeCommandUseErrorActionPreference = $true
}
if (-not $token) {
    Write-Host 'Check-StalledLanes: no GitHub token (GITHUB_SAMPLE_APPS_PAT, GH_TOKEN, GITHUB_TOKEN or gh auth)'
    exit 2
}
$gitHubHeaders = @{
    Authorization          = "Bearer $token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}

# Invoke-RestMethod writes a JSON array as one object; returning the variable enumerates it.
function Invoke-GitHub([string] $Path) {
    $result = Invoke-RestMethod -Uri "https://api.github.com/$Path" -Headers $gitHubHeaders -Method Get
    return $result
}

function Invoke-Octopus([string] $Path) {
    $result = Invoke-RestMethod -Uri "$($octopus.Base)/$Path" -Headers $octopus.Headers -Method Get
    return $result
}

function Add-Stall([string] $Kind, [int] $Pr, [string] $Branch, [string] $Detail) {
    $stalls.Add([pscustomobject]@{ Kind = $Kind; PR = $Pr; Branch = $Branch; Detail = $Detail })
}

function Get-Minute([object] $Since) {
    if (-not $Since) {
        return [double]::MaxValue
    }
    return ($now - [DateTimeOffset]$Since).TotalMinutes
}

# The newest status of one context on a commit, or $null.
function Get-CommitContext([string] $Sha, [string] $Context) {
    $combined = Invoke-GitHub "repos/$Repo/commits/$Sha/status?per_page=100"
    return @($combined.statuses | Where-Object { $_.context -eq $Context } | Sort-Object updated_at -Descending) | Select-Object -First 1
}

# ---- Octopus (app repositories) ----
$deploy = if ($repoConfig.ContainsKey('deploy')) { $repoConfig['deploy'] } else { $null }
$octopusKey = if ($deploy) { [Environment]::GetEnvironmentVariable($deploy['apiKeyEnv']) } else { $null }
$octopus = $null
if ($deploy -and $octopusKey) {
    $octopus = @{ Headers = @{ $deploy['apiKeyHeader'] = $octopusKey }; Base = "$($deploy['server'])/api/$($deploy['space'])" }
    $octopus.Project = @(Invoke-Octopus 'projects/all') | Where-Object { $_.Slug -eq $deploy['project'] } | Select-Object -First 1
    if (-not $octopus.Project) {
        Write-Host "Check-StalledLanes: Octopus project '$($deploy['project'])' not found in $($deploy['space'])"
        exit 2
    }
    $octopus.Environments = @(Invoke-Octopus 'environments/all')
    $octopus.Releases = @((Invoke-Octopus "projects/$($octopus.Project.Id)/releases?take=30").Items)
}

$compareCache = @{}
$deploymentCache = @{}

# The app commit a release was built from (its release notes carry "app-commit: <sha>").
function Get-ReleaseCommit([object] $Release) {
    $marker = [regex]::Escape($deploy['releaseCommitMarker'])
    $match = [regex]::Match([string]$Release.ReleaseNotes, "$marker\s*([0-9a-f]{40})")
    if ($match.Success) {
        return $match.Groups[1].Value
    }
    return $null
}

# The ids of the releases assembled after the merge whose app commit is the merge commit or contains it.
function Get-CarryingReleaseId([string] $MergeSha, [object] $MergedAt) {
    $ids = @()
    $after = ([DateTimeOffset]$MergedAt).AddMinutes(-5)
    foreach ($release in @($octopus.Releases | Where-Object { [DateTimeOffset]$_.Assembled -ge $after })) {
        $commit = Get-ReleaseCommit $release
        if (-not $commit) {
            continue
        }
        if ($commit -eq $MergeSha) {
            $ids += $release.Id
            continue
        }
        $key = "$MergeSha...$commit"
        if (-not $compareCache.ContainsKey($key)) {
            $compareCache[$key] = (Invoke-GitHub "repos/$Repo/compare/$key").status
        }
        if ($compareCache[$key] -in @('ahead', 'identical')) {
            $ids += $release.Id
        }
    }
    return $ids
}

# Deployment state of the carrying releases in one environment: the newest deployment and its task.
function Get-DeploymentState([string[]] $ReleaseIds, [string] $EnvironmentName) {
    $environment = $octopus.Environments | Where-Object { $_.Name -eq $EnvironmentName } | Select-Object -First 1
    if (-not $environment) {
        return $null
    }
    if (-not $deploymentCache.ContainsKey($environment.Id)) {
        $deploymentCache[$environment.Id] = @((Invoke-Octopus "deployments?projects=$($octopus.Project.Id)&environments=$($environment.Id)&take=20").Items)
    }
    $deployments = $deploymentCache[$environment.Id]
    $deployment = $deployments | Where-Object { $_.ReleaseId -in $ReleaseIds } | Sort-Object Created -Descending | Select-Object -First 1
    if (-not $deployment) {
        return $null
    }
    $task = Invoke-Octopus "tasks/$($deployment.TaskId)"
    return [pscustomobject]@{ Deployment = $deployment.Id; Task = $task.Id; State = $task.State; Waiting = [bool]$task.HasPendingInterruptions; Started = $task.StartTime; Completed = $task.CompletedTime; Queued = $task.QueueTime }
}

# ---- Open pull requests ----
$ci = $repoConfig['ci']
$prContext = if ($ci.ContainsKey('prContext')) { $ci['prContext'] } else { $null }
$ciLimit = if ($ci.ContainsKey('pendingLimitMinutes')) { [int]$ci['pendingLimitMinutes'] } else { 60 }
$openPrs = @(Invoke-GitHub "repos/$Repo/pulls?state=open&per_page=50")
foreach ($listed in $openPrs) {
    $pr = Invoke-GitHub "repos/$Repo/pulls/$($listed.number)"
    $branch = $pr.head.ref
    if ($pr.mergeable_state -eq 'dirty') {
        Add-Stall 'DIRTY' $pr.number $branch 'Merge conflicts with the base. Merge the default branch in, re-run the gates, re-push.'
        continue
    }
    if (-not $prContext) {
        $idle = Get-Minute $pr.updated_at
        if ($idle -gt (4 * $StaleMinutes)) {
            Add-Stall 'IDLE_PR' $pr.number $branch ('No PR CI in this repo and the PR is untouched for {0:n0} min. Confirm the gate summary, then merge.' -f $idle)
        }
        continue
    }
    $status = Get-CommitContext $pr.head.sha $prContext
    if (-not $status) {
        $headAge = Get-Minute (Invoke-GitHub "repos/$Repo/commits/$($pr.head.sha)").commit.committer.date
        if ($headAge -gt 30) {
            Add-Stall 'CI_STUCK' $pr.number $branch ("No $prContext status on the head commit {0:n0} min after it was committed (the push may not have triggered the build)." -f $headAge)
        }
        continue
    }
    switch ($status.state) {
        { $_ -in @('failure', 'error') } {
            Add-Stall 'CI_FAILED' $pr.number $branch "$prContext is $($status.state): $($status.target_url). Needs fix + re-push."
        }
        'pending' {
            $pending = Get-Minute $status.created_at
            if ($pending -gt $ciLimit) {
                Add-Stall 'CI_STUCK' $pr.number $branch ("$prContext pending for {0:n0} min: $($status.target_url)" -f $pending)
            }
        }
        'success' {
            $green = Get-Minute $status.updated_at
            if ($green -gt $StaleMinutes) {
                Add-Stall 'GREEN_UNMERGED' $pr.number $branch ("$prContext green for {0:n0} min but the PR is not merged. Stalled at the triage/merge step." -f $green)
            }
        }
    }
}

# ---- Recently merged pull requests whose issue is still open ----
$closed = @(Invoke-GitHub "repos/$Repo/pulls?state=closed&sort=updated&direction=desc&per_page=30")
$releaseContext = if ($ci.ContainsKey('releaseContext')) { $ci['releaseContext'] } else { $null }
$deployLimit = if ($deploy -and $deploy.ContainsKey('pendingLimitMinutes')) { [int]$deploy['pendingLimitMinutes'] } else { 90 }
foreach ($pr in @($closed | Where-Object { $_.merged_at })) {
    $mergedAge = Get-Minute $pr.merged_at
    if ($mergedAge -le $StaleMinutes) {
        continue
    }
    $numbers = @()
    if ($pr.body) {
        $numbers += [regex]::Matches($pr.body, '(?i)\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?|refs?|part of)\s+#(\d+)') | ForEach-Object { $_.Groups[1].Value }
    }
    if ($pr.head.ref -match '(?:issue|fix)[/\-](\d+)') {
        $numbers += $Matches[1]
    }
    $openIssues = @()
    foreach ($number in ($numbers | Select-Object -Unique)) {
        $issue = Invoke-GitHub "repos/$Repo/issues/$number"
        if ($issue.state -ne 'open' -or $issue.PSObject.Properties['pull_request']) {
            continue
        }
        $openChildren = @(Invoke-GitHub "repos/$Repo/issues/$number/sub_issues?per_page=100" | Where-Object { $_.state -eq 'open' })
        if ($openChildren.Count -eq 0) {
            $openIssues += $number
        }
    }
    if ($openIssues.Count -eq 0) {
        continue
    }
    $issueList = ($openIssues | ForEach-Object { "#$_" }) -join ', '
    $branch = $pr.head.ref

    if ($kind -ne 'app') {
        Add-Stall 'MERGED_ISSUE_OPEN' $pr.number $branch ('Merged {0:n0} min ago but {1} is still open with no open sub-issues. Check Argo CD for gitops/ changes, then close with the evidence comment.' -f $mergedAge, $issueList)
        continue
    }

    $mergeSha = $pr.merge_commit_sha
    if ($releaseContext) {
        $release = Get-CommitContext $mergeSha $releaseContext
        if (-not $release) {
            if ($mergedAge -gt $ciLimit) {
                Add-Stall 'RELEASE_STUCK' $pr.number $branch ("No $releaseContext status on merge commit $mergeSha {0:n0} min after the merge ($issueList open)." -f $mergedAge)
            }
            continue
        }
        if ($release.state -in @('failure', 'error')) {
            Add-Stall 'RELEASE_FAILED' $pr.number $branch "$releaseContext is $($release.state) on $mergeSha ($($release.target_url)); $issueList open."
            continue
        }
        if ($release.state -eq 'pending') {
            if ((Get-Minute $release.created_at) -gt $ciLimit) {
                Add-Stall 'RELEASE_STUCK' $pr.number $branch "$releaseContext pending on $mergeSha for > $ciLimit min ($($release.target_url))."
            }
            continue
        }
    }
    if (-not $octopus) {
        Write-Host "SKIP deployments of PR #$($pr.number): Octopus key variable '$($deploy['apiKeyEnv'])' not set"
        continue
    }
    $carrying = @(Get-CarryingReleaseId $mergeSha $pr.merged_at)
    if ($carrying.Count -eq 0) {
        if ($mergedAge -gt $ciLimit) {
            Add-Stall 'RELEASE_STUCK' $pr.number $branch "No Octopus release of $($deploy['project']) carries merge commit $mergeSha ($issueList open)."
        }
        continue
    }
    $lastEnvironment = $deploy['environments'][-1]['name']
    foreach ($target in $deploy['environments']) {
        $state = Get-DeploymentState $carrying $target['name']
        if (-not $state) {
            break  # not deployed there yet; the lifecycle promotes in order
        }
        if ($state.State -in @('Failed', 'Canceled', 'TimedOut')) {
            Add-Stall 'DEPLOY_FAILED' $pr.number $branch "$($state.Deployment) to $($target['name']) is $($state.State) ($($state.Task)); $issueList open."
            break
        }
        if ($state.Waiting) {
            Add-Stall 'DEPLOY_WAITING' $pr.number $branch "$($state.Deployment) to $($target['name']) waits on a manual intervention ($($state.Task))."
            break
        }
        if ($state.State -ne 'Success') {
            $since = if ($state.Started) { $state.Started } else { $state.Queued }
            if ((Get-Minute $since) -gt $deployLimit) {
                Add-Stall 'DEPLOY_STUCK' $pr.number $branch "$($state.Deployment) to $($target['name']) is $($state.State) for > $deployLimit min ($($state.Task))."
            }
            break
        }
        if ($target['name'] -eq $lastEnvironment -and (Get-Minute $state.Completed) -gt $StaleMinutes) {
            Add-Stall 'DEPLOYED_ISSUE_OPEN' $pr.number $branch "Deployed to $lastEnvironment by $($state.Deployment) ($($state.Task)) but $issueList is still open. Close it with the evidence comment."
        }
    }
}

# ---- Local sub-session liveness (pre-PR phase, invisible to GitHub) ----
if ($TasksDir -and (Test-Path -LiteralPath $TasksDir) -and $ActiveIds.Count -gt 0) {
    $cutoff = (Get-Date).AddMinutes(-$LocalStaleMinutes)
    foreach ($id in $ActiveIds) {
        $file = Get-Item -LiteralPath (Join-Path $TasksDir "$id.output") -ErrorAction SilentlyContinue
        if (-not $file) {
            Add-Stall 'LOCAL_STALL' 0 $id 'Active sub-session has no task output file at all.'
        }
        elseif ($file.LastWriteTime -lt $cutoff) {
            Add-Stall 'LOCAL_STALL' 0 $id ('Active sub-session output untouched since {0:HH:mm:ss} (> {1} min) - stalled before its PR.' -f $file.LastWriteTime, $LocalStaleMinutes)
        }
    }
}

if ($Json) {
    ConvertTo-Json -InputObject @($stalls) -Depth 20
}
else {
    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
    if ($stalls.Count -eq 0) {
        Write-Host "[$stamp] PASS no stalled work items in $Repo."
    }
    else {
        Write-Host "[$stamp] FAIL $($stalls.Count) stall(s) in ${Repo}:"
        foreach ($stall in $stalls) {
            Write-Host ('  [{0}] PR #{1} ({2}) - {3}' -f $stall.Kind, $stall.PR, $stall.Branch, $stall.Detail)
        }
    }
}
exit ($stalls.Count -gt 0 ? 1 : 0)
