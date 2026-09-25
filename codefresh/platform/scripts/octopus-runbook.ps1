#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Runs a config-as-code Octopus runbook through the REST API, optionally with prompted variable values,
    and optionally waits for its task.

.DESCRIPTION
    ADR-IR34 "Test harness": conformance-arm.ps1 force-sleeps both app clusters through env-sleep, and
    conformance-teardown.ps1 does the same. Run only by the platform-env/conformance* pipelines, which carry
    context platform-octopus (§7.0 decision 4).

    The calls mirror the harness's OctopusApi (tests/Platform.Conformance.Harness), each through
    'curl -fsS --max-time 60' with the headers in a private file:
      GET  /api/{space}/projects/{slug}
      GET  /api/{space}/environments/all
      GET  /api/{space}/projects/{projectId}/{gitRef}/runbooks?take=1000
      GET  /api/{space}/projects/{projectId}/{gitRef}/runbooks/{runbookId}/runbookRuns/preview/{environmentId}
      POST /api/{space}/projects/{projectId}/{gitRef}/runbooks/{runbookId}/run/v1
      GET  /api/{space}/tasks/{taskId}
    The preview is read only for -Prompt, the task only for -WaitMinutes (every 20 seconds).

    Environment: OCTOPUS_URL, OCTOPUS_SPACE_ID, OCTOPUS_API_KEY (context platform-octopus; the key is never
    printed and never on a command line); CF_BUILD_ID (default notes).

    Output: the task ID on standard output; the log on standard error.
    Exit codes: 0 the run was queued (with -WaitMinutes: its task finished successfully); 1 a lookup, the run
    request or the task failed; 2 invalid input (a -Prompt without '=', a missing Octopus variable).

.PARAMETER Project
    Project slug, for example platform-infrastructure.

.PARAMETER Runbook
    Runbook slug, name or ID.

.PARAMETER Environment
    Environment name, for example infra-nonprod.

.PARAMETER GitRef
    Git reference of the config-as-code runbooks (default refs/heads/main).

.PARAMETER Prompt
    Values of prompted variables as Name=value; the name matches the run preview's form without regard to
    case. Several values as a PowerShell array; 'pwsh -File' passes one.

.PARAMETER Notes
    Notes and comments of the run (default "platform-env conformance <CF_BUILD_ID or local>").

.PARAMETER WaitMinutes
    Waits up to this many minutes for the task; 0 (default) returns once the run is queued.

.EXAMPLE
    pwsh -NoProfile -File octopus-runbook.ps1 -Project platform-infrastructure -Runbook env-sleep -Environment infra-nonprod -Prompt Sleep.Force=true -WaitMinutes 30
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Project,

    [Parameter(Mandatory)]
    [string] $Runbook,

    [Parameter(Mandatory)]
    [string] $Environment,

    [string] $GitRef = 'refs/heads/main',

    [string[]] $Prompt = @(),

    [string] $Notes = '',

    [ValidateRange(0, 100000)]
    [int] $WaitMinutes = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("octopus-runbook: $Message")
}

function Stop-Runbook([string] $Message, [int] $ExitCode = 1) {
    Write-Note $Message
    exit $ExitCode
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

# One Octopus request: curl with the private header file. Returns the exit code and the body; never throws.
function Invoke-Octopus([string[]] $Arguments) {
    $PSNativeCommandUseErrorActionPreference = $false
    $lines = & curl -fsS --max-time 60 -H "@$script:HeaderFile" @Arguments
    return @{ ExitCode = $LASTEXITCODE; Body = (@($lines) -join "`n") }
}

# The body of a successful request as JSON (hashtables and arrays); $null for an empty body. Throws when the
# request failed or the body is not JSON.
function Read-Response([hashtable] $Response) {
    if ($Response.ExitCode -ne 0) {
        throw "curl exit $($Response.ExitCode)"
    }
    if ([string]::IsNullOrWhiteSpace($Response.Body)) {
        return $null
    }
    $json = ConvertFrom-Json -InputObject $Response.Body -AsHashtable -NoEnumerate -Depth 100
    return , $json
}

# jq '.Name': $null for a missing key or a null node; an error for a node that is not an object.
function Get-JsonProperty([object] $Node, [string] $Name) {
    if ($null -eq $Node) {
        return $null
    }
    if ($Node -isnot [System.Collections.IDictionary]) {
        throw "cannot read '$Name' of a JSON value that is not an object"
    }
    return , $Node[$Name]
}

# jq '.[]': the items of an array or the values of an object, one by one; an error for anything else.
function Get-JsonItem([object] $Node, [string] $What) {
    if ($Node -is [System.Collections.IDictionary]) {
        $Node = $Node.Values
    }
    elseif ($null -eq $Node -or $Node -is [string] -or $Node -isnot [System.Collections.IEnumerable]) {
        throw "cannot iterate over $What"
    }
    foreach ($item in $Node) {
        , $item
    }
}

# jq -r of '<value> // empty': '' for null and false, a string as it is, other values as JSON.
function ConvertTo-Text([object] $Value) {
    if ($null -eq $Value -or ($Value -is [bool] -and -not $Value)) {
        return ''
    }
    if ($Value -is [string]) {
        return $Value
    }
    if ($Value -is [bool]) {
        return 'true'
    }
    return ConvertTo-Json -InputObject $Value -Depth 100 -Compress
}

# jq's ascii_downcase: only A-Z change.
function ConvertTo-AsciiLower([string] $Text) {
    return [regex]::Replace($Text, '[A-Z]', { param($letter) $letter.Value.ToLowerInvariant() })
}

# jq '.x == "text"': only a string equals a string.
function Test-JsonText([object] $Value, [string] $Text) {
    return $Value -is [string] -and $Value -ceq $Text
}

# jq -r '.x' printed as "true".
function Test-JsonTrue([object] $Value) {
    return ($Value -is [bool] -and $Value) -or (Test-JsonText $Value 'true')
}

foreach ($name in 'OCTOPUS_URL', 'OCTOPUS_SPACE_ID', 'OCTOPUS_API_KEY') {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrEmpty($value) -or $value -match '<[\s\S]*>') {
        Stop-Runbook "$name is missing (context platform-octopus)" 2
    }
}
if (-not $PSBoundParameters.ContainsKey('Notes')) {
    $build = if ($env:CF_BUILD_ID) { $env:CF_BUILD_ID } else { 'local' }
    $Notes = "platform-env conformance $build"
}
$prompts = [System.Collections.Generic.List[hashtable]]::new()
foreach ($entry in $Prompt) {
    $separator = $entry.IndexOf('=')
    if ($separator -lt 0) {
        Stop-Runbook "-Prompt takes Name=value, not '$entry'" 2
    }
    $prompts.Add(@{ Name = $entry.Substring(0, $separator); Value = $entry.Substring($separator + 1) })
}

$base = "$($env:OCTOPUS_URL.TrimEnd('/'))/api/$($env:OCTOPUS_SPACE_ID)"
$work = [System.IO.Directory]::CreateTempSubdirectory('octopus-runbook-').FullName
try {
    $script:HeaderFile = Join-Path $work 'headers'
    Write-PrivateFile $script:HeaderFile "X-Octopus-ApiKey: $($env:OCTOPUS_API_KEY)`nContent-Type: application/json`n"
    $ref = [uri]::EscapeDataString($GitRef)

    $url = "$base/projects/$([uri]::EscapeDataString($Project))"
    try {
        $projectId = ConvertTo-Text (Get-JsonProperty (Read-Response (Invoke-Octopus @($url))) 'Id')
    }
    catch {
        Stop-Runbook "project $Project lookup failed (GET $url): $($_.Exception.Message)"
    }
    if (-not $projectId) {
        Stop-Runbook "project $Project not found (GET $url)"
    }

    $url = "$base/environments/all"
    try {
        $environments = Read-Response (Invoke-Octopus @($url))
        # jq 'first(.[] | select(...))': the first match ends the search.
        $found = @(Get-JsonItem $environments 'the environment list' | Where-Object { Test-JsonText (Get-JsonProperty $_ 'Name') $Environment } | Select-Object -First 1)
        $environmentId = if ($found.Count -gt 0) { ConvertTo-Text (Get-JsonProperty $found[0] 'Id') } else { '' }
    }
    catch {
        Stop-Runbook "environment lookup failed (GET $url): $($_.Exception.Message)"
    }
    if (-not $environmentId) {
        Stop-Runbook "environment $Environment not found (GET $url)"
    }

    $url = "$base/projects/$projectId/$ref/runbooks?take=1000"
    try {
        $runbooks = Read-Response (Invoke-Octopus @($url))
        $found = @(Get-JsonItem (Get-JsonProperty $runbooks 'Items') 'the runbook list (Items)' | Where-Object {
                (Test-JsonText (Get-JsonProperty $_ 'Slug') $Runbook) -or (Test-JsonText (Get-JsonProperty $_ 'Name') $Runbook) -or (Test-JsonText (Get-JsonProperty $_ 'Id') $Runbook)
            } | Select-Object -First 1)
        $runbookId = if ($found.Count -gt 0) { ConvertTo-Text (Get-JsonProperty $found[0] 'Id') } else { '' }
    }
    catch {
        Stop-Runbook "runbook lookup failed (GET $url): $($_.Exception.Message)"
    }
    if (-not $runbookId) {
        Stop-Runbook "runbook $Runbook not found in $Project at $GitRef"
    }

    # Prompted variables: map each name to the form element of the run preview.
    $form = [ordered]@{}
    if ($prompts.Count -gt 0) {
        $url = "$base/projects/$projectId/$ref/runbooks/$runbookId/runbookRuns/preview/$environmentId"
        try {
            $preview = Read-Response (Invoke-Octopus @($url))
        }
        catch {
            Stop-Runbook "run preview failed (GET $url): $($_.Exception.Message)"
        }
        # jq '.Form.Elements[]?': a preview without form elements has no prompted variable.
        try {
            $elements = @(Get-JsonItem (Get-JsonProperty (Get-JsonProperty $preview 'Form') 'Elements') 'Form.Elements')
        }
        catch {
            $elements = @()
        }
        foreach ($entry in $prompts) {
            $wanted = ConvertTo-AsciiLower $entry.Name
            $element = ''
            try {
                foreach ($candidate in $elements) {
                    if ((ConvertTo-AsciiLower (ConvertTo-Text (Get-JsonProperty (Get-JsonProperty $candidate 'Control') 'Name'))) -ceq $wanted) {
                        $element = ConvertTo-Text (Get-JsonProperty $candidate 'Name')
                        break
                    }
                }
            }
            catch {
                Stop-Runbook "run preview failed (GET $url): $($_.Exception.Message)"
            }
            if (-not $element) {
                Stop-Runbook "runbook $Runbook has no prompted variable $($entry.Name) in $Environment"
            }
            $form[$element] = $entry.Value
        }
    }

    $run = [ordered]@{
        SpaceId   = $env:OCTOPUS_SPACE_ID
        ProjectId = $projectId
        RunbookId = $runbookId
        GitRef    = $GitRef
        Notes     = $Notes
        Runs      = @([ordered]@{ EnvironmentId = $environmentId; FormValues = $form; Comments = $Notes })
    }
    $runFile = Join-Path $work 'run.json'
    [System.IO.File]::WriteAllText($runFile, (ConvertTo-Json -InputObject $run -Depth 20) + "`n", [System.Text.UTF8Encoding]::new($false))
    $url = "$base/projects/$projectId/$ref/runbooks/$runbookId/run/v1"
    try {
        $resources = Get-JsonProperty (Read-Response (Invoke-Octopus @('-X', 'POST', '--data-binary', "@$runFile", $url))) 'Resources'
        # jq '(.Resources // [])[0].TaskId'
        if ($null -eq $resources -or ($resources -is [bool] -and -not $resources)) {
            $resources = @()
        }
        if ($resources -is [System.Collections.IDictionary] -or $resources -is [string] -or $resources -isnot [System.Collections.IEnumerable]) {
            throw 'Resources is not an array'
        }
        $first = @($resources) | Select-Object -First 1
        $taskId = ConvertTo-Text (Get-JsonProperty $first 'TaskId')
    }
    catch {
        Stop-Runbook "the run request failed (POST $url): $($_.Exception.Message)"
    }
    if (-not $taskId) {
        Stop-Runbook "Octopus returned no task for the run of $Project/$Runbook in $Environment (POST $url)"
    }
    Write-Note "$Project/$Runbook in $Environment queued as $taskId"
    Write-Output $taskId

    if ($WaitMinutes -eq 0) {
        exit 0
    }
    $taskLink = "$($env:OCTOPUS_URL.TrimEnd('/'))/app#/$($env:OCTOPUS_SPACE_ID)/tasks/$taskId"
    $deadline = [DateTime]::UtcNow.AddMinutes($WaitMinutes)
    $state = 'unknown'
    while ($true) {
        $url = "$base/tasks/$taskId"
        $response = Invoke-Octopus @($url)
        if ($response.ExitCode -ne 0) {
            Stop-Runbook "task $taskId lookup failed (GET $url): curl exit $($response.ExitCode)"
        }
        $completed = $false
        $task = $null
        try {
            $task = Read-Response $response
            $completed = Test-JsonTrue (Get-JsonProperty $task 'IsCompleted')
            $state = ConvertTo-Text (Get-JsonProperty $task 'State')
        }
        catch {
            Write-Note "WARN task $taskId returned no readable state: $($_.Exception.Message)"
        }
        if ($completed) {
            Write-Note "$taskId finished: $state"
            if (-not (Test-JsonTrue (Get-JsonProperty $task 'FinishedSuccessfully'))) {
                Stop-Runbook "$Project/$Runbook in $Environment did not finish successfully (task $taskId, state $state): $taskLink"
            }
            exit 0
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            Stop-Runbook "task $taskId did not finish within $WaitMinutes minute(s) (last state $state): $taskLink"
        }
        Start-Sleep -Seconds 20
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
