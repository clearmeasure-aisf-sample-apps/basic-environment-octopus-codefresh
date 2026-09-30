#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Library (dot-source it): mints GitHub App installation tokens for the board app aisf-board, and resolves the
    GitHub token unattended tooling should use.

.DESCRIPTION
    Dot-source it, then call:

      New-GitHubAppJwt                     RS256 JWT of the App (iat = now - 60, exp = now + 540, iss = app id).
      Get-GitHubAppInstallationToken       Exchanges the JWT for a one-hour installation token, narrowed to the given
                                           repositories and permissions (POST /app/installations/{id}/access_tokens).
      Get-GitHubCliToken                   The token of the GitHub CLI ('gh auth token'), else GH_TOKEN / GITHUB_TOKEN.
      Resolve-GitHubToken                  The order unattended tooling uses (below); returns @{ Token; Source }.

    Private key: read from the file named by AISF_BOARD_APP_PRIVATE_KEY_PATH, else from the PEM text in
    AISF_BOARD_APP_PRIVATE_KEY. It is held in a local variable only: never echoed, never in an error message, never in a
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

function ConvertTo-Base64Url([byte[]] $Bytes) {
    return [Convert]::ToBase64String($Bytes).Replace('+', '-').Replace('/', '_').Replace('=', '')
}

function Get-GitHubApiBase {
    $base = if ($env:GITHUB_API_URL) { $env:GITHUB_API_URL } else { 'https://api.github.com' }
    return $base.TrimEnd('/')
}

# The PEM text of the App's private key: the file of AISF_BOARD_APP_PRIVATE_KEY_PATH, else AISF_BOARD_APP_PRIVATE_KEY.
# $null when neither is set. Errors name the variable, never the content.
function Get-GitHubAppPrivateKey {
    if ($env:AISF_BOARD_APP_PRIVATE_KEY_PATH) {
        if (-not (Test-Path -LiteralPath $env:AISF_BOARD_APP_PRIVATE_KEY_PATH -PathType Leaf)) {
            throw 'GitHub App private key: the file named by AISF_BOARD_APP_PRIVATE_KEY_PATH does not exist.'
        }
        return [System.IO.File]::ReadAllText($env:AISF_BOARD_APP_PRIVATE_KEY_PATH)
    }
    if ($env:AISF_BOARD_APP_PRIVATE_KEY) {
        $pem = $env:AISF_BOARD_APP_PRIVATE_KEY
        # A secret store that keeps the PEM on one line writes the line breaks as the two characters backslash and n.
        if ($pem -notmatch "\n" -and $pem.Contains('\n')) {
            $pem = $pem.Replace('\n', "`n")
        }
        return $pem
    }
    return $null
}

function Test-GitHubAppPrivateKey {
    return [bool]($env:AISF_BOARD_APP_PRIVATE_KEY_PATH -or $env:AISF_BOARD_APP_PRIVATE_KEY)
}

<#
.SYNOPSIS
    The RS256 JWT of the App: header {"alg":"RS256","typ":"JWT"}, claims iat = Now - 60, exp = Now + 540, iss = AppId.
.PARAMETER Now
    Unix seconds; the clock of the token (a parameter so tests are deterministic).
#>
function New-GitHubAppJwt {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $AppId,
        [long] $Now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    )
    $pem = Get-GitHubAppPrivateKey
    if (-not $pem) {
        throw "GitHub App ${AppId}: no private key (set AISF_BOARD_APP_PRIVATE_KEY_PATH to a PEM file, or AISF_BOARD_APP_PRIVATE_KEY to the PEM text)."
    }
    $header = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"RS256","typ":"JWT"}'))
    $claims = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"iat":' + ($Now - 60) + ',"exp":' + ($Now + 540) + ',"iss":"' + $AppId + '"}'))
    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        try {
            $rsa.ImportFromPem($pem)
        }
        catch [System.ArgumentException], [System.Security.Cryptography.CryptographicException] {
            throw "GitHub App ${AppId}: the private key is not a valid RSA PEM key."
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
        throw "GitHub App ${AppId}: $Method $Path -> no response."
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
    Installation id; default AISF_BOARD_APP_INSTALLATION_ID, else discovered with GET /repos/{owner}/{repo}/installation.
#>
function Get-GitHubAppInstallationToken {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $AppId,
        [Parameter(Mandatory)] [string[]] $Repository,
        [Parameter(Mandatory)] [hashtable] $Permission,
        [string] $InstallationId = '',
        [long] $Now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    )
    $jwt = New-GitHubAppJwt -AppId $AppId -Now $Now
    if (-not $InstallationId) {
        $InstallationId = [string]$env:AISF_BOARD_APP_INSTALLATION_ID
    }
    if (-not $InstallationId) {
        $found = Invoke-GitHubAppApi 'Get' "/repos/$($Repository[0])/installation" $jwt '' $AppId
        if ($found.Status -ne 200 -or -not $found.Json -or -not $found.Json.ContainsKey('id')) {
            throw "GitHub App ${AppId}: no installation found for $($Repository[0]) (HTTP $($found.Status))."
        }
        $InstallationId = [string]$found.Json['id']
    }
    $names = @($Repository | ForEach-Object { ($_ -split '/')[-1] })
    $body = [ordered]@{ repositories = $names; permissions = $Permission } | ConvertTo-Json -Depth 5 -Compress
    $minted = Invoke-GitHubAppApi 'Post' "/app/installations/$InstallationId/access_tokens" $jwt $body $AppId
    if ($minted.Status -ne 201 -or -not $minted.Json -or -not $minted.Json.ContainsKey('token') -or -not $minted.Json['token']) {
        throw "GitHub App ${AppId}: no installation token (HTTP $($minted.Status))."
    }
    return [string]$minted.Json['token']
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
