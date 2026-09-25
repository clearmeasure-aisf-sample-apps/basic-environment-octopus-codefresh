#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Git helpers for the conformance pipelines' writes to <sandbox-app-repo>; dot-sourced by conformance-arm.ps1 and
    conformance-publish.ps1.

.DESCRIPTION
    ADR-IR34 "Test harness": conformance-arm pushes the run's sandbox commits; the results go to branch
    conformance-results. The token (GITHUB_TOKEN, context platform-conformance) reaches git through a credential
    helper that reads the environment when git calls it: never on a command line, never in a URL.

    Functions (after '. sandbox-git.ps1'):
      Invoke-SandboxGit <git arguments>   git with that credential helper and the platform-conformance identity
      Get-SandboxUrl                      SANDBOX_GIT_URL, else https://github.com/<SANDBOX_APP_REPO>.git
      Test-SandboxRequirement             $true when GITHUB_TOKEN and SANDBOX_APP_REPO are set; else $false, with
                                          the reason on standard error

    Environment: SANDBOX_APP_REPO (owner/name, a spec variable), GITHUB_TOKEN; SANDBOX_GIT_URL overrides the remote
    (local rehearsals against a bare repository).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Invoke-SandboxGit {
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

    if (-not $env:GITHUB_TOKEN) {
        [Console]::Error.WriteLine('sandbox-git: GITHUB_TOKEN is not set (context platform-conformance)')
        return $false
    }
    if (-not $env:SANDBOX_APP_REPO -or $env:SANDBOX_APP_REPO -match '<[\s\S]*>') {
        [Console]::Error.WriteLine('sandbox-git: SANDBOX_APP_REPO is not set (spec variable)')
        return $false
    }
    return $true
}
