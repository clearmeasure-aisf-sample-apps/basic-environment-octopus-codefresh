#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Step octopus_preflight of a release pipeline: checks context platform-octopus and the Octopus CLI, with no network
    call.

.DESCRIPTION
    Fails closed before anything reaches Octopus when context platform-octopus (OCTOPUS_URL, OCTOPUS_SPACE_ID,
    OCTOPUS_API_KEY) is missing, incomplete or still holds placeholders, or when OCTOPUS_URL is not https. Then runs
    octopus version. The key is never printed.

    Usage (Codefresh step octopus_preflight):
      pwsh -NoProfile -File <env-repo>/codefresh/apps/<app>/scripts/octopus-preflight.ps1

.OUTPUTS
    The CLI version; failures go to standard error.

.NOTES
    Exit codes: 0 ready; 1 failed (a missing or placeholder value, a URL other than https, no working octopus CLI).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Exit-Failure([string] $Message) {
    [Console]::Error.WriteLine("octopus_preflight: $Message")
    exit 1
}

# Empty, a placeholder in angle brackets, or the literal text of an unresolved Codefresh variable.
function Test-Missing([string] $Value) {
    if (-not $Value) {
        return $true
    }
    $open = $Value.IndexOf('<')
    if ($open -ge 0 -and $Value.IndexOf('>', $open + 1) -ge 0) {
        return $true
    }
    return $Value.Contains('${{')
}

foreach ($key in @('OCTOPUS_URL', 'OCTOPUS_SPACE_ID', 'OCTOPUS_API_KEY')) {
    if (Test-Missing ([Environment]::GetEnvironmentVariable($key))) {
        Exit-Failure "$key is missing from context platform-octopus"
    }
}
if (-not $env:OCTOPUS_URL.StartsWith('https://', [System.StringComparison]::Ordinal)) {
    Exit-Failure 'OCTOPUS_URL must be https'
}
octopus version
exit 0
