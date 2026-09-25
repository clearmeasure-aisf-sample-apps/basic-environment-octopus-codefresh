#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Smoke test of the platform/ci-dotnet image: step smoke of platform-env/ci-image-dotnet, run inside the image just
    built.

.DESCRIPTION
    Every tool of the image answers, in this order, each with its version on the log: dotnet (SDKs), pwsh, docker,
    cosign, syft, crane, gitleaks, helm, kustomize, kubeconform, terraform, yamllint, the PSScriptAnalyzer module,
    octopus, az, sqlcmd (found on PATH), jq, git, the Playwright Chromium and headless shell under
    PLAYWRIGHT_BROWSERS_PATH, and the crap4dotnet tool (DOTNET_ROLL_FORWARD=LatestMajor). The first tool that is
    missing or fails stops the smoke test.

    Environment: VERSION (the image tag, exported by step image_version); PLAYWRIGHT_BROWSERS_PATH (the image sets
    /ms-playwright).
    Exit codes: 0 every tool answered; 1 a tool is missing or failed (the message names it).

.PARAMETER Version
    The image tag for the closing line (default: VERSION).

.PARAMETER BrowsersPath
    Folder of the Playwright browsers (default: PLAYWRIGHT_BROWSERS_PATH, else /ms-playwright).

.EXAMPLE
    pwsh -NoProfile -File codefresh/platform/scripts/ci-dotnet-smoke.ps1
#>
[CmdletBinding()]
param(
    [string] $Version = $env:VERSION,

    [string] $BrowsersPath = $(if ($env:PLAYWRIGHT_BROWSERS_PATH) { $env:PLAYWRIGHT_BROWSERS_PATH } else { '/ms-playwright' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Runs one check; the first failure ends the smoke test with the tool's name.
function Invoke-Smoke([string] $Tool, [scriptblock] $Check) {
    try {
        & $Check
    }
    catch {
        Write-Host "ci-dotnet-smoke: FAIL $Tool ($($_.Exception.Message))"
        exit 1
    }
}

$browsers = $BrowsersPath
Invoke-Smoke 'dotnet' { & dotnet --list-sdks }
Invoke-Smoke 'pwsh' { $PSVersionTable.PSVersion.ToString() }
Invoke-Smoke 'docker' { & docker --version }
Invoke-Smoke 'cosign' { & cosign version }
Invoke-Smoke 'syft' { & syft version }
Invoke-Smoke 'crane' { & crane version }
Invoke-Smoke 'gitleaks' { & gitleaks version }
Invoke-Smoke 'helm' { & helm version --short }
Invoke-Smoke 'kustomize' { & kustomize version }
Invoke-Smoke 'kubeconform' { & kubeconform -v }
Invoke-Smoke 'terraform' { & terraform version }
Invoke-Smoke 'yamllint' { & yamllint --version }
Invoke-Smoke 'PSScriptAnalyzer' { Import-Module PSScriptAnalyzer; (Get-Module PSScriptAnalyzer).Version.ToString() }
Invoke-Smoke 'octopus' { & octopus version }
Invoke-Smoke 'az' { & az version --output none }
Invoke-Smoke 'sqlcmd' { (Get-Command -Name sqlcmd -CommandType Application | Select-Object -First 1).Source }
Invoke-Smoke 'jq' { & jq --version }
Invoke-Smoke 'git' { & git --version }
Invoke-Smoke 'Playwright Chromium' {
    foreach ($pattern in 'chromium-*', 'chromium_headless_shell-*') {
        $found = @(Get-Item -Path (Join-Path $browsers $pattern) -ErrorAction SilentlyContinue)
        if ($found.Count -eq 0) {
            throw "nothing matches $(Join-Path $browsers $pattern)"
        }
        $found.FullName | Sort-Object
    }
}
Invoke-Smoke 'dotnet-crap' {
    $rollForward = $env:DOTNET_ROLL_FORWARD
    $env:DOTNET_ROLL_FORWARD = 'LatestMajor'
    try {
        & dotnet-crap --help | Out-Null
    }
    finally {
        $env:DOTNET_ROLL_FORWARD = $rollForward
    }
}
Write-Host "Smoke passed for platform/ci-dotnet $Version; pin its tag and digest by commit (see the header)."
