#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Library (dot-source it): mints GitHub App installation tokens for the board app aisf-board and the conformance app
    aisf-conformance, and resolves the GitHub token unattended tooling should use.

.DESCRIPTION
    Dot-source it, then call:

      New-GitHubAppJwt                     RS256 JWT of the App (iat = now - 60, exp = now + 540, iss = app id).
      Get-GitHubAppInstallationToken       Exchanges the JWT for a one-hour installation token, narrowed to the given
                                           repositories and permissions (POST /app/installations/{id}/access_tokens).
      Get-GitHubCliToken                   The token of the GitHub CLI ('gh auth token'), else GH_TOKEN / GITHUB_TOKEN.
      Resolve-GitHubToken                  The order unattended tooling uses (below); returns @{ Token; Source }.
      Resolve-GitHubAppToken               The installation token of another App by environment prefix (the conformance
                                           App aisf-conformance, -Prefix AISF_CONFORMANCE_APP); returns
                                           @{ Token; Source; State (app|unset|failed); Reason }. No fallback to a PAT.

    Every function that reads the key takes -Prefix (default AISF_BOARD_APP, so the board is unchanged):
    <Prefix>_PRIVATE_KEY_PATH, <Prefix>_PRIVATE_KEY and <Prefix>_INSTALLATION_ID.

    Private key: read from the file named by AISF_BOARD_APP_PRIVATE_KEY_PATH, else from the PEM text in
    AISF_BOARD_APP_PRIVATE_KEY (for the conformance App, the AISF_CONFORMANCE_APP_ equivalents). It is held in a local variable only: never echoed, never in an error message, never in a
    process argument, never written to disk. Neither is a JWT or a token ever printed; an error names the App id and the
    HTTP status only. The functions return the token in-process to the caller, who puts it in a request header.

    Token order of Resolve-GitHubToken:
      1. AISF_BOARD_APP_TOKEN        a pre-minted installation token
      2. minted from the App         AISF_BOARD_APP_ID (default: the app id of factory-loop.json, block githubApp) and
                                     the private key above; AISF_BOARD_APP_INSTALLATION_ID overrides the installation id
      3. the GitHub CLI              'gh auth token' (which itself honours GH_TOKEN and GITHUB_TOKEN); when gh is not
                                     installed, GH_TOKEN or GITHUB_TOKEN directly (gh-less cloud sessions)
    No personal access token variable of the platform exists any more.

    GITHUB_API_URL overrides the API base URL (https://api.github.com); the workflow honours it too, and the tests use it
    to point the scripts at a stub server.

    Documentation: docs/runbooks/credential-rotation.md, section "GitHub App aisf-board".
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

# >>> scripts/github/GitHubAppAuth.ps1
# Marked region: the Octopus step report-commit-status (.octopus/apps/workorders/workorders/deployment_process.ocl) inlines
# these functions verbatim (indentation removed); a conformance test fails when the copy and this region differ. Keep
# the region free of dollar-brace, percent-brace and hash-brace sequences (OCL heredocs forbid them): write $($AppId).
function ConvertTo-Base64Url([byte[]] $Bytes) {
    return [Convert]::ToBase64String($Bytes).Replace('+', '-').Replace('/', '_').Replace('=', '')
}

function Get-GitHubApiBase {
    $base = if ($env:GITHUB_API_URL) { $env:GITHUB_API_URL } else { 'https://api.github.com' }
    return $base.TrimEnd('/')
}

# PEM text as a secret store may hand it over: a PEM kept on one line writes its line breaks as the two characters
# backslash and n; those become real line breaks. Nothing else changes and nothing is echoed.
function ConvertTo-GitHubAppPem([string] $Pem) {
    if ($Pem -notmatch "\n" -and $Pem.Contains('\n')) {
        return $Pem.Replace('\n', "`n")
    }
    return $Pem
}

# The PEM text of the App's private key: the file of AISF_BOARD_APP_PRIVATE_KEY_PATH, else AISF_BOARD_APP_PRIVATE_KEY.
# $null when neither is set. Errors name the variable, never the content.
function Get-GitHubAppPrivateKey {
    param([string] $Prefix = 'AISF_BOARD_APP')
    $keyPath = [Environment]::GetEnvironmentVariable("$($Prefix)_PRIVATE_KEY_PATH")
    $keyText = [Environment]::GetEnvironmentVariable("$($Prefix)_PRIVATE_KEY")
    if ($keyPath) {
        if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
            throw "GitHub App private key: the file named by $($Prefix)_PRIVATE_KEY_PATH does not exist."
        }
        return [System.IO.File]::ReadAllText($keyPath)
    }
    if ($keyText) {
        return ConvertTo-GitHubAppPem $keyText
    }
    return $null
}

function Test-GitHubAppPrivateKey {
    param([string] $Prefix = 'AISF_BOARD_APP')
    return [bool]([Environment]::GetEnvironmentVariable("$($Prefix)_PRIVATE_KEY_PATH") -or [Environment]::GetEnvironmentVariable("$($Prefix)_PRIVATE_KEY"))
}

<#
.SYNOPSIS
    The RS256 JWT of the App: header {"alg":"RS256","typ":"JWT"}, claims iat = Now - 60, exp = Now + 540, iss = AppId.
.PARAMETER Now
    Unix seconds; the clock of the token (a parameter so tests are deterministic).
.PARAMETER PrivateKey
    The PEM text of the private key, held in memory only (the Octopus step gets it from a sensitive variable). When given
    it replaces the file and environment sources: no environment variable and no file is involved. Never echoed.
#>
function New-GitHubAppJwt {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $AppId,
        [long] $Now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds(),
        [string] $PrivateKey = '',
        [string] $Prefix = 'AISF_BOARD_APP'
    )
    $pem = if ($PrivateKey) { ConvertTo-GitHubAppPem $PrivateKey } else { Get-GitHubAppPrivateKey -Prefix $Prefix }
    if (-not $pem) {
        throw "GitHub App $($AppId): no private key (set $($Prefix)_PRIVATE_KEY_PATH to a PEM file, or $($Prefix)_PRIVATE_KEY to the PEM text)."
    }
    $header = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"RS256","typ":"JWT"}'))
    $claims = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"iat":' + ($Now - 60) + ',"exp":' + ($Now + 540) + ',"iss":"' + $AppId + '"}'))
    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        try {
            $rsa.ImportFromPem($pem)
        }
        catch [System.ArgumentException], [System.Security.Cryptography.CryptographicException] {
            throw "GitHub App $($AppId): the private key is not a valid RSA PEM key."
        }
        $signature = $rsa.SignData([Text.Encoding]::UTF8.GetBytes("$header.$claims"), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    }
    finally {
        $rsa.Dispose()
    }
    return "$header.$claims.$(ConvertTo-Base64Url $signature)"
}

# One GitHub API call with the App JWT or a token; returns @{ Status; Json } and never throws on an HTTP status.
function Invoke-GitHubAppApi([string] $Method, [string] $Path, [string] $Bearer, [string] $Body, [string] $AppId) {
    $headers = @{ Authorization = "Bearer $Bearer"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $arguments = @{ Uri = "$(Get-GitHubApiBase)$Path"; Method = $Method; Headers = $headers; SkipHttpErrorCheck = $true; TimeoutSec = 30 }
    if ($Body) {
        $arguments['Body'] = $Body
        $arguments['ContentType'] = 'application/json'
    }
    try {
        $answer = Invoke-WebRequest @arguments
    }
    catch [System.Net.Http.HttpRequestException], [System.Threading.Tasks.TaskCanceledException] {
        throw "GitHub App $($AppId): $Method $Path -> no response."
    }
    $json = $null
    if ($answer.Content) {
        try {
            $json = $answer.Content | ConvertFrom-Json -AsHashtable
        }
        catch [System.ArgumentException] {
            $json = $null
        }
    }
    return @{ Status = [int]$answer.StatusCode; Json = $json }
}

<#
.SYNOPSIS
    An installation access token of the App: valid one hour, limited to the given repositories and permissions.
.PARAMETER Repository
    owner/repo names; the token can reach only these. The first also finds the installation when its id is not given.
.PARAMETER Permission
    Hashtable such as @{ organization_projects = 'write'; issues = 'read' }; the token gets these and no more.
.PARAMETER InstallationId
    Installation id; default <Prefix>_INSTALLATION_ID (AISF_BOARD_APP_INSTALLATION_ID), else discovered with
    GET /repos/{owner}/{repo}/installation.
.PARAMETER PrivateKey
    The PEM text of the private key, in memory only; see New-GitHubAppJwt. Default: the file or environment sources.
.PARAMETER Prefix
    Environment-variable prefix of the App: <Prefix>_PRIVATE_KEY_PATH, <Prefix>_PRIVATE_KEY, <Prefix>_INSTALLATION_ID.
    AISF_BOARD_APP (default, the board App) or AISF_CONFORMANCE_APP (the conformance App aisf-conformance).
#>
function Get-GitHubAppInstallationToken {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $AppId,
        [Parameter(Mandatory)] [string[]] $Repository,
        [Parameter(Mandatory)] [hashtable] $Permission,
        [string] $InstallationId = '',
        [long] $Now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds(),
        [string] $PrivateKey = '',
        [string] $Prefix = 'AISF_BOARD_APP'
    )
    $jwt = New-GitHubAppJwt -AppId $AppId -Now $Now -PrivateKey $PrivateKey -Prefix $Prefix
    if (-not $InstallationId) {
        $InstallationId = [string][Environment]::GetEnvironmentVariable("$($Prefix)_INSTALLATION_ID")
    }
    if (-not $InstallationId) {
        $found = Invoke-GitHubAppApi 'Get' "/repos/$($Repository[0])/installation" $jwt '' $AppId
        if ($found.Status -ne 200 -or -not $found.Json -or -not $found.Json.ContainsKey('id')) {
            throw "GitHub App $($AppId): no installation found for $($Repository[0]) (HTTP $($found.Status))."
        }
        $InstallationId = [string]$found.Json['id']
    }
    $names = @($Repository | ForEach-Object { ($_ -split '/')[-1] })
    $body = [ordered]@{ repositories = $names; permissions = $Permission } | ConvertTo-Json -Depth 5 -Compress
    $minted = Invoke-GitHubAppApi 'Post' "/app/installations/$InstallationId/access_tokens" $jwt $body $AppId
    if ($minted.Status -ne 201 -or -not $minted.Json -or -not $minted.Json.ContainsKey('token') -or -not $minted.Json['token']) {
        throw "GitHub App $($AppId): no installation token (HTTP $($minted.Status))."
    }
    return [string]$minted.Json['token']
}
# <<< scripts/github/GitHubAppAuth.ps1

<#
.SYNOPSIS
    The installation token of another App of the platform (the conformance App aisf-conformance), read from the
    environment variables of its prefix; returns @{ Token; Source; State; Reason }.
.DESCRIPTION
    State 'app'    a token was minted (Source 'app'; Token holds it, in-process only).
    State 'unset'  the App id, the installation id or the private key is missing: nothing was requested, Token is $null.
                   This is the pending state before the owner has created the App (docs/runbooks/credential-rotation.md).
    State 'failed' the exchange was refused or gave no answer: Token is $null and Reason holds the HTTP status only,
                   never a key, JWT or token. There is no fallback to any other credential.
.PARAMETER Prefix
    <Prefix>_ID, <Prefix>_INSTALLATION_ID, <Prefix>_PRIVATE_KEY_PATH / <Prefix>_PRIVATE_KEY.
.PARAMETER Repository
    owner/repo names the token can reach.
.PARAMETER Permission
    Hashtable of the permissions the token gets, for example @{ contents = 'write'; metadata = 'read' }.
#>
function Resolve-GitHubAppToken {
    [CmdletBinding()]
    param(
        [string] $Prefix = 'AISF_CONFORMANCE_APP',
        [Parameter(Mandatory)] [string[]] $Repository,
        [Parameter(Mandatory)] [hashtable] $Permission,
        [long] $Now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    )
    $appId = [string][Environment]::GetEnvironmentVariable("${Prefix}_ID")
    $installation = [string][Environment]::GetEnvironmentVariable("${Prefix}_INSTALLATION_ID")
    $missing = @()
    if (-not $appId) { $missing += "${Prefix}_ID" }
    if (-not $installation) { $missing += "${Prefix}_INSTALLATION_ID" }
    if (-not (Test-GitHubAppPrivateKey -Prefix $Prefix)) { $missing += "${Prefix}_PRIVATE_KEY" }
    if ($missing.Count -gt 0) {
        return @{ Token = $null; Source = 'none'; State = 'unset'; Reason = "not set: $($missing -join ', ')" }
    }
    try {
        $token = Get-GitHubAppInstallationToken -AppId $appId -Repository $Repository -Permission $Permission -InstallationId $installation -Now $Now -Prefix $Prefix
        return @{ Token = $token; Source = 'app'; State = 'app'; Reason = '' }
    }
    catch [System.Management.Automation.RuntimeException] {
        # The message names the App id and the HTTP status only, never a key, JWT or token; keep the status alone.
        $message = $_.Exception.Message
        $reason = if ($message -match 'HTTP (\d{3})') { "HTTP $($Matches[1])" } elseif ($message -match 'no response') { 'no response' } elseif ($message -match 'private key') { 'the private key was not usable' } else { 'refused' }
        return @{ Token = $null; Source = 'none'; State = 'failed'; Reason = $reason }
    }
}

# The token of the GitHub CLI; gh honours GH_TOKEN / GITHUB_TOKEN itself. Without gh installed, those variables directly
# (a cloud session has no gh login). $null when there is none.
function Get-GitHubCliToken {
    [CmdletBinding()]
    [OutputType([string])]
    param()
    if (Get-Command -Name gh -CommandType Application -ErrorAction SilentlyContinue) {
        $saved = $PSNativeCommandUseErrorActionPreference
        $PSNativeCommandUseErrorActionPreference = $false
        try {
            $value = (gh auth token 2>$null | Out-String).Trim()
        }
        finally {
            $PSNativeCommandUseErrorActionPreference = $saved
        }
        if ($value) {
            return $value
        }
    }
    foreach ($name in @('GH_TOKEN', 'GITHUB_TOKEN')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) {
            return $value
        }
    }
    return $null
}

<#
.SYNOPSIS
    The GitHub token for unattended tooling, in the documented order; returns @{ Token; Source } (Source is 'app' or
    'gh') or $null when there is none.
.PARAMETER AppConfig
    The githubApp block of factory-loop.json (appId, installationId, repositories, permissions); optional.
#>
function Resolve-GitHubToken {
    [CmdletBinding()]
    param(
        [hashtable] $AppConfig = @{}
    )
    if ($env:AISF_BOARD_APP_TOKEN) {
        return @{ Token = $env:AISF_BOARD_APP_TOKEN; Source = 'app' }
    }
    $appId = if ($env:AISF_BOARD_APP_ID) { $env:AISF_BOARD_APP_ID } elseif ($AppConfig.ContainsKey('appId')) { [string]$AppConfig['appId'] } else { '' }
    if ($appId -and (Test-GitHubAppPrivateKey) -and $AppConfig.ContainsKey('repositories') -and $AppConfig.ContainsKey('permissions')) {
        $installation = if ($AppConfig.ContainsKey('installationId')) { [string]$AppConfig['installationId'] } else { '' }
        try {
            $token = Get-GitHubAppInstallationToken -AppId $appId -Repository @($AppConfig['repositories']) -Permission $AppConfig['permissions'] -InstallationId $installation
            return @{ Token = $token; Source = 'app' }
        }
        catch [System.Management.Automation.RuntimeException] {
            # The message names the App id and the HTTP status only, never a key or token.
            Write-Host "github-app: $($_.Exception.Message) Falling back to the GitHub CLI token."
        }
    }
    $cli = Get-GitHubCliToken
    if ($cli) {
        return @{ Token = $cli; Source = 'gh' }
    }
    return $null
}
