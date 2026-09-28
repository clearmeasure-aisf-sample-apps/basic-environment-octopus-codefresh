#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Step prepare of workorders/release: did codefresh/ci already pass the exact tree of the commit under release?

.DESCRIPTION
    The release runs on the master merge commit M; workorders/ci ran on the pull request head. When a commit C carries a
    successful codefresh/ci status and has the same tree as M, the static gates (code_analysis, build_sqlite, qodana,
    security_scan) already passed on the very files the release builds, and the release need not run them again.
    build_sql (the Release build that package and the images use, with its unit, integration and CRAP gates) always
    runs.

    Candidates C, in order, at most -MaxCandidates:
      1. the parents of M after the first (a merge commit: parent 2 is the pull request head);
      2. the heads of the merged pull requests whose merge commit is M (GitHub commits/{M}/pulls; a squash merge);
      3. M itself (a fast-forward: CI ran on M).
    A candidate verifies M only when all of these hold:
      - git has it (a missing commit is fetched from origin by SHA) and its tree equals the tree of M;
      - the first parent of M (the master tip the change was merged into) is an ancestor of it, so CI tested the
        change on top of the same master, and its docs-only diff matched the release's;
      - GitHub's combined status of it holds the context -Context (codefresh/ci) in state success.

    Exports, with cf_export (Codefresh puts it on PATH in every freestyle step; the value travels in the environment):
      CI_TREE_VERIFIED  true when a candidate verified M, else false.
      CI_TREE_COMMIT    the verifying commit, else none.
    Fail closed: any error, a missing tool, a private repository (the API answers 404 without a token), a rate limit or
    RELEASE_FULL_GATES=true exports CI_TREE_VERIFIED=false, and every gate runs. The GitHub calls are anonymous: the app
    repo is public and the release carries no GitHub token (at most three calls per release, far below the 60 an hour
    GitHub allows an address).

    Usage (Codefresh step prepare of workorders/release, in the application checkout, full history):
      pwsh -NoProfile -File <env-repo>/codefresh/apps/workorders/scripts/ci-tree.ps1 -Repository <owner>/<repo>

.PARAMETER Repository
    The application repository, owner/name (default: CF_REPO_OWNER/CF_REPO_NAME).

.PARAMETER Context
    The status context that CI posts (default codefresh/ci).

.PARAMETER ApiUrl
    The GitHub REST API root (default: GITHUB_API_URL, else https://api.github.com).

.PARAMETER MaxCandidates
    At most this many candidate commits are checked (default 5).

.NOTES
    Exit code: 0 always once the parameters bind (a failure exports CI_TREE_VERIFIED=false).
#>
[CmdletBinding()]
param(
    [string] $Repository = $(if ($env:CF_REPO_OWNER -and $env:CF_REPO_NAME) { "$($env:CF_REPO_OWNER)/$($env:CF_REPO_NAME)" } else { '' }),
    [string] $Context = 'codefresh/ci',
    [string] $ApiUrl = $(if ($env:GITHUB_API_URL) { $env:GITHUB_API_URL } else { 'https://api.github.com' }),
    [ValidateRange(1, 20)]
    [int] $MaxCandidates = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$apiRoot = $ApiUrl.TrimEnd('/')
$candidateLimit = $MaxCandidates

# Exports a variable to the later steps: cf_export NAME when Codefresh put it on PATH (the value in the environment),
# else a NAME=value line in ${CF_VOLUME_PATH}/env_vars_to_export (the file cf_export writes).
function Export-CodefreshVariable([string] $Name, [string] $Value) {
    $cfExport = Get-Command -Name cf_export -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    # Codefresh's cf_export has no shebang line, so pwsh cannot start it: run it through a shell.
    $cfShell = (Get-Command -Name bash, sh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if ($cfExport) {
        [Environment]::SetEnvironmentVariable($Name, $Value)
        & $cfShell $cfExport.Source $Name
        return
    }
    if ($env:CF_VOLUME_PATH) {
        Add-Content -LiteralPath (Join-Path $env:CF_VOLUME_PATH 'env_vars_to_export') -Value "$Name=$Value"
    }
}

# Exports the decision, logs it and ends the script with exit code 0.
function Complete-Decision([string] $Commit, [string] $Reason) {
    $verified = [bool] $Commit
    Export-CodefreshVariable 'CI_TREE_VERIFIED' $(if ($verified) { 'true' } else { 'false' })
    Export-CodefreshVariable 'CI_TREE_COMMIT' $(if ($verified) { $Commit } else { 'none' })
    if ($verified) {
        Write-Host "ci-tree.ps1: CI_TREE_VERIFIED=true: $Reason; code_analysis, build_sqlite, qodana and security_scan exit early."
    }
    else {
        Write-Host "ci-tree.ps1: CI_TREE_VERIFIED=false: $Reason; every gate runs."
    }
    exit 0
}

# Runs git with its standard error discarded; returns its exit code and its standard output, trimmed.
function Invoke-Git([string[]] $Arguments) {
    $PSNativeCommandUseErrorActionPreference = $false
    $output = & git @Arguments 2>$null
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ((@($output) -join "`n").Trim()) }
}

# One anonymous GET of the GitHub API: the parsed JSON, or $null when curl fails or the answer is not JSON.
function Invoke-GitHubApi([string] $Path) {
    $PSNativeCommandUseErrorActionPreference = $false
    $output = curl -fsS --max-time 20 -H 'Accept: application/vnd.github+json' -H 'X-GitHub-Api-Version: 2022-11-28' "$apiRoot/$Path" 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }
    try {
        # -NoEnumerate and the unary comma keep a JSON array (even an empty one) as one value for the caller.
        return , ((@($output) -join "`n") | ConvertFrom-Json -NoEnumerate)
    }
    catch {
        return $null
    }
}

# The value of a property of a parsed JSON object, or $null when the object has no such property.
function Get-JsonValue([object] $Object, [string] $Name) {
    if ($null -eq $Object) {
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) {
        return $property.Value
    }
    return $null
}

# Why a candidate does not verify the release commit, or '' when it does.
function Test-Candidate([string] $Candidate) {
    if ((Invoke-Git @('cat-file', '-e', "$Candidate^{commit}")).ExitCode -ne 0) {
        $null = Invoke-Git @('fetch', '--no-tags', '--quiet', 'origin', $Candidate)
    }
    $candidateTree = Invoke-Git @('rev-parse', '--verify', '--quiet', "$Candidate^{tree}")
    if ($candidateTree.ExitCode -ne 0) {
        return 'not in the clone and not fetchable'
    }
    if ($candidateTree.Text -cne $tree) {
        return "tree $($candidateTree.Text) differs from $tree"
    }
    if ((Invoke-Git @('merge-base', '--is-ancestor', $firstParent, $Candidate)).ExitCode -ne 0) {
        return "does not contain the first parent $firstParent"
    }
    $status = Invoke-GitHubApi "repos/$Repository/commits/$Candidate/status"
    if ($null -eq $status) {
        return 'the commit status API did not answer'
    }
    $states = @(@(Get-JsonValue $status 'statuses') | Where-Object { $null -ne $_ -and (Get-JsonValue $_ 'context') -ceq $Context } | ForEach-Object { Get-JsonValue $_ 'state' })
    if ($states -notcontains 'success') {
        return "$Context is $(if ($states.Count -gt 0) { $states -join ', ' } else { 'not reported' })"
    }
    return ''
}

try {
    if ($env:RELEASE_FULL_GATES -ceq 'true') {
        Complete-Decision '' 'RELEASE_FULL_GATES=true'
    }
    if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        Complete-Decision '' "no repository (owner/name) given: '$Repository'"
    }
    if (-not (Get-Command -Name git -CommandType Application -ErrorAction SilentlyContinue)) {
        Complete-Decision '' 'git is not on PATH'
    }
    if (-not (Get-Command -Name curl -CommandType Application -ErrorAction SilentlyContinue)) {
        Complete-Decision '' 'curl is not on PATH'
    }

    $headResult = Invoke-Git @('rev-parse', '--verify', '--quiet', 'HEAD^{commit}')
    $treeResult = Invoke-Git @('rev-parse', '--verify', '--quiet', 'HEAD^{tree}')
    $parentsResult = Invoke-Git @('rev-list', '--parents', '-n', '1', 'HEAD')
    if ($headResult.ExitCode -ne 0 -or $treeResult.ExitCode -ne 0 -or $parentsResult.ExitCode -ne 0) {
        Complete-Decision '' 'not inside a git repository with a HEAD commit'
    }
    $head = $headResult.Text
    $tree = $treeResult.Text
    $parents = @($parentsResult.Text -split '\s+' | Where-Object { $_ } | Select-Object -Skip 1)
    if ($parents.Count -eq 0) {
        Complete-Decision '' "$head has no parent"
    }
    $firstParent = $parents[0]
    Write-Host "ci-tree.ps1: release commit $head, tree $tree, first parent $firstParent"

    $checked = [System.Collections.Generic.List[string]]::new()
    # Checks the candidates in order and ends the script on the first one that verifies the release commit.
    function Test-CandidateList([string[]] $Candidates, [string] $Kind) {
        foreach ($candidate in $Candidates) {
            if ($candidate -cnotmatch '^[0-9a-f]{40}$' -or $checked.Contains($candidate) -or $checked.Count -ge $candidateLimit) {
                continue
            }
            $checked.Add($candidate)
            $reason = Test-Candidate $candidate
            if (-not $reason) {
                Complete-Decision $candidate "$Context passed $candidate ($Kind), whose tree $tree is the release commit's"
            }
            Write-Host "ci-tree.ps1: $candidate ($Kind): $reason"
        }
    }

    Test-CandidateList @($parents | Select-Object -Skip 1) 'merged parent'
    $pulls = Invoke-GitHubApi "repos/$Repository/commits/$head/pulls"
    if ($null -eq $pulls) {
        Write-Host 'ci-tree.ps1: the pull request API did not answer'
    }
    else {
        $heads = @(@($pulls) | Where-Object { $null -ne $_ -and (Get-JsonValue $_ 'merge_commit_sha') -ceq $head } | ForEach-Object { Get-JsonValue (Get-JsonValue $_ 'head') 'sha' } | Where-Object { $_ -is [string] })
        Test-CandidateList $heads 'pull request head'
    }
    Test-CandidateList @($head) 'the release commit'
    Complete-Decision '' "no commit with a successful $Context has the release commit's tree (checked $($checked.Count))"
}
catch {
    Complete-Decision '' "the check failed: $($_.Exception.Message)"
}
