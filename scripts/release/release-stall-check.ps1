#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Reports releases that stalled between environments, read from the pin commits on main; with -Issues it keeps one GitHub issue per stall.

.DESCRIPTION
    Octopus deploys a version by committing it to this repository. The subject of such a pin commit is
    "Pin <app> <version> in <environment> (Deployments-<n>)". A release that is pinned in one environment and never in
    the next one leaves no other trace, so this script reads the pin commits and says which releases wait.

    Input. Lines of the form "<commit> <committer date, ISO 8601> <subject>", which is what
    git log --format='%H %cI %s' prints. They come from the pipeline, from -PinLog, or (the default) from git log of
    -Ref in -Root. Lines that are no pin commit are ignored. Pins later than -Now are ignored too, so -Now answers
    "what was stalled at that moment".

    Rule. For every app that has a folder <AppsRoot>/<app>/envs/<environment>, and every environment of
    -EnvironmentOrder that the app has, except its last one:
      1. The candidate is the highest version ever pinned in that environment. Versions compare as versions
         (2.5.100 is newer than 2.5.99; 1.2.3 is newer than 1.2.3-hotfix.5), never as text. An older release that a
         newer one replaced in the same environment is therefore never reported.
      2. Promoted: the candidate is pinned in a later environment of the app.
      3. Bypassed: the next environment holds a newer version than the candidate (pinned there directly).
      4. RolledBack: the latest pin of the environment is an older version than the candidate.
      5. Frozen: the next environment is -FreezeEnvironment and -Now lies in a freeze window.
      6. Waiting: the candidate's latest pin is at most -ThresholdMinutes old. Minutes inside a freeze window do not
         count when the next environment is -FreezeEnvironment.
      7. Stalled: everything else.
    The last environment of an app has no next one, so a version pinned there is never a stall, and an app with one
    environment is never checked. The first pin of a new app is a candidate like any other.

    Freeze. The prod weekend freeze of octopus/terraform/freezes.tf: the first window (-FreezeFirstWindowStart to
    -FreezeFirstWindowEnd, the default of variable prod_freeze_first_window) repeats every seven days and concerns
    promotions into -FreezeEnvironment only.

    Output. One line per stall for people (app, version, the environment it is stuck in, the next environment, the pin
    commit, the deployment ID and the age), then a PASS or FAIL line. The data goes to the pipeline: one object per
    stall (-All: one per app and environment, whatever its state), or one JSON array with -Json.

    -Issues. Reconciles GitHub issues of -Repository with the result, through the REST API (GITHUB_API_URL, default
    https://api.github.com) and the token in the environment variable GITHUB_TOKEN, which is never printed:
      - a stall gets one issue titled "Release <version> of <app> has not left <environment>", unless an open issue
        has that exact title, or one with that title was closed after the pin (someone already answered this stall);
      - an open issue that this script wrote (same title shape, and the marker line in its body) whose release is
        promoted, bypassed, rolled back, replaced by a newer release or no longer checked gets one comment saying
        which, and is closed. A release that is frozen or waiting keeps its issue, and so does an issue whose
        release the pin history does not know.
    -DryRun only reads and prints what it would write.

    Exit codes: without -Issues 0 no stall, 1 at least one stall; with -Issues 0 reconciled, 1 a GitHub call failed;
    2 usage error.

.PARAMETER PinLine
    Pin lines, from the pipeline or as an argument. Without any, -PinLog or git log is read.

.PARAMETER PinLog
    A file of pin lines, instead of git log.

.PARAMETER Root
    The environment-repo root (default: two levels above this script).

.PARAMETER Ref
    The Git ref whose history is read (default: HEAD).

.PARAMETER AppsRoot
    The folder that holds <app>/envs/<environment> (default: <Root>/gitops/apps).

.PARAMETER Now
    The moment to judge, ISO 8601 (default: the current time).

.PARAMETER ThresholdMinutes
    How long a release may wait for the next environment (default: 90).

.PARAMETER EnvironmentOrder
    The promotion order (default: tdd, uat, prod).

.PARAMETER ExcludeApp
    Apps that are never checked (default: sandbox, the conformance fixture, whose releases stay in tdd on purpose).
    Pass an empty string to check every app.

.PARAMETER FreezeEnvironment
    The environment the freeze protects (default: prod). Empty: no freeze.

.PARAMETER FreezeFirstWindowStart
    Start of the first freeze window (default: 2026-10-03T00:00:00Z, a Saturday).

.PARAMETER FreezeFirstWindowEnd
    End of the first freeze window (default: 2026-10-05T00:00:00Z, the following Monday).

.PARAMETER All
    Also report the app and environment pairs that are not stalled, with their state.

.PARAMETER Json
    Write the data as one JSON array.

.PARAMETER Issues
    Reconcile the GitHub issues of -Repository with the result.

.PARAMETER Repository
    owner/repo of the issues (default: the environment variable GITHUB_REPOSITORY).

.PARAMETER DryRun
    With -Issues: read only, and print what would be written.

.EXAMPLE
    pwsh -NoProfile -File scripts/release/release-stall-check.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/release/release-stall-check.ps1 -Now 2026-10-06T12:00:00Z -All -Json

.EXAMPLE
    git log --format='%H %cI %s' origin/main | pwsh -NoProfile -File scripts/release/release-stall-check.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/release/release-stall-check.ps1 -Issues -Repository <owner>/<repo> -DryRun
#>
[CmdletBinding()]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseProcessBlockForPipelineCommand', '', Justification = 'The pipeline is read once, whole, through $input.')]
param(
    [Parameter(ValueFromPipeline)][string[]] $PinLine = @(),
    [string] $PinLog = '',
    [string] $Root = '',
    [string] $Ref = 'HEAD',
    [string] $AppsRoot = '',
    [string] $Now = '',
    [ValidateRange(1, 527040)][int] $ThresholdMinutes = 90,
    [string[]] $EnvironmentOrder = @('tdd', 'uat', 'prod'),
    [string[]] $ExcludeApp = @('sandbox'),
    [string] $FreezeEnvironment = 'prod',
    [string] $FreezeFirstWindowStart = '2026-10-03T00:00:00Z',
    [string] $FreezeFirstWindowEnd = '2026-10-05T00:00:00Z',
    [switch] $All,
    [switch] $Json,
    [switch] $Issues,
    [string] $Repository = '',
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

# The whole pipeline input. Without a process block $PinLine holds only the last piped item, so it is read only when
# the lines came as an argument.
$piped = @($input | ForEach-Object { "$_" } | Where-Object { $_.Trim() })
if ($piped.Count -eq 0) {
    $piped = @($PinLine | ForEach-Object { "$_" } | Where-Object { $_.Trim() })
}

$check = 'release-stall'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$week = [TimeSpan]::FromDays(7)
$linePattern = '^(?<sha>[0-9a-f]{7,64})\s+(?<when>\S+)\s+(?<subject>.*)$'
$subjectPattern = '^Pin (?<app>[a-z0-9][a-z0-9-]*) (?<version>[0-9A-Za-z][0-9A-Za-z.+-]*) in (?<env>[a-z0-9][a-z0-9-]*) \((?<deployment>Deployments-\d+)\)$'
$versionPattern = '^(?<core>\d+(?:\.\d+){0,3})(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$'
$titlePattern = '^Release (?<version>[0-9A-Za-z][0-9A-Za-z.+-]*) of (?<app>[a-z0-9][a-z0-9-]*) has not left (?<env>[a-z0-9][a-z0-9-]*)$'
$workflow = '.github/workflows/release-stall-check.yml'

function Stop-Usage([string] $Message) {
    Write-Host "FAIL ${check}: $Message"
    exit 2
}

function ConvertTo-Moment([string] $Text, [string] $Name) {
    $moment = [DateTimeOffset]::MinValue
    $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
    if (-not [DateTimeOffset]::TryParse($Text, $culture, $styles, [ref] $moment)) {
        Stop-Usage "$Name '$Text' is not an ISO 8601 time"
    }
    return $moment.ToUniversalTime()
}

function Format-Moment([DateTimeOffset] $Moment) {
    return $Moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $culture)
}

function Format-Minute([int] $Minutes) {
    $days = [Math]::Floor($Minutes / 1440)
    $hours = [Math]::Floor(($Minutes % 1440) / 60)
    $rest = $Minutes % 60
    if ($days -gt 0) {
        return ('{0}d {1}h {2:00}m' -f $days, $hours, $rest)
    }
    if ($hours -gt 0) {
        return ('{0}h {1:00}m' -f $hours, $rest)
    }
    return "${rest}m"
}

function Get-ShortSha([string] $Sha) {
    return $Sha.Substring(0, [Math]::Min(7, $Sha.Length))
}

# A version as numbers and pre-release identifiers, or $null when the text is no version.
function ConvertTo-ReleaseVersion([string] $Text) {
    $match = [regex]::Match($Text, $versionPattern)
    if (-not $match.Success) {
        return $null
    }
    $numbers = @($match.Groups['core'].Value.Split('.') | ForEach-Object { [decimal] $_ })
    $pre = @()
    if ($match.Groups['pre'].Success) {
        $pre = @($match.Groups['pre'].Value.Split('.'))
    }
    return [pscustomobject]@{ Text = $Text; Numbers = $numbers; Pre = $pre }
}

# Semantic-version order: numbers first, then a release above its pre-releases, then the pre-release identifiers
# (numeric ones as numbers, below text ones). Returns -1, 0 or 1.
function Compare-ReleaseVersion($Left, $Right) {
    $length = [Math]::Max($Left.Numbers.Count, $Right.Numbers.Count)
    for ($index = 0; $index -lt $length; $index++) {
        $a = if ($index -lt $Left.Numbers.Count) { $Left.Numbers[$index] } else { [decimal] 0 }
        $b = if ($index -lt $Right.Numbers.Count) { $Right.Numbers[$index] } else { [decimal] 0 }
        if ($a -ne $b) {
            return $(if ($a -lt $b) { -1 } else { 1 })
        }
    }
    if ($Left.Pre.Count -eq 0 -or $Right.Pre.Count -eq 0) {
        if ($Left.Pre.Count -eq $Right.Pre.Count) {
            return 0
        }
        return $(if ($Left.Pre.Count -eq 0) { 1 } else { -1 })
    }
    $length = [Math]::Max($Left.Pre.Count, $Right.Pre.Count)
    for ($index = 0; $index -lt $length; $index++) {
        if ($index -ge $Left.Pre.Count) {
            return -1
        }
        if ($index -ge $Right.Pre.Count) {
            return 1
        }
        $a = [string] $Left.Pre[$index]
        $b = [string] $Right.Pre[$index]
        $aNumeric = $a -match '^\d+$'
        $bNumeric = $b -match '^\d+$'
        if ($aNumeric -and $bNumeric) {
            if ([decimal] $a -ne [decimal] $b) {
                return $(if ([decimal] $a -lt [decimal] $b) { -1 } else { 1 })
            }
            continue
        }
        if ($aNumeric -ne $bNumeric) {
            return $(if ($aNumeric) { -1 } else { 1 })
        }
        $order = [string]::CompareOrdinal($a, $b)
        if ($order -ne 0) {
            return $(if ($order -lt 0) { -1 } else { 1 })
        }
    }
    return 0
}

# The pin with the highest version of a list, or $null for an empty list.
function Get-NewestPin([object[]] $Pins) {
    $newest = $null
    foreach ($pin in $Pins) {
        if ($null -eq $newest -or (Compare-ReleaseVersion $pin.Parsed $newest.Parsed) -gt 0) {
            $newest = $pin
        }
    }
    return $newest
}

function Get-FreezeWindowIndex([DateTimeOffset] $Moment) {
    return [Math]::Max(0, [int] [Math]::Floor(($Moment - $script:freezeEnd).Ticks / $week.Ticks))
}

# The end of the freeze window that holds the moment, or $null when the moment is outside every window.
function Get-FreezeEnd([DateTimeOffset] $Moment) {
    if (-not $script:freezeOn -or $Moment -lt $script:freezeStart) {
        return $null
    }
    for ($index = Get-FreezeWindowIndex $Moment; ; $index++) {
        $start = $script:freezeStart.AddDays(7 * $index)
        if ($start -gt $Moment) {
            return $null
        }
        $end = $script:freezeEnd.AddDays(7 * $index)
        if ($Moment -lt $end) {
            return $end
        }
    }
}

# Whole minutes between two moments that lie inside freeze windows.
function Get-FrozenMinute([DateTimeOffset] $From, [DateTimeOffset] $To) {
    if (-not $script:freezeOn -or $To -le $script:freezeStart) {
        return 0
    }
    $ticks = [long] 0
    for ($index = Get-FreezeWindowIndex $From; ; $index++) {
        $start = $script:freezeStart.AddDays(7 * $index)
        if ($start -ge $To) {
            break
        }
        $end = $script:freezeEnd.AddDays(7 * $index)
        $low = if ($start -gt $From) { $start } else { $From }
        $high = if ($end -lt $To) { $end } else { $To }
        if ($high -gt $low) {
            $ticks += ($high - $low).Ticks
        }
    }
    return [int] [Math]::Floor([TimeSpan]::FromTicks($ticks).TotalMinutes)
}

function Get-PinText($Pin) {
    return "$(Get-ShortSha $Pin.Sha) at $(Format-Moment $Pin.When) ($($Pin.Deployment))"
}

function Get-StallTitle([string] $App, [string] $Version, [string] $Environment) {
    return "Release $Version of $App has not left $Environment"
}

# --- Input ------------------------------------------------------------------------------------------------------

if (-not $Root) {
    $Root = Join-Path $PSScriptRoot '..' '..'
}
if (-not $AppsRoot) {
    $AppsRoot = Join-Path $Root 'gitops' 'apps'
}
if (-not (Test-Path -LiteralPath $AppsRoot -PathType Container)) {
    Stop-Usage "no folder $AppsRoot (-AppsRoot holds <app>/envs/<environment>)"
}
$order = @($EnvironmentOrder | ForEach-Object { "$_".Split(',') } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($order.Count -lt 2) {
    Stop-Usage '-EnvironmentOrder needs at least two environments'
}
$excluded = @($ExcludeApp | ForEach-Object { "$_".Split(',') } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$nowMoment = if ($Now) { ConvertTo-Moment $Now '-Now' } else { [DateTimeOffset]::UtcNow }
$script:freezeOn = [bool] $FreezeEnvironment
$script:freezeStart = ConvertTo-Moment $FreezeFirstWindowStart '-FreezeFirstWindowStart'
$script:freezeEnd = ConvertTo-Moment $FreezeFirstWindowEnd '-FreezeFirstWindowEnd'
if ($script:freezeEnd -le $script:freezeStart -or ($script:freezeEnd - $script:freezeStart) -gt $week) {
    Stop-Usage 'the first freeze window must end after its start and last at most seven days'
}

if ($piped.Count -gt 0) {
    $lines = $piped
    $source = 'the pipeline'
}
elseif ($PinLog) {
    if (-not (Test-Path -LiteralPath $PinLog -PathType Leaf)) {
        Stop-Usage "no file $PinLog (-PinLog)"
    }
    $lines = @(Get-Content -LiteralPath $PinLog)
    $source = $PinLog
}
else {
    $PSNativeCommandUseErrorActionPreference = $false
    $lines = @(git -C $Root log '--format=%H %cI %s' $Ref 2>&1 | ForEach-Object { "$_" })
    $gitExit = $LASTEXITCODE
    $PSNativeCommandUseErrorActionPreference = $true
    if ($gitExit -ne 0) {
        Stop-Usage "git log $Ref failed in ${Root}: $($lines | Select-Object -First 1)"
    }
    $source = "git log $Ref"
}

$pins = [System.Collections.Generic.List[object]]::new()
$unordered = [System.Collections.Generic.List[string]]::new()
$sequence = $lines.Count
foreach ($line in $lines) {
    $sequence--
    $entry = [regex]::Match($line.Trim(), $linePattern)
    if (-not $entry.Success) {
        continue
    }
    $subject = [regex]::Match($entry.Groups['subject'].Value.Trim(), $subjectPattern)
    if (-not $subject.Success) {
        continue
    }
    $when = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($entry.Groups['when'].Value, $culture, [System.Globalization.DateTimeStyles]::AssumeUniversal, [ref] $when)) {
        continue
    }
    $parsed = ConvertTo-ReleaseVersion $subject.Groups['version'].Value
    if ($null -eq $parsed) {
        $unordered.Add("$($subject.Groups['app'].Value) $($subject.Groups['version'].Value)")
        continue
    }
    if ($when -gt $nowMoment) {
        continue
    }
    $pins.Add([pscustomobject]@{
            App        = $subject.Groups['app'].Value
            Version    = $parsed.Text
            Parsed     = $parsed
            Env        = $subject.Groups['env'].Value
            Deployment = $subject.Groups['deployment'].Value
            Sha        = $entry.Groups['sha'].Value
            When       = $when.ToUniversalTime()
            # git log lists the newest commit first, so a later line is an older commit.
            Sequence   = $sequence
        })
}
foreach ($name in @($unordered | Sort-Object -Unique)) {
    Write-Host "WARN ${check}: $name is no version this check can order; its pins are ignored"
}
$timeline = @($pins | Sort-Object -Property When, Sequence)

# --- The decision ------------------------------------------------------------------------------------------------

$environmentsByApp = [ordered]@{}
$notChecked = [System.Collections.Generic.List[string]]::new()
foreach ($app in @($timeline | ForEach-Object { $_.App } | Sort-Object -Unique)) {
    if ($excluded -contains $app) {
        $notChecked.Add("$app (excluded)")
        continue
    }
    $folder = Join-Path $AppsRoot $app 'envs'
    $has = @($order | Where-Object { Test-Path -LiteralPath (Join-Path $folder $_) -PathType Container })
    if ($has.Count -eq 0) {
        $notChecked.Add("$app (no $app/envs/<environment> under -AppsRoot)")
        continue
    }
    $environmentsByApp[$app] = $has
}

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($app in $environmentsByApp.Keys) {
    $environments = @($environmentsByApp[$app])
    $ofApp = @($timeline | Where-Object { $_.App -eq $app })
    for ($index = 0; $index -lt $environments.Count - 1; $index++) {
        $environment = $environments[$index]
        $next = $environments[$index + 1]
        $later = @($environments[($index + 1)..($environments.Count - 1)])
        $here = @($ofApp | Where-Object { $_.Env -eq $environment })
        if ($here.Count -eq 0) {
            continue
        }
        $candidate = Get-NewestPin $here
        $pin = @($here | Where-Object { $_.Version -eq $candidate.Version })[-1]
        $latest = $here[-1]
        $promotion = @($ofApp | Where-Object { $later -contains $_.Env -and $_.Version -eq $pin.Version }) | Select-Object -First 1
        $nextNewest = Get-NewestPin @($ofApp | Where-Object { $_.Env -eq $next })
        $age = [int] [Math]::Floor(($nowMoment - $pin.When).TotalMinutes)
        $freezes = $script:freezeOn -and $next -eq $FreezeEnvironment
        $waiting = if ($freezes) { $age - (Get-FrozenMinute $pin.When $nowMoment) } else { $age }
        $frozenUntil = if ($freezes) { Get-FreezeEnd $nowMoment } else { $null }

        if ($promotion) {
            $state = 'Promoted'
            $detail = "$($pin.Version) was pinned in $($promotion.Env) by $(Get-PinText $promotion)"
        }
        elseif ($nextNewest -and (Compare-ReleaseVersion $nextNewest.Parsed $pin.Parsed) -gt 0) {
            $state = 'Bypassed'
            $detail = "$next already holds the newer version $($nextNewest.Version), pinned by $(Get-PinText $nextNewest)"
        }
        elseif ($latest.Version -ne $pin.Version) {
            $state = 'RolledBack'
            $detail = "$environment was rolled back to $($latest.Version) by $(Get-PinText $latest)"
        }
        elseif ($null -ne $frozenUntil) {
            $state = 'Frozen'
            $detail = "promotions into $next are frozen until $(Format-Moment $frozenUntil)"
        }
        elseif ($waiting -le $ThresholdMinutes) {
            $state = 'Waiting'
            $detail = "waiting $(Format-Minute $waiting) for $next; the threshold is $ThresholdMinutes minutes"
        }
        else {
            $state = 'Stalled'
            $detail = "not pinned in $next after $(Format-Minute $waiting); the threshold is $ThresholdMinutes minutes"
        }
        $rows.Add([pscustomobject][ordered]@{
                App              = $app
                Version          = $pin.Version
                Environment      = $environment
                NextEnvironment  = $next
                State            = $state
                Commit           = $pin.Sha
                DeploymentId     = $pin.Deployment
                PinnedAt         = Format-Moment $pin.When
                AgeMinutes       = $age
                WaitingMinutes   = $waiting
                ThresholdMinutes = $ThresholdMinutes
                Title            = Get-StallTitle $app $pin.Version $environment
                Detail           = $detail
            })
    }
}

$stalls = @($rows | Where-Object { $_.State -eq 'Stalled' })
$scope = "$($environmentsByApp.Count) app(s) from $source, as of $(Format-Moment $nowMoment), threshold $ThresholdMinutes minutes"
if ($notChecked.Count -gt 0) {
    Write-Host "${check}: not checked: $($notChecked -join '; ')"
}
foreach ($row in $rows) {
    if ($row.State -eq 'Stalled') {
        $age = "age $(Format-Minute $row.AgeMinutes)"
        if ($row.WaitingMinutes -ne $row.AgeMinutes) {
            $age += " ($(Format-Minute $row.WaitingMinutes) outside the $($row.NextEnvironment) freeze)"
        }
        Write-Host ("STALL $($row.App) $($row.Version): stuck in $($row.Environment), not pinned in $($row.NextEnvironment); " +
            "pin $(Get-ShortSha $row.Commit) ($($row.DeploymentId)) at $($row.PinnedAt), $age")
    }
    elseif ($All) {
        Write-Host "$($row.State.ToLowerInvariant()) $($row.App) $($row.Version) in $($row.Environment): $($row.Detail)"
    }
}
if ($stalls.Count -gt 0) {
    Write-Host "FAIL ${check}: $($stalls.Count) stalled release(s); $scope"
}
else {
    Write-Host "PASS ${check}: no stalled release; $scope"
}

if (-not $Issues) {
    $data = @(if ($All) { $rows } else { $stalls })
    if ($Json) {
        Write-Output (ConvertTo-Json -InputObject $data -Depth 20)
    }
    else {
        Write-Output $data
    }
    exit ($stalls.Count -gt 0 ? 1 : 0)
}

# --- GitHub issues -----------------------------------------------------------------------------------------------

if (-not $Repository) {
    $Repository = "$env:GITHUB_REPOSITORY"
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    Stop-Usage '-Issues needs -Repository <owner>/<repo> (or GITHUB_REPOSITORY)'
}
if (-not $env:GITHUB_TOKEN -and -not $DryRun) {
    Stop-Usage '-Issues needs the token of the workflow in the environment variable GITHUB_TOKEN'
}
$apiBase = if ($env:GITHUB_API_URL) { $env:GITHUB_API_URL.TrimEnd('/') } else { 'https://api.github.com' }
$headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'release-stall-check' }
if ($env:GITHUB_TOKEN) {
    $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)"
}

# One GitHub REST call. A refused call ends the run: the next scheduled run starts from the issues as they are.
function Invoke-GitHubApi([string] $Method, [string] $Path, $Body = $null) {
    $request = @{ Method = $Method; Uri = "$apiBase/$Path"; Headers = $headers; SkipHttpErrorCheck = $true; StatusCodeVariable = 'status' }
    if ($null -ne $Body) {
        $request['Body'] = ConvertTo-Json -InputObject $Body -Depth 20
        $request['ContentType'] = 'application/json; charset=utf-8'
    }
    try {
        $answer = Invoke-RestMethod @request
    }
    catch {
        Write-Host "FAIL ${check} issues: $Method $Path did not answer ($($_.Exception.GetType().Name))"
        exit 1
    }
    if ($status -lt 200 -or $status -ge 300) {
        $reason = if ($answer -is [psobject] -and $answer.PSObject.Properties['message']) { ": $($answer.message)" } else { '' }
        Write-Host "FAIL ${check} issues: $Method $Path answered HTTP $status$reason"
        exit 1
    }
    return $answer
}

# Every issue of a listing (pull requests left out), 100 a page.
function Get-GitHubIssue([string] $Query) {
    $found = [System.Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le 50; $page++) {
        $batch = @(Invoke-GitHubApi 'GET' "repos/$Repository/issues?$Query&per_page=100&page=$page")
        foreach ($issue in $batch) {
            if ($null -ne $issue -and -not $issue.PSObject.Properties['pull_request']) {
                $found.Add($issue)
            }
        }
        if ($batch.Count -lt 100) {
            break
        }
    }
    return $found.ToArray()
}

function Get-StallMarker([string] $App, [string] $Version, [string] $Environment) {
    return "<!-- release-stall app=$App version=$Version environment=$Environment -->"
}

function Get-StallIssueBody($Row) {
    return @(
        "Release **$($Row.Version)** of **$($Row.App)** was pinned in **$($Row.Environment)** and has not been pinned in **$($Row.NextEnvironment)**."
        ''
        '| | |'
        '|---|---|'
        "| App | $($Row.App) |"
        "| Version | $($Row.Version) |"
        "| Stuck in | $($Row.Environment) |"
        "| Next environment | $($Row.NextEnvironment) |"
        "| Pin commit | $($Row.Commit) |"
        "| Deployment | $($Row.DeploymentId) |"
        "| Pinned at | $($Row.PinnedAt) |"
        "| Waiting when reported | $(Format-Minute $Row.WaitingMinutes) (threshold $($Row.ThresholdMinutes) minutes; age $(Format-Minute $Row.AgeMinutes)) |"
        ''
        "Octopus shows why the deployment to $($Row.NextEnvironment) did not run or did not finish: open the release $($Row.Version) of the project. Causes and actions: [If a step stalls](../blob/main/docs/runbooks/demo-commit-to-prod.md#if-a-step-stalls)."
        ''
        "The check (``$workflow``, hourly) closes this issue with a comment once $($Row.Version) is pinned in $($Row.NextEnvironment) or a newer release takes its place in $($Row.Environment). Closing it by hand is an answer too: the check does not open it again for this pin."
        ''
        (Get-StallMarker $Row.App $Row.Version $Row.Environment)
    ) -join "`n"
}

# Why an issue's release no longer waits, or $null when it still does, or when the pin history does not know the
# app, the environment or the version (the issue is then left alone: nothing of its title is ever echoed).
function Get-StallResolution([string] $App, [string] $Version, [string] $Environment) {
    if (-not $environmentsByApp.Contains($App)) {
        if (@($timeline | Where-Object { $_.App -eq $App -and $_.Env -eq $Environment -and $_.Version -eq $Version }).Count -eq 0) {
            return $null
        }
        return "$App is no longer checked (excluded, or it has no environment folder)."
    }
    $row = $rows | Where-Object { $_.App -eq $App -and $_.Environment -eq $Environment } | Select-Object -First 1
    if (-not $row) {
        $environments = @($environmentsByApp[$App])
        if ($order -contains $Environment -and ($environments -notcontains $Environment -or $environments[-1] -eq $Environment)) {
            return "$Environment is no longer an environment of $App with a next one."
        }
        return $null
    }
    if ($row.Version -eq $Version) {
        if (@('Promoted', 'Bypassed', 'RolledBack') -contains $row.State) {
            return "$($row.Detail)."
        }
        return $null
    }
    $parsed = ConvertTo-ReleaseVersion $Version
    $ofApp = @($timeline | Where-Object { $_.App -eq $App })
    $own = @($ofApp | Where-Object { $_.Env -eq $Environment -and $_.Version -eq $Version })
    if ($null -eq $parsed -or $own.Count -eq 0) {
        return $null
    }
    $environments = @($environmentsByApp[$App])
    $later = @($environments[([Array]::IndexOf($environments, $Environment) + 1)..($environments.Count - 1)])
    $promotion = @($ofApp | Where-Object { $later -contains $_.Env -and $_.Version -eq $Version }) | Select-Object -First 1
    $fate = if ($promotion) {
        "$Version was pinned in $($promotion.Env) by $(Get-PinText $promotion)"
    }
    else {
        "$Version itself was never pinned in $($row.NextEnvironment)"
    }
    return "$($row.Version) is now the newest release in $Environment (pin $(Get-ShortSha $row.Commit) at $($row.PinnedAt), $($row.DeploymentId)); $fate."
}

$open = @(Get-GitHubIssue 'state=open')
$opened = 0
$present = 0
$answered = 0
$resolved = 0

$missing = [System.Collections.Generic.List[object]]::new()
foreach ($row in $stalls) {
    $existing = $open | Where-Object { $_.title -ceq $row.Title } | Select-Object -First 1
    if ($existing) {
        Write-Host "EXISTS #$($existing.number) $($row.Title)"
        $present++
    }
    else {
        $missing.Add($row)
    }
}
if ($missing.Count -gt 0) {
    # Closed issues changed since the oldest of these pins: a stall that someone closed stays closed.
    $since = ($missing | ForEach-Object { $_.PinnedAt } | Sort-Object | Select-Object -First 1)
    $closed = @(Get-GitHubIssue "state=closed&since=$since")
    foreach ($row in $missing) {
        $pinned = ConvertTo-Moment $row.PinnedAt 'PinnedAt'
        $earlier = $closed | Where-Object { $_.title -ceq $row.Title -and $_.closed_at -and ([DateTimeOffset] $_.closed_at) -gt $pinned } | Select-Object -First 1
        if ($earlier) {
            Write-Host "ANSWERED #$($earlier.number) $($row.Title): closed after the pin, not opened again"
            $answered++
        }
        elseif ($DryRun) {
            Write-Host "WOULD OPEN $($row.Title)"
        }
        else {
            $created = Invoke-GitHubApi 'POST' "repos/$Repository/issues" @{ title = $row.Title; body = (Get-StallIssueBody $row) }
            Write-Host "OPENED #$($created.number) $($row.Title)"
            $opened++
        }
    }
}

foreach ($issue in $open) {
    $title = [regex]::Match("$($issue.title)", $titlePattern)
    if (-not $title.Success) {
        continue
    }
    $app = $title.Groups['app'].Value
    $version = $title.Groups['version'].Value
    $environment = $title.Groups['env'].Value
    $body = if ($issue.PSObject.Properties['body']) { "$($issue.body)" } else { '' }
    if (-not $body.Contains((Get-StallMarker $app $version $environment))) {
        continue
    }
    $resolution = Get-StallResolution $app $version $environment
    if (-not $resolution) {
        continue
    }
    if ($DryRun) {
        Write-Host "WOULD CLOSE #$($issue.number) $($issue.title): $resolution"
        continue
    }
    $comment = "Resolved: $resolution`n`nClosed by the release stall check (``$workflow``)."
    $null = Invoke-GitHubApi 'POST' "repos/$Repository/issues/$($issue.number)/comments" @{ body = $comment }
    $null = Invoke-GitHubApi 'PATCH' "repos/$Repository/issues/$($issue.number)" @{ state = 'closed'; state_reason = 'completed' }
    Write-Host "RESOLVED #$($issue.number) $($issue.title): $resolution"
    $resolved++
}

$mode = if ($DryRun) { ' (dry run, nothing written)' } else { '' }
Write-Host "PASS ${check} issues: $opened opened, $present already open, $answered answered earlier, $resolved resolved in $Repository$mode"
exit 0
