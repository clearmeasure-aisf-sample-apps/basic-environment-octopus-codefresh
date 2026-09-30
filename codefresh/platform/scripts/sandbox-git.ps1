#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Git helpers for the conformance pipelines' writes to <sandbox-app-repo>; dot-sourced by conformance-arm.ps1 and
    conformance-publish.ps1.

.DESCRIPTION
    ADR-IR34 "Test harness": conformance-arm pushes the run's sandbox commits; the results go to branch
    conformance-results. The token is an installation token of the GitHub App aisf-conformance, minted per run by
    conformance-github.ps1 (#44; it lives in this process as GITHUB_TOKEN for one hour) and reaches git through a
    credential helper that reads the environment when git calls it: never on a command line, never in a URL. A token
    older than 45 minutes is re-minted before the next git call.

    Functions (after '. sandbox-git.ps1'):
      Invoke-SandboxGit <git arguments>   git with that credential helper and the platform-conformance identity
      Get-SandboxUrl                      SANDBOX_GIT_URL, else https://github.com/<SANDBOX_APP_REPO>.git
      Test-SandboxRequirement             $true when SANDBOX_APP_REPO is set and the App token was minted; else $false,
                                          with the reason on standard error (App not configured: PENDING owner setup, #44)

    Environment: SANDBOX_APP_REPO (owner/name, a spec variable), the AISF_CONFORMANCE_APP_* inputs (see
    conformance-github.ps1); SANDBOX_GIT_URL overrides the remote (local rehearsals against a bare repository).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. (Join-Path $PSScriptRoot 'conformance-github.ps1')

function Invoke-SandboxGit {
    Update-ConformanceGitHubTokenIfStale
    # Single quotes on purpose: git's helper shell expands GITHUB_TOKEN when it runs.
    & git -c credential.helper= `
        -c 'credential.helper=!f() { echo username=x-access-token; echo "password=${GITHUB_TOKEN}"; }; f' `
        -c user.name=platform-conformance `
        -c user.email=platform-conformance@users.noreply.github.com `
        @args
}

function Get-SandboxUrl {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    if ($env:SANDBOX_GIT_URL) {
        return $env:SANDBOX_GIT_URL
    }
    if (-not $env:SANDBOX_APP_REPO) {
        throw 'SANDBOX_APP_REPO is not set'
    }
    return "https://github.com/$($env:SANDBOX_APP_REPO).git"
}

function Test-SandboxRequirement {
    [CmdletBinding()]
    [OutputType([bool])]
    param()

    if (-not $env:SANDBOX_APP_REPO -or $env:SANDBOX_APP_REPO -match '<[\s\S]*>') {
        [Console]::Error.WriteLine('sandbox-git: SANDBOX_APP_REPO is not set (spec variable)')
        return $false
    }
    # Mints the token of the GitHub App aisf-conformance; the reason (PENDING owner setup, or the HTTP status) is
    # reported by conformance-github.ps1.
    return (Initialize-ConformanceGitHubToken).State -eq 'app'
}
