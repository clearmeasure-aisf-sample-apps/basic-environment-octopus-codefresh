#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Prints the paths changed by the commit under test, one per line, for the application repo's own docs-only
    classifier.

.DESCRIPTION
    The classifier reads the list on standard input:
      .github/scripts/detect-code-changes.sh --from-list -
    Run it from the application checkout (<app-repo>);
    this script lives in the environment repo under codefresh/apps/<app>/scripts/.

      release branch (RELEASE_BRANCH, default master):
                git diff HEAD^1 HEAD  (the first-parent diff; for a merge commit,
                everything the pull request brings in)
      branches: git diff $(git merge-base origin/<release branch> HEAD) HEAD  (what the
                pull request would merge), as in build.yml job "Detect code changes"
    Codefresh exposes no equivalent of GitHub's event.before, so master uses the
    first parent instead.

    Fail open: on any error the script prints a sentinel path that is not
    documentation, so the classifier reports code=true and every gate runs. It
    always exits 0. An empty diff prints nothing, which the classifier treats as
    docs-only, matching build.yml.

.PARAMETER Branch
    Branch under test (default: CF_BRANCH, else the current branch).

.PARAMETER Repo
    The application checkout (default: git rev-parse --show-toplevel).

.OUTPUTS
    One changed path per line, or the sentinel path changed-paths-unavailable. The log goes to standard error.

.NOTES
    Exit code: 0 always once the parameters bind (a runtime error prints the sentinel).
#>
[CmdletBinding()]
param(
    [string] $Branch = $env:CF_BRANCH,
    [string] $Repo = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Not *.md, not docs/*, not LICENSE*, not .github/ISSUE_TEMPLATE/*: counts as code.
$sentinel = 'changed-paths-unavailable'
$releaseBranch = if ($env:RELEASE_BRANCH) { $env:RELEASE_BRANCH } else { 'master' }

function Exit-FailOpen([string] $Message) {
    [Console]::Error.WriteLine("changed-paths.ps1: $Message; failing open (all gates run)")
    Write-Output $sentinel
    exit 0
}

# Runs git with its standard error discarded; returns its exit code and its standard output lines.
function Invoke-Git([string[]] $Arguments) {
    $PSNativeCommandUseErrorActionPreference = $false
    $output = & git @Arguments 2>$null
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Lines = @($output) }
}

$entered = $false
try {
    if (-not $Repo) {
        $toplevel = Invoke-Git @('rev-parse', '--show-toplevel')
        if ($toplevel.ExitCode -ne 0) {
            Exit-FailOpen 'not inside a git repository'
        }
        $Repo = $toplevel.Lines -join "`n"
    }
    try {
        Push-Location -LiteralPath $Repo
        $entered = $true
    }
    catch {
        Exit-FailOpen "cannot enter $Repo"
    }

    if (-not $Branch) {
        $current = Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')
        if ($current.ExitCode -ne 0) {
            Exit-FailOpen 'cannot resolve the branch'
        }
        $Branch = $current.Lines -join "`n"
    }

    if ($Branch -ceq $releaseBranch) {
        if ((Invoke-Git @('rev-parse', '--verify', '--quiet', 'HEAD^1')).ExitCode -ne 0) {
            Exit-FailOpen 'HEAD has no parent'
        }
        $base = 'HEAD^1'
    }
    else {
        # Refresh origin/<release branch>; a failed fetch still leaves an existing ref usable.
        $null = Invoke-Git @('fetch', '--no-tags', '--quiet', 'origin', "${releaseBranch}:refs/remotes/origin/${releaseBranch}")
        if ((Invoke-Git @('rev-parse', '--verify', '--quiet', "origin/$releaseBranch")).ExitCode -ne 0) {
            Exit-FailOpen "origin/$releaseBranch is unavailable"
        }
        $mergeBase = Invoke-Git @('merge-base', "origin/$releaseBranch", 'HEAD')
        if ($mergeBase.ExitCode -ne 0) {
            Exit-FailOpen 'git merge-base failed'
        }
        $base = $mergeBase.Lines -join "`n"
        if (-not $base) {
            Exit-FailOpen 'empty merge-base'
        }
    }

    # --no-renames: a code-to-docs rename still lists the removed code path (as in build.yml).
    $diff = Invoke-Git @('diff', '--name-only', '--no-renames', $base, 'HEAD')
    if ($diff.ExitCode -ne 0) {
        Exit-FailOpen 'git diff failed'
    }
}
catch {
    Exit-FailOpen "unexpected error: $($_.Exception.Message)"
}
finally {
    if ($entered) {
        Pop-Location
    }
}

$diff.Lines
exit 0
