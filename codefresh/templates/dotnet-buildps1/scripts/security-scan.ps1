#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Step security_scan (advisory gate): Gitleaks over the checkout and the NuGet vulnerability and deprecation reports.

.DESCRIPTION
    Runs in the gate's worktree of the application checkout (${CF_VOLUME_PATH}/wt/security) and writes to the output
    folder:
      gitleaks.sarif             gitleaks dir . with the application repo's .gitleaks.toml, secrets redacted
      vulnerability-report.txt   dotnet list src/ChurchBulletin.sln package --vulnerable --include-transitive
      deprecated-report.txt      dotnet list src/ChurchBulletin.sln package --deprecated (a warning only)
    Both reports are printed. The step fails when Gitleaks finds a leak or fails, when the vulnerability listing fails
    or names a vulnerable package, and when the restore fails; gate.ps1 counts it as advisory (-Advisory security_scan).

    Usage (Codefresh step security_scan):
      pwsh -NoProfile -File <env-repo>/codefresh/apps/<app>/scripts/security-scan.ps1 [-Out <dir>]

.PARAMETER Out
    The report folder (default: ${ARTIFACTS_DIR}/security).

.OUTPUTS
    The reports, and the tools' own output.

.NOTES
    Exit codes: 0 clean; 1 a leak, a vulnerable package or a failing tool.
#>
[CmdletBinding()]
param(
    [string] $Out = "$($env:ARTIFACTS_DIR)/security"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$solution = 'src/ChurchBulletin.sln'

# Runs a tool and passes its output through; $true when it exits 0 (a missing tool fails, as in a shell).
function Invoke-Tool([string] $Name, [string[]] $Arguments) {
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        & $Name @Arguments | Out-Host
    }
    catch {
        [Console]::Error.WriteLine("security-scan.ps1: $($_.Exception.Message)")
        return $false
    }
    return $LASTEXITCODE -eq 0
}

# Runs dotnet with standard output and error written to one report file (as >file 2>&1), then prints the file;
# $true when dotnet exits 0.
function Invoke-Report([string] $Path, [string[]] $Arguments) {
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        $lines = @(& dotnet @Arguments 2>&1 | ForEach-Object { "$_" })
        $succeeded = $LASTEXITCODE -eq 0
    }
    catch {
        $lines = @($_.Exception.Message)
        $succeeded = $false
    }
    $text = -join ($lines | ForEach-Object { "$_`n" })
    [System.IO.File]::WriteAllText($Path, $text, [System.Text.UTF8Encoding]::new($false))
    [Console]::Out.Write($text)
    [Console]::Out.Flush()
    return $succeeded
}

$null = New-Item -ItemType Directory -Force -Path $Out
$failed = $false

if (-not (Invoke-Tool 'gitleaks' @('dir', '.', '--config', '.gitleaks.toml', '--redact', '--no-banner', '--report-format', 'sarif', '--report-path', "$Out/gitleaks.sarif"))) {
    $failed = $true
}
dotnet restore $solution

$vulnerabilities = Join-Path $Out 'vulnerability-report.txt'
if (-not (Invoke-Report $vulnerabilities @('list', $solution, 'package', '--vulnerable', '--include-transitive'))) {
    $failed = $true
}
if ([System.IO.File]::ReadAllText($vulnerabilities).Contains('has the following vulnerable packages')) {
    $failed = $true
}

$deprecations = Join-Path $Out 'deprecated-report.txt'
$null = Invoke-Report $deprecations @('list', $solution, 'package', '--deprecated')
if ([System.IO.File]::ReadAllText($deprecations).Contains('has the following deprecated packages')) {
    Write-Host 'WARNING: deprecated NuGet packages'
}

if ($failed) {
    exit 1
}
exit 0
