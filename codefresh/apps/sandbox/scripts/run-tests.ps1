#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Step build_test of the sandbox pipelines: the unit tests of Sandbox.sln as TRX, then the TRX summary.

.DESCRIPTION
    Runs dotnet test over the Release build of the step's first command (--no-build), with the TRX files under
    ${ARTIFACTS_DIR}/tests, then trx-summary.ps1 over ${ARTIFACTS_DIR} into ${ARTIFACTS_DIR}/test-summary.md, also
    when a test failed. The result is that of dotnet test.

    Usage (Codefresh step build_test, in the application checkout):
      pwsh -NoProfile -File <env-repo>/codefresh/apps/sandbox/scripts/run-tests.ps1 -Title "sandbox/<pipeline> ${VERSION}"

.PARAMETER Title
    The title of the summary, for example "sandbox/ci 0.1.4-ci.abc1234".

.OUTPUTS
    The test log and the summary.

.NOTES
    Exit codes: 0 every test passed; 1 a test failed, dotnet test failed, or the summary failed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Title
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$artifacts = $env:ARTIFACTS_DIR

$PSNativeCommandUseErrorActionPreference = $false
try {
    dotnet test Sandbox.sln --configuration Release --no-build --logger 'trx;LogFilePrefix=sandbox' --results-directory "$artifacts/tests"
    $passed = $LASTEXITCODE -eq 0
}
catch {
    [Console]::Error.WriteLine("run-tests.ps1: $($_.Exception.Message)")
    $passed = $false
}
$PSNativeCommandUseErrorActionPreference = $true

& (Join-Path $PSScriptRoot 'trx-summary.ps1') -Path $artifacts -Out "$artifacts/test-summary.md" -Title $Title
if ($LASTEXITCODE -ne 0) {
    exit 1
}
if (-not $passed) {
    exit 1
}
exit 0
