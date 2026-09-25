#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    platform-pin-writer: the fallback pin writer (ADR-IR34 decision 20; CAP-OCT-012 runs against whichever writer is
    active).

.DESCRIPTION
    An app switches to it by pull request only if the Preview step "Update Argo CD Application Image Tags" breaks after
    an Octopus Cloud upgrade: that pull request replaces the image-tag step with a step of this template (or an inline
    copy) and gives the step the sensitive variable PinWriter.GitToken.

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
    Commits are authored by octopus-argocd-pin-bot, the identity of the bot-path audit (PLATFORM_BOT_AUTHORS).
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

.PARAMETER Branch
    Branch Argo CD reads; empty means main (PinWriter.Branch).

.PARAMETER TimeoutSeconds
    Longest wait for Argo CD; empty means 900 (PinWriter.TimeoutSeconds).

.OUTPUTS
    Octopus output variable PinWriter.Commit. Sensitive Octopus variable PinWriter.GitToken: a token with contents
    write on the environment repository. Invalid input, a missing images[] entry, three failed pushes or the Argo CD
    timeout fail the step (Fail-Step); a failing git or kubectl call stops it.
#>
[CmdletBinding()]
param(
    [string]$App = $OctopusParameters['PinWriter.App'],
    [string]$Deployable = $OctopusParameters['PinWriter.Deployable'],
    [string]$Environment = $OctopusParameters['PinWriter.Environment'],
    [string]$Images = $OctopusParameters['PinWriter.Images'],
    [string]$RepoUrl = $OctopusParameters['PinWriter.RepoUrl'],
    [string]$Branch = $OctopusParameters['PinWriter.Branch'],
    [string]$TimeoutSeconds = $OctopusParameters['PinWriter.TimeoutSeconds']
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandArgumentPassing = 'Standard'
$PSNativeCommandUseErrorActionPreference = $true

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
$token = [string]$OctopusParameters['PinWriter.GitToken']
if (-not $token) {
    Fail-Step 'The sensitive variable PinWriter.GitToken is empty; the pull request that switches on the fallback writer must provide it.'
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
