#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Runs the .NET conformance suite and summarises it: step run of platform-env/conformance and
    platform-env/conformance-destructive.

.DESCRIPTION
    ADR-IR34 "Test harness". In order:
      1. exports CONFORMANCE_RESULTS_DIR (the results folder, for the publish and teardown steps), creates the
         folder and, with -KeepResults, deletes the older results folders next to it (the build volume);
      2. mints PLATFORM_RUN_ID as conformance-arm.ps1 does when it is absent, and exports it;
      3. records the power state of both app clusters in <results>/power-before.txt (aks-power.ps1);
         conformance-teardown.ps1 force-sleeps the ones that were not Running (the clusters the run woke);
      4. dotnet build, then dotnet test of tests/Platform.Conformance.sln with TEST_FILTER and the TRX logger (no
         JUnit), with a heartbeat line every 5 minutes (Codefresh ends a build whose log stays silent for 45);
      5. Platform.Conformance.Report writes summary.md (printed to the log) and summary.json;
      6. records Codefresh build annotations: per-verdict counts and failed capability IDs (best effort).
    Run from the environment repository root.

    Environment: TEST_FILTER (required); PLATFORM_RUN_ID; the harness's secrets (OCTOPUS_API_KEY, AZURE_CLIENT_ID,
    AZURE_CLIENT_SECRET, AZURE_TENANT_ID, GITHUB_TOKEN, CODEFRESH_API_KEY); CF_API_KEY, CF_BUILD_ID, CF_URL,
    CF_PIPELINE_NAME, CF_VOLUME_PATH (Codefresh). No secret is printed or put on a command line.

    Exit code: that of dotnet test (a failed test fails the build); 1 when dotnet build fails; 2 when TEST_FILTER is
    missing.

.PARAMETER ResultsDirectory
    Results folder: TRX files, summaries, power-before.txt and the harness artifacts.

.PARAMETER KeepResults
    Keeps the newest KeepResults folders next to the results folder, this one included, and deletes the older ones;
    only folders named like a Codefresh build ID (24 hex digits) are deleted. The pipelines pass 10 (the build
    volume); 0 (default) deletes nothing.

.EXAMPLE
    pwsh -NoProfile -File codefresh/platform/scripts/conformance-run.ps1 -ResultsDirectory "$CF_VOLUME_PATH/conformance/$CF_BUILD_ID" -KeepResults 10
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string] $ResultsDirectory,

    [ValidateRange(0, 10000)]
    [int] $KeepResults = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. (Join-Path $PSScriptRoot 'aks-power.ps1')

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("conformance-run: $Message")
}

# A variable for the later steps: cf_export NAME (value from the environment) when Codefresh provides the command,
# else a NAME=value line in $CF_VOLUME_PATH/env_vars_to_export. Never for secrets.
function Export-BuildVariable([string] $Name, [string] $Value) {
    [Environment]::SetEnvironmentVariable($Name, $Value)
    $cfExport = Get-Command -Name cf_export -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cfExport) {
        & $cfExport.Source $Name
        Write-Note "exported $Name through cf_export"
    }
    elseif ($env:CF_VOLUME_PATH) {
        [System.IO.File]::AppendAllText((Join-Path $env:CF_VOLUME_PATH 'env_vars_to_export'), "$Name=$Value`n")
        Write-Note "exported $Name through `$CF_VOLUME_PATH/env_vars_to_export"
    }
    else {
        Write-Note "$Name not exported: no cf_export and no CF_VOLUME_PATH (not a Codefresh build)"
    }
}

# A new file readable only by its owner (mode 0600 outside Windows).
function Write-PrivateFile([string] $Path, [string] $Content) {
    $options = [System.IO.FileStreamOptions]::new()
    $options.Mode = [System.IO.FileMode]::CreateNew
    $options.Access = [System.IO.FileAccess]::Write
    if (-not $IsWindows) {
        $options.UnixCreateMode = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite
    }
    $stream = [System.IO.FileStream]::new($Path, $options)
    try {
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Content)
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally {
        $stream.Dispose()
    }
}

# The run ID of conformance-arm.ps1: lower case, bytes outside [a-z0-9.-] as '-', at most 40 characters.
function ConvertTo-RunId([string] $Value) {
    $characters = foreach ($byte in [System.Text.Encoding]::UTF8.GetBytes($Value)) {
        $character = if ($byte -ge 0x41 -and $byte -le 0x5A) { [char]($byte + 0x20) } else { [char]$byte }
        if ($character -cmatch '^[a-z0-9.-]$') { $character } else { '-' }
    }
    $text = -join $characters
    return $(if ($text.Length -gt 40) { $text.Substring(0, 40) } else { $text })
}

# jq's tostring: a string as it is, null as "null", anything else as compact JSON.
function ConvertTo-JqString([object] $Value) {
    if ($null -eq $Value) {
        return 'null'
    }
    if ($Value -is [string]) {
        return $Value
    }
    return ConvertTo-Json -InputObject $Value -Depth 100 -Compress
}

# jq '.name': $null for a missing key or a null node; an error for a node that is not an object.
function Get-JsonProperty([object] $Node, [string] $Name) {
    if ($null -eq $Node) {
        return $null
    }
    if ($Node -isnot [System.Collections.IDictionary]) {
        throw "cannot read '$Name' of a JSON value that is not an object"
    }
    return , $Node[$Name]
}

# The five annotations of summary.json, as compact JSON lines; throws where jq fails.
function Get-Annotation([string] $SummaryFile, [string] $BuildId, [string] $RunId) {
    $summary = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($SummaryFile)) -AsHashtable -NoEnumerate -Depth 100
    $totals = Get-JsonProperty $summary 'totals'
    $capabilities = Get-JsonProperty $summary 'capabilities'
    if ($capabilities -is [System.Collections.IDictionary]) {
        $capabilities = @($capabilities.Values)
    }
    elseif ($null -eq $capabilities -or $capabilities -is [string] -or $capabilities -isnot [System.Collections.IEnumerable]) {
        throw 'cannot iterate over .capabilities'
    }
    $failed = foreach ($capability in $capabilities) {
        if ((Get-JsonProperty $capability 'status') -is [string] -and (Get-JsonProperty $capability 'status') -ceq 'fail') {
            $id = Get-JsonProperty $capability 'id'
            if ($null -eq $id) { '' } elseif ($id -is [string]) { $id } else { ConvertTo-Json -InputObject $id -Compress }
        }
    }
    $pairs = [ordered]@{
        'conformance-run-id'              = $RunId
        'conformance-passed'              = ConvertTo-JqString (Get-JsonProperty $totals 'passed')
        'conformance-failed'              = ConvertTo-JqString (Get-JsonProperty $totals 'failed')
        'conformance-inconclusive'        = ConvertTo-JqString (Get-JsonProperty $totals 'inconclusive')
        'conformance-failed-capabilities' = @($failed) -join ' '
    }
    foreach ($key in $pairs.Keys) {
        ConvertTo-Json -Compress -InputObject ([ordered]@{ entityType = 'build'; entityId = $BuildId; key = $key; value = $pairs[$key] })
    }
}

# 1. The results folder of the build, for this step and the publish and teardown steps. The folder is passed on as
# given; .NET file calls get its full path.
Export-BuildVariable 'CONFORMANCE_RESULTS_DIR' $ResultsDirectory
$resultsPath = [System.IO.Path]::GetFullPath($ResultsDirectory, (Get-Location).ProviderPath)
New-Item -ItemType Directory -Path $resultsPath -Force | Out-Null
if ($KeepResults -gt 0) {
    $parent = Split-Path -Parent $resultsPath
    Get-ChildItem -LiteralPath $parent -Directory |
        Sort-Object -Property @{ Expression = 'LastWriteTimeUtc'; Descending = $true }, @{ Expression = 'Name'; Descending = $false } |
        Select-Object -Skip $KeepResults |
        Where-Object { $_.Name -cmatch '^[0-9a-f]{24}$' } |
        ForEach-Object {
            Write-Note "deleting the older results folder $($_.FullName)"
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
}

$filter = $env:TEST_FILTER
if (-not $filter) {
    Write-Note 'TEST_FILTER is not set (a variable of the pipeline spec)'
    exit 2
}

# 2. The run ID.
if (-not $env:PLATFORM_RUN_ID) {
    $build = if ($env:CF_BUILD_ID) { $env:CF_BUILD_ID } else { 'local' }
    $suffix = if ($build.Length -ge 8) { $build.Substring($build.Length - 8) } else { '' }
    $stamp = [DateTime]::UtcNow.ToString("yyyyMMdd't'HHmm", [System.Globalization.CultureInfo]::InvariantCulture)
    $env:PLATFORM_RUN_ID = ConvertTo-RunId "r$stamp-$suffix"
}
Export-BuildVariable 'PLATFORM_RUN_ID' $env:PLATFORM_RUN_ID
Write-Note "run $($env:PLATFORM_RUN_ID), filter $filter"

# 3. Power state before the tests.
$powerFile = Join-Path $resultsPath 'power-before.txt'
[System.IO.File]::WriteAllText($powerFile, '')
$powerDirectory = [System.IO.Directory]::CreateTempSubdirectory('conformance-run-').FullName
try {
    if (Initialize-AksPower -Directory $powerDirectory) {
        foreach ($tier in 'nonprod', 'prod') {
            [System.IO.File]::AppendAllText($powerFile, "$tier=$(Get-AksPowerState -Tier $tier)`n")
        }
    }
}
finally {
    Remove-Item -LiteralPath $powerDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
$env:PLATFORM_ARTIFACTS_DIR = if ($env:PLATFORM_ARTIFACTS_DIR) { $env:PLATFORM_ARTIFACTS_DIR } else { "$ResultsDirectory/artifacts" }
# Q49: the build's own Codefresh key serves the harness unless the context supplies one.
$env:CODEFRESH_API_KEY = if ($env:CODEFRESH_API_KEY) { $env:CODEFRESH_API_KEY } else { $env:CF_API_KEY }

# 4. Build and test.
$PSNativeCommandUseErrorActionPreference = $false
& dotnet build tests/Platform.Conformance.sln --configuration Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Note "dotnet build of tests/Platform.Conformance.sln failed (exit $LASTEXITCODE)"
    exit 1
}
# The console logger at normal verbosity streams each test's outcome while the suite runs (a live run takes hours;
# the TRX appears only at the end). Codefresh ends a build whose log stays silent for 45 minutes ("inactivity", first
# live run 2026-09-24), and one live test may wait longer, so a heartbeat line every 5 minutes keeps the build active.
$test = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
foreach ($argument in 'test', 'tests/Platform.Conformance.sln', '--configuration', 'Release', '--no-build',
    '--filter', $filter, '--logger', 'trx;LogFilePrefix=conformance', '--logger', 'console;verbosity=normal',
    '--results-directory', $ResultsDirectory) {
    $test.ArgumentList.Add($argument)
}
$test.UseShellExecute = $false
$test.WorkingDirectory = (Get-Location).ProviderPath
$process = [System.Diagnostics.Process]::Start($test)
while (-not $process.WaitForExit(300000)) {
    Write-Host "conformance-run: dotnet test still running at $([DateTime]::UtcNow.ToString('HH:mm:ss', [System.Globalization.CultureInfo]::InvariantCulture))Z"
}
$process.WaitForExit()
$status = $process.ExitCode

# 5. The capability report.
if (@(Get-ChildItem -LiteralPath $resultsPath -Filter '*.trx' -File -ErrorAction SilentlyContinue).Count -gt 0) {
    $pipeline = if ($env:CF_PIPELINE_NAME) { $env:CF_PIPELINE_NAME } else { 'conformance' }
    & dotnet run --project tests/Platform.Conformance.Report --configuration Release --no-build -- report `
        --trx $ResultsDirectory `
        --assembly tests/Platform.Conformance.Tests/bin/Release/net10.0/Platform.Conformance.Tests.dll `
        --assembly tests/Platform.Conformance.Offline/bin/Release/net10.0/Platform.Conformance.Offline.dll `
        --repo-root . --out $ResultsDirectory --title "$pipeline $($env:PLATFORM_RUN_ID)"
    $summaryMarkdown = Join-Path $resultsPath 'summary.md'
    if (Test-Path -LiteralPath $summaryMarkdown -PathType Leaf) {
        [System.IO.File]::ReadAllLines($summaryMarkdown) | ForEach-Object { Write-Host $_ }
    }
}

# 6. Build annotations: per-verdict counts and failed capability IDs (never fatal).
$key = if ($env:CF_API_KEY) { $env:CF_API_KEY } else { $env:CODEFRESH_API_KEY }
$summaryJson = Join-Path $resultsPath 'summary.json'
if ($key -and $env:CF_BUILD_ID -and (Test-Path -LiteralPath $summaryJson -PathType Leaf)) {
    try {
        $annotations = @(Get-Annotation $summaryJson $env:CF_BUILD_ID $env:PLATFORM_RUN_ID)
    }
    catch {
        Write-Note "WARN no annotations: $summaryJson is not a readable summary ($($_.Exception.Message))"
        $annotations = @()
    }
    if ($annotations.Count -gt 0) {
        $headerDirectory = [System.IO.Directory]::CreateTempSubdirectory('conformance-run-').FullName
        try {
            $headers = Join-Path $headerDirectory 'headers'
            Write-PrivateFile $headers "Authorization: $key`nContent-Type: application/json`n"
            $url = "$(if ($env:CF_URL) { $env:CF_URL } else { 'https://g.codefresh.io' })/api/annotations"
            foreach ($annotation in $annotations) {
                & curl -fsS --max-time 30 -H "@$headers" -X POST --data-binary $annotation $url | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    Write-Note "WARN annotation not recorded (POST $url, curl exit $LASTEXITCODE) [VERIFY the annotations route]"
                }
            }
        }
        finally {
            Remove-Item -LiteralPath $headerDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
exit $status
