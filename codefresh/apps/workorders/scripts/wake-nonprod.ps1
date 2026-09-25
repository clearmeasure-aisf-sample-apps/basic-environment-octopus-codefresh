#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Step wake_nonprod of a release pipeline: asks Octopus to run runbook env-wake of project platform-infrastructure in
    infra-nonprod, fire-and-forget (ADR-IR33).

.DESCRIPTION
    Runs in parallel with the gates when the build will release. Non-blocking three times over: the step has
    fail_fast false and no strict_fail_fast, and every path of this script exits 0. A missed wake costs only time:
    step 0 of the tdd deployment (Deploy a Release of platform-wake) wakes the cluster and waits. env-wake is
    idempotent.

    Credential: the Space Manager key of context platform-octopus (OCTOPUS_URL, OCTOPUS_SPACE_ID, OCTOPUS_API_KEY),
    sent by curl from a private header file so that it never appears in a process list or the log. The runbook is
    config as code, so the run route carries the Git ref (refs/heads/main) of the Octopus project [VERIFY: Octopus's
    RunConfigAsCodeRunbook.ps1 example is marked early access].

    Usage (Codefresh step wake_nonprod):
      pwsh -NoProfile -File <env-repo>/codefresh/apps/<app>/scripts/wake-nonprod.ps1

.OUTPUTS
    The queued task IDs in the log. Warnings go to standard error.

.NOTES
    Exit code: 0 always; a problem is a warning.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$project = 'platform-infrastructure'
$environment = 'infra-nonprod'

function Exit-Skip([string] $Reason) {
    [Console]::Error.WriteLine("wake_nonprod: WARNING: $Reason. Not waking now; step 0 of the tdd deployment wakes the cluster.")
    exit 0
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

# One request with the key from the header file: the response body, or $null when curl fails.
function Invoke-OctopusApi([string] $Headers, [string[]] $Arguments) {
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        $output = curl -fsS --max-time 30 -H "@$Headers" @Arguments
    }
    catch {
        return $null
    }
    if ($LASTEXITCODE -ne 0) {
        return $null
    }
    return (@($output) -join "`n")
}

# The Id of the first element of a JSON list or object whose field (one of $Fields) equals $Wanted, as
# jq -r 'first(.[] | select(...) | .Id) // empty' prints it. Skips with $Failure when the text is no such JSON, with
# $Missing when no element matches or the Id is empty.
function Find-Id([string] $Json, [string[]] $Fields, [string] $Wanted, [string] $Failure, [string] $Missing) {
    if ([string]::IsNullOrWhiteSpace($Json)) {
        Exit-Skip $Missing
    }
    try {
        $document = ConvertFrom-Json -InputObject $Json -AsHashtable -NoEnumerate
    }
    catch {
        Exit-Skip $Failure
    }
    if ($document -is [System.Collections.IDictionary]) {
        $elements = @($document.Values)
    }
    elseif ($document -is [System.Collections.IList]) {
        $elements = @($document)
    }
    else {
        Exit-Skip $Failure
    }
    foreach ($element in $elements) {
        if ($null -eq $element) {
            continue
        }
        if ($element -isnot [System.Collections.IDictionary]) {
            Exit-Skip $Failure
        }
        $matched = @($Fields | Where-Object { $element[$_] -is [string] -and $element[$_] -ceq $Wanted }).Count -gt 0
        if (-not $matched) {
            continue
        }
        $id = $element['Id']
        if ($null -eq $id -or ($id -is [bool] -and -not $id)) {
            Exit-Skip $Missing
        }
        $text = if ($id -is [string]) { $id } else { ConvertTo-Json -InputObject $id -Depth 20 -Compress }
        $text = $text.TrimEnd("`n")
        if (-not $text) {
            Exit-Skip $Missing
        }
        return $text
    }
    Exit-Skip $Missing
}

# Every TaskId in the run response (jq -c '[.. | objects | (.TaskId // empty)] | unique'): true, numbers, strings,
# then arrays and objects, each kind sorted and without duplicates.
function Get-TaskList([string] $Response) {
    if ([string]::IsNullOrWhiteSpace($Response)) {
        return ''
    }
    try {
        $document = ConvertFrom-Json -InputObject $Response -AsHashtable -NoEnumerate
    }
    catch {
        return '(response not parsed)'
    }
    $found = [System.Collections.Generic.List[object]]::new()
    $pending = [System.Collections.Generic.Stack[object]]::new()
    $pending.Push($document)
    while ($pending.Count -gt 0) {
        $node = $pending.Pop()
        if ($node -is [System.Collections.IDictionary]) {
            $task = $node['TaskId']
            if ($null -ne $task -and -not ($task -is [bool] -and -not $task)) {
                $found.Add($task)
            }
            foreach ($value in $node.Values) {
                $pending.Push($value)
            }
        }
        elseif ($node -is [System.Collections.IList]) {
            foreach ($value in $node) {
                $pending.Push($value)
            }
        }
    }
    $flags = @($found | Where-Object { $_ -is [bool] } | Select-Object -First 1)
    $numbers = @($found | Where-Object { $_ -isnot [bool] -and $_ -isnot [System.Collections.IEnumerable] } | Sort-Object -Unique)
    $texts = [string[]] @($found | Where-Object { $_ -is [string] } | Select-Object -Unique)
    [System.Array]::Sort($texts, [System.StringComparer]::Ordinal)
    $structures = @($found | Where-Object { $_ -isnot [string] -and $_ -is [System.Collections.IEnumerable] } |
            Sort-Object -Property { ConvertTo-Json -InputObject $_ -Depth 20 -Compress } -Unique)
    return ConvertTo-Json -InputObject @($flags + $numbers + $texts + $structures) -Depth 20 -Compress
}

foreach ($key in @('OCTOPUS_URL', 'OCTOPUS_SPACE_ID', 'OCTOPUS_API_KEY')) {
    if (Test-Missing ([Environment]::GetEnvironmentVariable($key))) {
        Exit-Skip "$key is missing from context platform-octopus"
    }
}
$base = ($env:OCTOPUS_URL -replace '/\z', '') + '/api'
$space = $env:OCTOPUS_SPACE_ID

$headers = $null
try {
    try {
        # Mode 0600 from the start, before the key is written.
        $headers = [System.IO.Path]::GetTempFileName()
    }
    catch {
        Exit-Skip 'mktemp failed'
    }
    [System.IO.File]::WriteAllText($headers, "X-Octopus-ApiKey: $($env:OCTOPUS_API_KEY)`nContent-Type: application/json`n", [System.Text.UTF8Encoding]::new($false))

    $projects = Invoke-OctopusApi $headers @("$base/$space/projects/all")
    if ($null -eq $projects) {
        Exit-Skip 'the project lookup failed'
    }
    $projectId = Find-Id $projects @('Slug', 'Name') $project 'the project lookup failed' "project $project not found"

    $environments = Invoke-OctopusApi $headers @("$base/$space/environments/all")
    if ($null -eq $environments) {
        Exit-Skip 'the environment lookup failed'
    }
    $environmentId = Find-Id $environments @('Name') $environment 'the environment lookup failed' "environment $environment not found"

    $run = [ordered]@{ EnvironmentId = $environmentId; TenantId = $null; SkipActions = @(); SpecificMachineIds = @(); ExcludedMachineIds = @() }
    $body = ConvertTo-Json -InputObject ([ordered]@{ SelectedPackages = @(); SelectedGitResources = @(); Runs = @($run) }) -Depth 20 -Compress
    $runUrl = "$base/spaces/$space/projects/$projectId/refs%2Fheads%2Fmain/runbooks/env-wake/run/v1"
    $response = Invoke-OctopusApi $headers @('-X', 'POST', '--data-binary', $body, $runUrl)
    if ($null -eq $response) {
        Exit-Skip 'the env-wake run request failed'
    }
    Write-Host "wake_nonprod: env-wake queued in $environment, tasks $(Get-TaskList $response); the build does not wait for it."
}
catch {
    Exit-Skip "unexpected error: $($_.Exception.Message)"
}
finally {
    if ($headers) {
        Remove-Item -LiteralPath $headers -Force -ErrorAction SilentlyContinue
    }
}
exit 0
