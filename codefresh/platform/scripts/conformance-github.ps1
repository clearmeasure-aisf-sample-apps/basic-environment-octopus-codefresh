#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Mints the GitHub token of a conformance script from the GitHub App aisf-conformance; dot-sourced by sandbox-git.ps1,
    conformance-run.ps1 and (through sandbox-git.ps1) conformance-arm.ps1 and conformance-publish.ps1.

.DESCRIPTION
    The conformance pipelines and the end-to-end pass no longer use a personal access token (#44). Each run exchanges the
    App's private key for a one-hour installation token (scripts/github/GitHubAppAuth.ps1, Resolve-GitHubAppToken),
    narrowed to the repositories and permissions of codefresh/platform/github-app.json:
      repositories  the environment repository, 20260923-001, the sandbox repository (SANDBOX_APP_REPO) and, when set,
                    PLATFORM_E2E_REPO: the installation covers exactly three repositories;
      permissions   Contents rw, Pull requests rw, Commit statuses read, Metadata read.
    The token is set in this process only: $env:GITHUB_TOKEN (the .NET harness and the git credential helper of
    sandbox-git.ps1 keep their GITHUB_TOKEN contract) and, when a token file is wanted, the file named by
    GITHUB_TOKEN_FILE (mode 0600, replaced atomically), which the harness re-reads per request so a suite longer than one
    hour keeps working. Nothing is printed: not the key, not a JWT, not the token; a failure names the HTTP status only.

    Inputs (Codefresh context platform-conformance; Octopus variables E2E.GitHubApp* for the end-to-end pass):
      AISF_CONFORMANCE_APP_ID, AISF_CONFORMANCE_APP_INSTALLATION_ID, and AISF_CONFORMANCE_APP_PRIVATE_KEY (the PEM text)
      or AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH (a PEM file; operators).

    Functions (after '. conformance-github.ps1'):
      Get-ConformanceGitHubAppConfig            the parsed github-app.json
      Get-ConformanceGitHubRepositoryList         the repositories the token is narrowed to
      Test-ConformanceGitHubAppInput            names of the App inputs that are missing (empty: all set)
      Initialize-ConformanceGitHubToken         mints; returns @{ State (app|unset|failed); Reason }; sets GITHUB_TOKEN and,
                                                with -TokenFile, GITHUB_TOKEN_FILE; on unset or failed it clears both and
                                                reports once on standard error (PENDING owner setup, or the HTTP status)
      Update-ConformanceGitHubToken             the same for a re-mint during a long run: keeps the old token when the
                                                exchange fails
      Update-ConformanceGitHubTokenIfStale      re-mints when the token is older than refreshMinutes

    No fallback: with the App unset or refused there is no GITHUB_TOKEN, whatever else the environment holds.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. (Join-Path $PSScriptRoot '..' '..' '..' 'scripts' 'github' 'GitHubAppAuth.ps1')

$script:ConformanceAppConfigPath = Join-Path $PSScriptRoot '..' 'github-app.json'
$script:ConformanceTokenMintedAt = $null

function Get-ConformanceGitHubAppConfig {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param()

    return Get-Content -LiteralPath $script:ConformanceAppConfigPath -Raw | ConvertFrom-Json -AsHashtable
}

function Get-ConformanceGitHubRepositoryList {
    [CmdletBinding()]
    [OutputType([string[]])]
    param()

    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @((Get-ConformanceGitHubAppConfig)['repositories']) + @($env:SANDBOX_APP_REPO, $env:PLATFORM_E2E_REPO)) {
        if ($name -and $name -notmatch '<[\s\S]*>' -and -not $names.Contains($name)) {
            $names.Add($name)
        }
    }
    return $names.ToArray()
}

function Test-ConformanceGitHubAppInput {
    [CmdletBinding()]
    [OutputType([string[]])]
    param()

    $prefix = [string](Get-ConformanceGitHubAppConfig)['environmentPrefix']
    $missing = @()
    foreach ($suffix in '_ID', '_INSTALLATION_ID') {
        if (-not [Environment]::GetEnvironmentVariable("$prefix$suffix")) {
            $missing += "$prefix$suffix"
        }
    }
    if (-not (Test-GitHubAppPrivateKey -Prefix $prefix)) {
        $missing += "${prefix}_PRIVATE_KEY"
    }
    return $missing
}

# Writes the token to a file readable only by its owner (mode 0600 outside Windows) and swaps it in atomically, so a
# reader sees the old or the new token, never a partial one.
function Write-ConformanceGitHubTokenFile([string] $Path, [string] $Token) {
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    $options = [System.IO.FileStreamOptions]::new()
    $options.Mode = [System.IO.FileMode]::CreateNew
    $options.Access = [System.IO.FileAccess]::Write
    if (-not $IsWindows) {
        $options.UnixCreateMode = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite
    }
    $stream = [System.IO.FileStream]::new($temporary, $options)
    try {
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Token)
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally {
        $stream.Dispose()
    }
    [System.IO.File]::Move($temporary, $Path, $true)
}

function Write-ConformanceGitHubNote([string] $Message) {
    [Console]::Error.WriteLine("conformance-github: $Message")
}

# One exchange; returns @{ State; Reason } and sets GITHUB_TOKEN (and the token file) only on success.
function Invoke-ConformanceGitHubMint {
    $config = Get-ConformanceGitHubAppConfig
    $permission = @{}
    foreach ($key in $config['permissions'].Keys) {
        $permission[$key] = [string]$config['permissions'][$key]
    }
    $resolved = Resolve-GitHubAppToken -Prefix ([string]$config['environmentPrefix']) -Repository (Get-ConformanceGitHubRepositoryList) -Permission $permission
    if ($resolved.State -eq 'app') {
        $env:GITHUB_TOKEN = $resolved.Token
        if ($env:GITHUB_TOKEN_FILE) {
            Write-ConformanceGitHubTokenFile $env:GITHUB_TOKEN_FILE $resolved.Token
        }
        $script:ConformanceTokenMintedAt = [DateTime]::UtcNow
    }
    return @{ State = $resolved.State; Reason = $resolved.Reason }
}

function Initialize-ConformanceGitHubToken {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        # Also keep the token in a file (GITHUB_TOKEN_FILE) that the harness re-reads per request; a temporary file
        # is created when the variable is not set.
        [switch] $TokenFile
    )

    # No fallback: a GITHUB_TOKEN of the environment (a stale PAT of the context) is dropped first.
    $env:GITHUB_TOKEN = $null
    if ($TokenFile) {
        if (-not $env:GITHUB_TOKEN_FILE) {
            $env:GITHUB_TOKEN_FILE = Join-Path ([System.IO.Path]::GetTempPath()) "conformance-github-$([Guid]::NewGuid().ToString('N')).token"
        }
    }
    elseif ($env:GITHUB_TOKEN_FILE) {
        $env:GITHUB_TOKEN_FILE = $null
    }
    $result = Invoke-ConformanceGitHubMint
    if ($result.State -eq 'unset') {
        $env:GITHUB_TOKEN_FILE = $null
        Write-ConformanceGitHubNote 'GitHub App aisf-conformance is not configured (AISF_CONFORMANCE_APP_ID / _INSTALLATION_ID / _PRIVATE_KEY): PENDING owner setup, #44'
    }
    elseif ($result.State -eq 'failed') {
        $env:GITHUB_TOKEN_FILE = $null
        Write-ConformanceGitHubNote "mint refused ($($result.Reason))"
    }
    return $result
}

function Update-ConformanceGitHubToken {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param()

    $previous = $env:GITHUB_TOKEN
    $result = Invoke-ConformanceGitHubMint
    if ($result.State -ne 'app') {
        $env:GITHUB_TOKEN = $previous
        Write-ConformanceGitHubNote "re-mint failed ($($result.Reason)); the previous token stays in use until it expires"
    }
    return $result
}

function Update-ConformanceGitHubTokenIfStale {
    [CmdletBinding()]
    param()

    if (-not $env:GITHUB_TOKEN -or $null -eq $script:ConformanceTokenMintedAt) {
        return
    }
    $minutes = [double](Get-ConformanceGitHubAppConfig)['refreshMinutes']
    if (([DateTime]::UtcNow - $script:ConformanceTokenMintedAt).TotalMinutes -ge $minutes) {
        Update-ConformanceGitHubToken | Out-Null
    }
}
