#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    platform-pin-writer: the fallback pin writer (ADR-IR34 decision 20; CAP-OCT-012 runs against whichever writer is
    active).

.DESCRIPTION
    An app switches to it by pull request (stage 2 of #58; earlier, only if the Preview step "Update Argo CD Application
    Image Tags" breaks after an Octopus Cloud upgrade): that pull request replaces the image-tag step with a step of this
    template (or an inline copy) and gives the step the GitHub App inputs PinWriter.AppId, PinWriter.InstallationId and
    the sensitive variable PinWriter.AppPrivateKey. There is no personal access token and no fallback to one.

    octopus/terraform creates step template platform-pin-writer from this file; the parameters default to the template
    parameters PinWriter.*. An inline copy sits between "# >>> octopus/step-templates/pin-writer.ps1" and
    "# <<< octopus/step-templates/pin-writer.ps1", inside a script block that the step calls with its inputs (offline
    drift test). No dollar-brace or percent-brace sequences (OCL heredoc template syntax) and no hash-brace sequences
    (Octopus variable substitution).

    What it does, like the Preview step: commits images[].newTag (tags only; a digest next to the tag is removed, V3) in
    gitops/apps/<app>/envs/<env>/<deployable>/kustomization.yaml on the default branch, as the pin bot, then waits
    until Argo CD reports Application <app>-<deployable>-<env> Synced at that commit and Healthy. Kustomize
    deployables only; Helm and raw deployables keep the Preview step.
    Where it runs: k8s-<env> (variable Platform.WorkerPool). The wait reads applications.argoproj.io in namespace
    argocd as service account octopus-worker-<env>-scripts (Role octopus-worker-application-reader,
    argocd/clusters/<tier>/octopus-workers-rbac.yaml).
    Commits are authored by octopus-argocd-pin-bot, the identity of the bot-path audit (PLATFORM_BOT_AUTHORS); the
    GitHub-side actor is the GitHub App aisf-pin-writer.
    Credential: the step mints a one-hour installation token of the GitHub App aisf-pin-writer per run (RS256 JWT of the
    private key, exchanged at the installation access-token endpoint of the API, narrowed to this repository with contents write and metadata
    read), by the inline copy of New-GitHubAppJwt and Get-GitHubAppInstallationToken between "# >>> scripts/github/GitHubAppAuth.ps1"
    and "# <<< scripts/github/GitHubAppAuth.ps1" (a step runs on a worker without a checkout; an offline drift test keeps
    the copy equal to the library). The key stays in memory (never in the environment, a file, an argument or the log). An
    unset input or a refused or unanswered exchange fails the step before any commit, naming the variable or the HTTP
    status only.
    The token reaches git through the environment of a two-line POSIX sh askpass helper (git starts it for every
    credential prompt), never through an argument or the remote URL.

.PARAMETER App
    App slug (PinWriter.App).

.PARAMETER Deployable
    Deployable name, for example app (PinWriter.Deployable).

.PARAMETER Environment
    tdd, uat or prod (PinWriter.Environment).

.PARAMETER Images
    image=tag pairs, separated by commas or new lines; images are names under apps/<app>/ (PinWriter.Images).

.PARAMETER RepoUrl
    Environment repository; empty means the platform's environment repository (PinWriter.RepoUrl).

.PARAMETER AppId
    Id of the GitHub App aisf-pin-writer (PinWriter.AppId).

.PARAMETER InstallationId
    Installation id of the App on the environment repository (PinWriter.InstallationId).

.PARAMETER Branch
    Branch Argo CD reads; empty means main (PinWriter.Branch).

.PARAMETER TimeoutSeconds
    Longest wait for Argo CD; empty means 900 (PinWriter.TimeoutSeconds).

.OUTPUTS
    Octopus output variable PinWriter.Commit. Sensitive Octopus variable PinWriter.AppPrivateKey: the PEM private key
    of the GitHub App aisf-pin-writer (Contents read and write, Metadata read; installed on the environment repository
    only). Invalid input, an unset App input, a refused token exchange, a missing images[] entry, three failed pushes or
    the Argo CD timeout fail the step (Fail-Step); a failing git or kubectl call stops it.
#>
[CmdletBinding()]
param(
    [string]$App = $OctopusParameters['PinWriter.App'],
    [string]$Deployable = $OctopusParameters['PinWriter.Deployable'],
    [string]$Environment = $OctopusParameters['PinWriter.Environment'],
    [string]$Images = $OctopusParameters['PinWriter.Images'],
    [string]$RepoUrl = $OctopusParameters['PinWriter.RepoUrl'],
    [string]$AppId = $OctopusParameters['PinWriter.AppId'],
    [string]$InstallationId = $OctopusParameters['PinWriter.InstallationId'],
    [string]$Branch = $OctopusParameters['PinWriter.Branch'],
    [string]$TimeoutSeconds = $OctopusParameters['PinWriter.TimeoutSeconds']
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandArgumentPassing = 'Standard'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

if (-not $RepoUrl) {
    $RepoUrl = 'https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh.git'
}
if (-not $Branch) {
    $Branch = 'main'
}
if (-not $TimeoutSeconds) {
    $TimeoutSeconds = '900'
}
if ($App -cnotmatch '^[a-z][a-z0-9]{2,11}$') {
    Fail-Step "PinWriter.App (-App) '$App' is not an app slug."
}
if ($Deployable -cnotmatch '^[a-z][a-z0-9]{1,11}$') {
    Fail-Step "PinWriter.Deployable (-Deployable) '$Deployable' is not a deployable name."
}
if ($Environment -cnotin 'tdd', 'uat', 'prod') {
    Fail-Step "PinWriter.Environment (-Environment) must be tdd, uat or prod, not '$Environment'."
}
if ($TimeoutSeconds -cnotmatch '\A[0-9]+\z') {
    Fail-Step "PinWriter.TimeoutSeconds (-TimeoutSeconds) must be a whole number of seconds, not '$TimeoutSeconds'."
}
$key = [string]$OctopusParameters['PinWriter.AppPrivateKey']
$unset = @()
if (-not $AppId) { $unset += 'PinWriter.AppId' }
if (-not $InstallationId) { $unset += 'PinWriter.InstallationId' }
if (-not $key) { $unset += 'PinWriter.AppPrivateKey' }
if ($unset.Count -gt 0) {
    Fail-Step "The GitHub App inputs are not set: $($unset -join ', '). The pull request that switches on the pin writer must provide them (docs/runbooks/octopus-github-app-git.md); there is no fallback credential."
}
if ($AppId -cnotmatch '\A[0-9]{1,20}\z') {
    Fail-Step 'PinWriter.AppId (-AppId) is not a GitHub App id.'
}
if ($InstallationId -cnotmatch '\A[0-9]{1,20}\z') {
    Fail-Step 'PinWriter.InstallationId (-InstallationId) is not a GitHub App installation id.'
}
foreach ($tool in 'git', 'kubectl') {
    if (-not (Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue)) {
        Fail-Step "$tool is missing from the step container."
    }
}

# Rewrites newTag of the images[] entries named <registry>/apps/<app>/<image> and drops their digest lines, line by
# line as the awk program of the Bash writer did. Returns updated, no-entry or no-newtag; only updated writes the file.
function Update-PinTag([string]$Path, [string]$Image, [string]$Tag) {
    $blank = '[ \t\r\n\f\v]'
    $suffix = "apps/$App/$Image"
    $content = [IO.File]::ReadAllText($Path)
    if ($content.EndsWith("`n")) {
        $content = $content.Substring(0, $content.Length - 1)
    }
    $lines = if ($content.Length -gt 0) { $content -split "`n" } else { @() }
    $output = [Collections.Generic.List[string]]::new()
    $inItem = $false
    $matched = $false
    $updated = $false
    foreach ($line in $lines) {
        if ($line -cmatch "^$blank*-$blank+name:") {
            $value = ($line -creplace "^$blank*-$blank+name:$blank*", '') -creplace '["\t\n\v\f\r ]', ''
            $inItem = ($value -ceq $suffix) -or $value.EndsWith("/$suffix", [StringComparison]::Ordinal)
            if ($inItem) {
                $matched = $true
            }
            $output.Add($line)
            continue
        }
        if ($inItem -and ($line -cmatch "^$blank*-$blank" -or $line -cmatch '^[^ \t\r\n\f\v]')) {
            $inItem = $false
        }
        if ($inItem -and $line -cmatch "^$blank*newTag:") {
            $line = $line.Substring(0, $line.IndexOf('newTag:', [StringComparison]::Ordinal)) + "newTag: `"$Tag`""
            $updated = $true
        }
        if ($inItem -and $line -cmatch "^$blank*digest:") {
            continue
        }
        $output.Add($line)
    }
    if (-not $matched) {
        return 'no-entry'
    }
    if (-not $updated) {
        return 'no-newtag'
    }
    [IO.File]::WriteAllText($Path, -join ($output | ForEach-Object { "$_`n" }))
    return 'updated'
}

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

# The installation token of the GitHub App aisf-pin-writer, minted per run and narrowed to this repository with contents
# write and metadata read. The key stays in memory; a failure names the App id and the HTTP status only.
$tokenRepository = 'clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh'
try {
    $token = Get-GitHubAppInstallationToken -AppId $AppId -Repository @($tokenRepository) -Permission @{ contents = 'write'; metadata = 'read' } -InstallationId $InstallationId -PrivateKey $key
}
catch [System.Management.Automation.RuntimeException] {
    $message = $_.Exception.Message
    $reason = if ($message -match 'HTTP (\d{3})') { "HTTP $($Matches[1])" } elseif ($message -match 'no response') { 'no response' } elseif ($message -match 'private key') { 'the private key is not usable' } else { 'refused' }
    Fail-Step "No installation token for GitHub App $AppId ($reason); nothing was committed. There is no fallback credential."
}
finally {
    $key = $null
}

$work = [IO.Directory]::CreateTempSubdirectory('pin-writer-').FullName
try {
    $askpass = Join-Path $work 'askpass.sh'
    $helper = @(
        '#!/bin/sh'
        'case "$1" in Username*) echo x-access-token ;; *) printf "%s\n" "$PINWRITER_TOKEN" ;; esac'
    )
    [IO.File]::WriteAllText($askpass, -join ($helper | ForEach-Object { "$_`n" }))
    [IO.File]::SetUnixFileMode($askpass, [IO.UnixFileMode]'UserRead, UserWrite, UserExecute')
    $env:PINWRITER_TOKEN = $token
    $env:GIT_ASKPASS = $askpass
    $env:GIT_TERMINAL_PROMPT = '0'
    $clone = Join-Path $work 'repo'
    git clone --quiet --depth 50 --branch $Branch $RepoUrl $clone
    git -C $clone config user.name 'octopus-argocd-pin-bot'
    git -C $clone config user.email 'octopus-argocd-pin-bot@users.noreply.github.com'

    $file = "gitops/apps/$App/envs/$Environment/$Deployable/kustomization.yaml"
    $path = Join-Path $clone $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail-Step "$file does not exist: the fallback writer handles Kustomize deployables only."
    }

    $summary = ''
    foreach ($entry in $Images -split '[,\n]') {
        $pair = $entry -creplace '[ \t\r\n\f\v]', ''
        if (-not $pair) {
            continue
        }
        $separator = $pair.IndexOf('=')
        $image = if ($separator -ge 0) { $pair.Substring(0, $separator) } else { $pair }
        $tag = if ($separator -ge 0) { $pair.Substring($separator + 1) } else { '' }
        if ($image -cnotmatch '^[a-z][a-z0-9-]{0,38}[a-z0-9]$') {
            Fail-Step "Image name '$image' is invalid (PinWriter.Images, -Images)."
        }
        if ($tag -cnotmatch '^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$') {
            Fail-Step "Tag '$tag' of $image is invalid (PinWriter.Images, -Images)."
        }
        switch (Update-PinTag $path $image $tag) {
            'updated' { $summary += " $image=$tag" }
            'no-entry' { Fail-Step "$file has no images[] entry for apps/$App/$image." }
            default { Fail-Step "The images[] entry for apps/$App/$image in $file has no newTag line." }
        }
    }
    if (-not $summary) {
        Fail-Step 'PinWriter.Images (-Images) names no image.'
    }

    $PSNativeCommandUseErrorActionPreference = $false
    git -C $clone diff --quiet -- $file
    $unchanged = $LASTEXITCODE -eq 0
    $PSNativeCommandUseErrorActionPreference = $true
    if ($unchanged) {
        Write-Host "$file already pins$summary; nothing to commit."
    }
    else {
        git -C $clone add -- $file
        git -C $clone commit --quiet -m "pin($App/$Deployable/$Environment):$summary" -m "Octopus release $($OctopusParameters['Octopus.Release.Number']), platform-pin-writer (ADR-IR34 decision 20)."
        $attempt = 1
        while ($true) {
            $PSNativeCommandUseErrorActionPreference = $false
            git -C $clone push --quiet origin "HEAD:$Branch"
            $pushed = $LASTEXITCODE -eq 0
            $PSNativeCommandUseErrorActionPreference = $true
            if ($pushed) {
                break
            }
            if ($attempt -ge 3) {
                Fail-Step "Pushing the pin commit to $Branch failed three times."
            }
            $attempt++
            Start-Sleep -Seconds 5
            git -C $clone pull --quiet --rebase origin $Branch
        }
    }
    $commit = (git -C $clone rev-parse HEAD) -join "`n"
    Set-OctopusVariable -name 'PinWriter.Commit' -value $commit
    Write-Host "Pinned$summary in $file at $commit."

    $application = "$App-$Deployable-$Environment"
    $deadline = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() + [long]$TimeoutSeconds
    while ($true) {
        $PSNativeCommandUseErrorActionPreference = $false
        $state = (kubectl --namespace argocd get application $application --output 'jsonpath={.status.sync.revision} {.status.sync.status} {.status.health.status}' 2>$null) -join "`n"
        if ($LASTEXITCODE -ne 0) {
            $state = ''
        }
        $PSNativeCommandUseErrorActionPreference = $true
        $fields = @(($state -split "`n", 2)[0].Trim([char[]]" `t") -split '[ \t]+', 3) + @('', '', '')
        if ($fields[0] -ceq $commit -and $fields[1] -ceq 'Synced' -and $fields[2] -ceq 'Healthy') {
            break
        }
        if ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() -ge $deadline) {
            Fail-Step "Argo CD Application $application did not reach Synced and Healthy at $commit within $TimeoutSeconds seconds (last: '$state')."
        }
        Start-Sleep -Seconds 15
    }
    Write-Highlight "Argo CD Application $application is Synced at $commit and Healthy."
}
finally {
    Remove-Item -Path Env:PINWRITER_TOKEN, Env:GIT_ASKPASS, Env:GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
