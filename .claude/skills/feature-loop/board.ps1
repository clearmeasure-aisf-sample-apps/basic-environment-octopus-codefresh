#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Feature-loop helper: one command per board move, CI check, deployment check, sub-issue tree or lane record.

.DESCRIPTION
    Settings come from .claude/factory-loop.json (board, columns, dispatch repo, CI contexts, Octopus project,
    poll intervals). Output is a few short lines per call, so an agent reads facts instead of raw JSON.

    move <item> <column>        board-status repository_dispatch to the environment repo (204 = moved). Any other
                                answer posts the fallback 'board-status:' comment on the item (unless -NoFallback)
                                and exits 1.
    status <pr|sha>             A PR number: state, head, mergeable state and the head's commit statuses (plus the
                                merge commit's statuses once merged). A SHA (7-40 hex): its commit statuses.
    deploy <sha>                Octopus: the release carrying the commit and its deployment state per environment.
    wait ci <pr>                Poll until the PR head's CI context (codefresh/ci) is final.
    wait release <pr|sha>       Poll until the release context (codefresh/release) on the merge commit is final.
    wait deploy <sha> <env>     Poll until the carrying release's deployment to <env> is final.
    tree <item>                 Sub-issue tree (all repos) and the children-first order of its open items.
    lane [<item> [key=value]]   Lane records of the dispatch orchestrator: <git common dir>/feature-loop/lanes.json.
                                No item: one line per lane. key=null removes a key.

    <item> is N, #N (defaultRepo or -Repo) or owner/repo#N.
    Tokens: GitHub, in this order: AISF_BOARD_APP_TOKEN (a pre-minted installation token of the App aisf-board), else an
    installation token minted from AISF_BOARD_APP_ID (default: factory-loop.json githubApp.appId) with the private key of
    AISF_BOARD_APP_PRIVATE_KEY_PATH (a PEM file) or AISF_BOARD_APP_PRIVATE_KEY (PEM text), else 'gh auth token' (which
    honours GH_TOKEN; gh-less sessions read GH_TOKEN or GITHUB_TOKEN). scripts/github/GitHubAppAuth.ps1 does the minting.
    The App has no Contents: write and Commit statuses: read, so a call it refuses (401, 403, 404) is retried once with
    the 'gh' token; 'move' therefore normally lands with the gh token. Octopus from the variable named by
    deploy.apiKeyEnv (OCTOPUS). Tokens go only into request headers and are never printed.
    GITHUB_API_URL overrides https://api.github.com (the test seam).

    Exit codes: 0 success/final-success, 1 failed/refused/final-failure, 2 usage error, 3 skipped (no Octopus key),
    4 wait timed out.

.EXAMPLE
    pwsh -NoProfile -File .claude/skills/feature-loop/board.ps1 move 123 'In Progress'

.EXAMPLE
    pwsh -NoProfile -File .claude/skills/feature-loop/board.ps1 wait ci 45 -TimeoutMinutes 60
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $Command = '',

    [Parameter(Position = 1, ValueFromRemainingArguments)]
    [string[]] $Arguments = @(),

    [string] $Repo = '',
    [string] $ConfigPath = '',
    [int] $IntervalMinutes = 0,
    [int] $TimeoutMinutes = 0,
    [switch] $NoFallback
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

# A refused or failed call: one line (method, path, HTTP status), exit 1; never the headers.
function Stop-Call([string] $What, [System.Management.Automation.ErrorRecord] $Record) {
    $code = if ($Record.Exception.PSObject.Properties['Response'] -and $Record.Exception.Response) { [int]$Record.Exception.Response.StatusCode } else { 'no response' }
    Write-Host "board: $What -> HTTP $code $($Record.Exception.Message.Split([Environment]::NewLine)[0])"
    exit 1
}

function Stop-Usage([string] $Message) {
    Write-Host "board: $Message (see Get-Help $PSCommandPath)"
    exit 2
}

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot '..' '..' 'factory-loop.json'
}
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    Stop-Usage "config '$ConfigPath' not found"
}
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -AsHashtable
if (-not $Repo) {
    $Repo = $config['defaultRepo']
}
$options = @{ Interval = $IntervalMinutes; Timeout = $TimeoutMinutes; NoFallback = [bool]$NoFallback }
$polling = if ($config.ContainsKey('polling')) { $config['polling'] } else { @{} }

# ---- Helpers ----
$script:apiBase = if ($env:GITHUB_API_URL) { $env:GITHUB_API_URL.TrimEnd('/') } else { 'https://api.github.com' }

# The token resolver of the platform (scripts/github/GitHubAppAuth.ps1). A copy of this script in a repository without
# that library resolves through the GitHub CLI only.
$appAuthLibrary = Join-Path $PSScriptRoot '..' '..' '..' 'scripts' 'github' 'GitHubAppAuth.ps1'
if (Test-Path -LiteralPath $appAuthLibrary -PathType Leaf) {
    . $appAuthLibrary
}
else {
    function Get-GitHubCliToken {
        if (Get-Command -Name gh -CommandType Application -ErrorAction SilentlyContinue) {
            $PSNativeCommandUseErrorActionPreference = $false
            $value = (gh auth token 2>$null | Out-String).Trim()
            $PSNativeCommandUseErrorActionPreference = $true
            if ($value) {
                return $value
            }
        }
        return @($env:GH_TOKEN, $env:GITHUB_TOKEN) | Where-Object { $_ } | Select-Object -First 1
    }
    function Resolve-GitHubToken([hashtable] $AppConfig) {
        $null = $AppConfig  # the CLI-only resolver has no App to mint from
        $value = Get-GitHubCliToken
        if ($value) {
            return @{ Token = $value; Source = 'gh' }
        }
        return $null
    }
}
$appConfig = if ($config.ContainsKey('githubApp')) { $config['githubApp'] } else { @{} }

# The resolved token and its source label ('app' or 'gh'); the value goes only into request headers.
$script:tokenState = $null
function Get-GitHubTokenState {
    if (-not $script:tokenState) {
        $resolved = Resolve-GitHubToken -AppConfig $appConfig
        if (-not $resolved) {
            Stop-Usage 'no GitHub token (AISF_BOARD_APP_TOKEN, AISF_BOARD_APP_ID with AISF_BOARD_APP_PRIVATE_KEY_PATH or AISF_BOARD_APP_PRIVATE_KEY, or gh auth login / GH_TOKEN)'
        }
        $script:tokenState = $resolved
    }
    return $script:tokenState
}

$script:cliToken = $null
$script:cliTokenLooked = $false
function Get-CliTokenOnce {
    if (-not $script:cliTokenLooked) {
        $script:cliToken = Get-GitHubCliToken
        $script:cliTokenLooked = $true
    }
    return $script:cliToken
}

function Invoke-GitHubOnce([string] $Method, [string] $Path, [string] $Json, [string] $Token) {
    $headers = @{ Authorization = "Bearer $Token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $arguments = @{ Uri = "$script:apiBase/$Path"; Headers = $headers; Method = $Method; SkipHttpErrorCheck = $true }
    if ($Json) {
        $arguments['Body'] = $Json
        $arguments['ContentType'] = 'application/json'
    }
    try {
        return Invoke-WebRequest @arguments
    }
    catch [System.Net.Http.HttpRequestException] {
        Write-Host "board: $Method github $Path -> no response ($($_.Exception.Message.Split([Environment]::NewLine)[0]))"
        exit 1
    }
}

# One GitHub call. A call the App token cannot make (401, 403, 404: no Contents: write for the dispatch, no Commit
# statuses: read for statuses) is retried once with the GitHub CLI token, when there is one.
$script:retryNoted = $false
function Send-GitHub([string] $Method, [string] $Path, [hashtable] $Body) {
    $json = if ($Body) { ConvertTo-Json -InputObject $Body -Depth 20 -Compress } else { '' }
    $state = Get-GitHubTokenState
    $answer = Invoke-GitHubOnce $Method $Path $json $state.Token
    if ($state.Source -eq 'app' -and [int]$answer.StatusCode -in @(401, 403, 404)) {
        $cli = Get-CliTokenOnce
        if ($cli -and $cli -ne $state.Token) {
            if (-not $script:retryNoted) {
                Write-Host "board: the App token was refused (HTTP $([int]$answer.StatusCode) on $Method $Path); retrying with the GitHub CLI token"
                $script:retryNoted = $true
            }
            $answer = Invoke-GitHubOnce $Method $Path $json $cli
        }
    }
    return $answer
}

# Invoke-RestMethod writes a JSON array as one object; returning the variable enumerates it.
function Invoke-GitHub([string] $Path) {
    $answer = Send-GitHub 'Get' $Path $null
    if ([int]$answer.StatusCode -ge 400) {
        Write-Host "board: GET github $Path -> HTTP $([int]$answer.StatusCode)"
        exit 1
    }
    if (-not $answer.Content) {
        return
    }
    $result = $answer.Content | ConvertFrom-Json -NoEnumerate
    return $result
}

# N, #N or owner/repo#N -> @{ Repo; Number }.
function Resolve-Item([string] $Text) {
    if ($Text -match '^(?<repo>[\w.-]+/[\w.-]+)#(?<n>\d+)$') {
        return @{ Repo = $Matches['repo']; Number = [int]$Matches['n'] }
    }
    if ($Text -match '^#?(?<n>\d+)$') {
        return @{ Repo = $Repo; Number = [int]$Matches['n'] }
    }
    Stop-Usage "'$Text' is not N, #N or owner/repo#N"
}

function Get-RepoConfig([string] $Name) {
    if (-not $config['repos'].ContainsKey($Name)) {
        Stop-Usage "repo '$Name' is not in 'repos' of $ConfigPath"
    }
    return $config['repos'][$Name]
}

function Test-Sha([string] $Text) {
    return $Text -match '^[0-9a-f]{7,40}$' -and $Text -notmatch '^\d+$'
}

function Format-Age([object] $Since) {
    if (-not $Since) {
        return '-'
    }
    $minutes = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$Since).TotalMinutes
    return ('{0:n0}m ago' -f $minutes)
}

function Get-Short([string] $Sha) {
    return $Sha.Substring(0, [Math]::Min(12, $Sha.Length))
}

function Get-Interval([string] $Kind, [int] $Default) {
    if ($options.Interval -gt 0) {
        return $options.Interval
    }
    if ($polling.ContainsKey($Kind) -and $polling[$Kind].ContainsKey('intervalMinutes')) {
        return [int]$polling[$Kind]['intervalMinutes']
    }
    return $Default
}

function Get-Timeout([int] $Default) {
    if ($options.Timeout -gt 0) {
        return $options.Timeout
    }
    return $Default
}

# ---- Commit statuses ----
# The latest status of every context on a commit (the combined status lists the newest per context).
function Get-CommitStatus([string] $Sha) {
    $combined = Invoke-GitHub "repos/$Repo/commits/$Sha/status?per_page=100"
    return @($combined.statuses)
}

function Write-CommitStatus([string] $Label, [string] $Sha) {
    # @(): an empty array is unrolled to $null on return, and $null has no Count under strict mode.
    $statuses = @(Get-CommitStatus $Sha)
    if ($statuses.Count -eq 0) {
        Write-Host "  $Label $(Get-Short $Sha): no statuses"
        return
    }
    foreach ($status in $statuses) {
        Write-Host ('  {0} {1}: {2} {3} ({4}) {5}' -f $Label, (Get-Short $Sha), $status.context, $status.state, (Format-Age $status.updated_at), $status.target_url)
    }
}

# A PR number or SHA -> the SHA to read a context from. Kind 'head' or 'merge'.
function Resolve-Commit([string] $Ref, [string] $Kind) {
    if (Test-Sha $Ref) {
        return $Ref
    }
    $pr = Invoke-GitHub "repos/$Repo/pulls/$((Resolve-Item $Ref).Number)"
    if ($Kind -eq 'merge') {
        if (-not $pr.merged_at) {
            return $null
        }
        return $pr.merge_commit_sha
    }
    return $pr.head.sha
}

function Get-ContextState([string] $Sha, [string] $Context) {
    if (-not $Sha) {
        return $null
    }
    return Get-CommitStatus $Sha | Where-Object { $_.context -eq $Context } | Select-Object -First 1
}

# ---- Octopus ----
$script:octopus = $null
function Get-Octopus {
    if ($script:octopus) {
        return $script:octopus
    }
    $repoConfig = Get-RepoConfig $Repo
    if (-not $repoConfig.ContainsKey('deploy')) {
        Stop-Usage "repo '$Repo' has no 'deploy' section (not an app repository)"
    }
    $deploy = $repoConfig['deploy']
    $key = [Environment]::GetEnvironmentVariable($deploy['apiKeyEnv'])
    if (-not $key) {
        Write-Host "SKIP deploy: Octopus key variable '$($deploy['apiKeyEnv'])' not set"
        exit 3
    }
    $state = @{ Deploy = $deploy; Headers = @{ $deploy['apiKeyHeader'] = $key }; Base = "$($deploy['server'])/api/$($deploy['space'])" }
    $script:octopus = $state
    $projects = Invoke-Octopus 'projects/all'
    $state.Project = $projects | Where-Object { $_.Slug -eq $deploy['project'] } | Select-Object -First 1
    if (-not $state.Project) {
        Stop-Usage "Octopus project '$($deploy['project'])' not found in $($deploy['space'])"
    }
    $environments = Invoke-Octopus 'environments/all'
    $state.Environments = @($environments)
    return $state
}

function Invoke-Octopus([string] $Path) {
    $state = Get-Octopus
    try {
        $result = Invoke-RestMethod -Uri "$($state.Base)/$Path" -Headers $state.Headers -Method Get
    }
    catch {
        Stop-Call "GET octopus $Path" $_
    }
    return $result
}

# Releases whose release notes name an app commit equal to, or containing, the given commit; oldest first.
function Get-CarryingRelease([string] $Sha) {
    $state = Get-Octopus
    $marker = [regex]::Escape($state.Deploy['releaseCommitMarker'])
    $releases = @((Invoke-Octopus "projects/$($state.Project.Id)/releases?take=30").Items)
    $carrying = @()
    foreach ($release in $releases) {
        $match = [regex]::Match([string]$release.ReleaseNotes, "$marker\s*([0-9a-f]{40})")
        if (-not $match.Success) {
            continue
        }
        $commit = $match.Groups[1].Value
        if ($commit.StartsWith($Sha)) {
            $carrying += $release
            continue
        }
        $answer = Send-GitHub 'Get' "repos/$Repo/compare/$Sha...$commit" $null
        $compare = if ([int]$answer.StatusCode -eq 200) { ($answer.Content | ConvertFrom-Json).status } else { 'unknown' }
        if ($compare -in @('ahead', 'identical')) {
            $carrying += $release
        }
    }
    return @($carrying | Sort-Object { [DateTimeOffset]$_.Assembled })
}

# The newest deployment of any carrying release to one environment, with its task state; $null if none.
function Get-DeploymentState([object[]] $Releases, [string] $EnvironmentName) {
    $state = Get-Octopus
    $environment = $state.Environments | Where-Object { $_.Name -eq $EnvironmentName } | Select-Object -First 1
    if (-not $environment) {
        Stop-Usage "Octopus environment '$EnvironmentName' not found"
    }
    $ids = @($Releases | ForEach-Object { $_.Id })
    $deployments = @((Invoke-Octopus "deployments?projects=$($state.Project.Id)&environments=$($environment.Id)&take=20").Items)
    $deployment = $deployments | Where-Object { $_.ReleaseId -in $ids } | Sort-Object { [DateTimeOffset]$_.Created } -Descending | Select-Object -First 1
    if (-not $deployment) {
        return $null
    }
    $task = Invoke-Octopus "tasks/$($deployment.TaskId)"
    $version = ($Releases | Where-Object { $_.Id -eq $deployment.ReleaseId } | Select-Object -First 1).Version
    return [pscustomobject]@{
        Environment = $EnvironmentName; Version = $version; Deployment = $deployment.Id; Task = $task.Id
        State = $task.State; Waiting = [bool]$task.HasPendingInterruptions; Since = ($task.CompletedTime ?? $task.StartTime ?? $task.QueueTime)
    }
}

function Format-Deployment([string] $EnvironmentName, [object] $State) {
    if (-not $State) {
        return "  ${EnvironmentName}: not deployed"
    }
    $waiting = if ($State.Waiting) { ' WAITING-ON-INTERVENTION' } else { '' }
    return ('  {0}: {1}{2} release {3} {4} {5} ({6})' -f $EnvironmentName, $State.State, $waiting, $State.Version, $State.Deployment, $State.Task, (Format-Age $State.Since))
}

# ---- Commands ----
function Invoke-Move([string[]] $Rest) {
    if ($Rest.Count -lt 2) {
        Stop-Usage 'move <item> <column>'
    }
    $item = Resolve-Item $Rest[0]
    $wanted = ($Rest[1..($Rest.Count - 1)] -join ' ').Trim()
    $column = $config['columnOrder'] | Where-Object { $_ -ieq $wanted } | Select-Object -First 1
    if (-not $column) {
        Stop-Usage "'$wanted' is not a column ($($config['columnOrder'] -join ', '))"
    }
    $dispatch = $config['boardMoves']['dispatch']
    $body = @{ event_type = $dispatch['eventType']; client_payload = @{ repository = $item.Repo; issue = $item.Number; status = $column } }
    $answer = Send-GitHub 'Post' "repos/$($dispatch['repo'])/dispatches" $body
    $label = "$($item.Repo)#$($item.Number)"
    if ([int]$answer.StatusCode -eq 204) {
        Write-Host "MOVED $label -> $column (dispatch 204)"
        exit 0
    }
    Write-Host "REFUSED $label -> $column (dispatch HTTP $([int]$answer.StatusCode))"
    if ($options.NoFallback) {
        exit 1
    }
    $comment = $config['boardMoves']['fallback']['template'].Replace('<column>', $column)
    $posted = Send-GitHub 'Post' "repos/$($item.Repo)/issues/$($item.Number)/comments" @{ body = $comment }
    if ([int]$posted.StatusCode -eq 201) {
        Write-Host "FALLBACK comment posted on $label ($((ConvertFrom-Json $posted.Content).html_url))"
    }
    else {
        Write-Host "FALLBACK comment refused too (HTTP $([int]$posted.StatusCode)); report both calls"
    }
    exit 1
}

function Invoke-Status([string[]] $Rest) {
    if ($Rest.Count -lt 1) {
        Stop-Usage 'status <pr|sha>'
    }
    if (Test-Sha $Rest[0]) {
        Write-Host "commit $(Get-Short $Rest[0]) in $Repo"
        Write-CommitStatus 'commit' $Rest[0]
        exit 0
    }
    $item = Resolve-Item $Rest[0]
    $Repo = $item.Repo
    $pr = Invoke-GitHub "repos/$Repo/pulls/$($item.Number)"
    $state = if ($pr.merged_at) { 'merged' } else { $pr.state }
    Write-Host ('PR {0}#{1} {2} head={3} mergeable={4} updated {5}' -f $Repo, $pr.number, $state, (Get-Short $pr.head.sha), $pr.mergeable_state, (Format-Age $pr.updated_at))
    Write-CommitStatus 'head' $pr.head.sha
    if ($pr.merged_at) {
        Write-Host "  merge commit $($pr.merge_commit_sha) ($(Format-Age $pr.merged_at))"
        Write-CommitStatus 'merge' $pr.merge_commit_sha
    }
    exit 0
}

function Invoke-Deploy([string[]] $Rest) {
    if ($Rest.Count -lt 1 -or -not (Test-Sha $Rest[0])) {
        Stop-Usage 'deploy <sha>'
    }
    $sha = $Rest[0]
    $releases = Get-CarryingRelease $sha
    if ($releases.Count -eq 0) {
        Write-Host "no Octopus release of $((Get-Octopus).Deploy['project']) carries $(Get-Short $sha) yet"
        exit 1
    }
    Write-Host ('carrying release {0} ({1}), assembled {2}; later carrying: {3}' -f $releases[0].Version, $releases[0].Id, (Format-Age $releases[0].Assembled), ((@($releases | Select-Object -Skip 1 | ForEach-Object { $_.Version }) -join ', ') -replace '^$', 'none'))
    foreach ($target in (Get-Octopus).Deploy['environments']) {
        Write-Host (Format-Deployment $target['name'] (Get-DeploymentState $releases $target['name']))
    }
    exit 0
}

# One poll of a wait target -> @{ Final = $bool; Success = $bool; Line = 'text' }.
function Get-WaitState([string] $Kind, [string] $Ref, [string] $EnvironmentName) {
    $repoConfig = Get-RepoConfig $Repo
    switch ($Kind) {
        'ci' {
            $context = $repoConfig['ci']['prContext']
            $sha = Resolve-Commit $Ref 'head'
            $status = Get-ContextState $sha $context
            $state = if ($status) { $status.state } else { 'missing' }
            return @{ Final = $state -in @('success', 'failure', 'error'); Success = $state -eq 'success'; Line = "$context $state on head $(Get-Short $sha) $(if ($status) { $status.target_url })" }
        }
        'release' {
            $context = $repoConfig['ci']['releaseContext']
            $sha = Resolve-Commit $Ref 'merge'
            if (-not $sha) {
                return @{ Final = $false; Success = $false; Line = "PR $Ref not merged yet" }
            }
            $status = Get-ContextState $sha $context
            $state = if ($status) { $status.state } else { 'missing' }
            return @{ Final = $state -in @('success', 'failure', 'error'); Success = $state -eq 'success'; Line = "$context $state on merge $(Get-Short $sha) $(if ($status) { $status.target_url })" }
        }
        'deploy' {
            $releases = Get-CarryingRelease $Ref
            if ($releases.Count -eq 0) {
                return @{ Final = $false; Success = $false; Line = "no release carries $(Get-Short $Ref) yet" }
            }
            $state = Get-DeploymentState $releases $EnvironmentName
            $line = (Format-Deployment $EnvironmentName $state).Trim()
            if (-not $state) {
                return @{ Final = $false; Success = $false; Line = $line }
            }
            $final = $state.Waiting -or $state.State -in @('Success', 'Failed', 'Canceled', 'TimedOut')
            return @{ Final = $final; Success = $state.State -eq 'Success'; Line = $line }
        }
    }
    Stop-Usage "wait kind '$Kind' is not ci, release or deploy"
}

function Invoke-Wait([string[]] $Rest) {
    if ($Rest.Count -lt 2) {
        Stop-Usage 'wait ci <pr> | wait release <pr|sha> | wait deploy <sha> <env>'
    }
    $kind = $Rest[0]
    $ref = $Rest[1]
    if ($ref -match '#') {
        $item = Resolve-Item $ref
        $Repo = $item.Repo
        $ref = [string]$item.Number
    }
    $environmentName = if ($Rest.Count -ge 3) { $Rest[2] } else { '' }
    if ($kind -eq 'deploy' -and (-not $environmentName -or -not (Test-Sha $ref))) {
        Stop-Usage 'wait deploy <sha> <env>'
    }
    $repoConfig = Get-RepoConfig $Repo
    $defaults = @{ ci = @(4, 60); release = @(4, 60); deploy = @(5, 90) }
    if (-not $defaults.ContainsKey($kind)) {
        Stop-Usage "wait kind '$kind' is not ci, release or deploy"
    }
    $limit = if ($kind -eq 'deploy' -and $repoConfig.ContainsKey('deploy')) { [int]$repoConfig['deploy']['pendingLimitMinutes'] } else { [int]$repoConfig['ci']['pendingLimitMinutes'] }
    $interval = Get-Interval $kind $defaults[$kind][0]
    $timeout = Get-Timeout ($limit ? $limit : $defaults[$kind][1])
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes($timeout)
    $last = ''
    while ($true) {
        $state = Get-WaitState $kind $ref $environmentName
        if ($state.Line -ne $last) {
            Write-Host "[$((Get-Date).ToString('HH:mm'))] $($state.Line)"
            $last = $state.Line
        }
        if ($state.Final) {
            exit ($state.Success ? 0 : 1)
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            Write-Host "TIMEOUT after $timeout min: $($state.Line)"
            exit 4
        }
        Start-Sleep -Seconds (60 * $interval)
    }
}

function Get-RepoFromUrl([string] $Url) {
    return ($Url -replace '^https?://[^/]+/repos/', '')
}

function Write-Tree([string] $ItemRepo, [int] $Number, [int] $Depth, [System.Collections.Generic.List[string]] $Order) {
    $issue = Invoke-GitHub "repos/$ItemRepo/issues/$Number"
    $label = "$ItemRepo#$Number"
    Write-Host ('{0}- {1} [{2}] {3}' -f ('  ' * $Depth), $label, $issue.state, $issue.title)
    if ($Depth -lt 6) {
        foreach ($child in @(Invoke-GitHub "repos/$ItemRepo/issues/$Number/sub_issues?per_page=50")) {
            Write-Tree (Get-RepoFromUrl $child.repository_url) $child.number ($Depth + 1) $Order
        }
    }
    if ($issue.state -eq 'open') {
        $Order.Add($label)
    }
}

function Invoke-Tree([string[]] $Rest) {
    if ($Rest.Count -lt 1) {
        Stop-Usage 'tree <item>'
    }
    $item = Resolve-Item $Rest[0]
    $order = [System.Collections.Generic.List[string]]::new()
    Write-Tree $item.Repo $item.Number 0 $order
    Write-Host "children-first order (open): $(if ($order.Count) { $order -join ', ' } else { 'none' })"
    exit 0
}

function Get-LanePath {
    $PSNativeCommandUseErrorActionPreference = $false
    $common = (git rev-parse --path-format=absolute --git-common-dir 2>$null | Out-String).Trim()
    $PSNativeCommandUseErrorActionPreference = $true
    if (-not $common) {
        Stop-Usage 'lane needs a git checkout (the file lives in the git common dir)'
    }
    return Join-Path $common 'feature-loop' 'lanes.json'
}

function Format-Lane([string] $Key, [System.Collections.IDictionary] $Fields) {
    $pairs = foreach ($name in $Fields.Keys) {
        $value = $Fields[$name]
        if ($value -is [datetime]) {
            $value = $value.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        }
        "$name=$value"
    }
    return "$Key $($pairs -join ' ')"
}

function Invoke-Lane([string[]] $Rest) {
    $path = Get-LanePath
    $lanes = if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable } else { [ordered]@{} }
    if ($Rest.Count -eq 0) {
        if ($lanes.Count -eq 0) {
            Write-Host "no lanes ($path)"
        }
        foreach ($key in $lanes.Keys) {
            Write-Host (Format-Lane $key $lanes[$key])
        }
        exit 0
    }
    $item = Resolve-Item $Rest[0]
    $key = "$($item.Repo)#$($item.Number)"
    if (-not $lanes.Contains($key)) {
        $lanes[$key] = [ordered]@{}
    }
    foreach ($pair in @($Rest | Select-Object -Skip 1)) {
        if ($pair -notmatch '^(?<k>[A-Za-z][\w-]*)=(?<v>.*)$') {
            Stop-Usage "'$pair' is not key=value"
        }
        if ($Matches['v'] -eq 'null') {
            $lanes[$key].Remove($Matches['k'])
        }
        else {
            $lanes[$key][$Matches['k']] = $Matches['v']
        }
    }
    if ($Rest.Count -gt 1) {
        $lanes[$key]['updated'] = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
        ConvertTo-Json -InputObject $lanes -Depth 20 | Set-Content -LiteralPath $path
    }
    Write-Host (Format-Lane $key $lanes[$key])
    exit 0
}

switch ($Command) {
    'move' { Invoke-Move $Arguments }
    'status' { Invoke-Status $Arguments }
    'deploy' { Invoke-Deploy $Arguments }
    'wait' { Invoke-Wait $Arguments }
    'tree' { Invoke-Tree $Arguments }
    'lane' { Invoke-Lane $Arguments }
    default { Stop-Usage "unknown command '$Command'; use move, status, deploy, wait, tree or lane" }
}
