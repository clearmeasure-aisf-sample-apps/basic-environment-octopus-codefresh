#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Reports releases that stalled between environments, read from the pin commits on main, and with -PullRequestBuilds pull requests whose build never started; with -Issues it keeps one GitHub issue per finding.

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

    -PullRequestBuilds. A push to a pull request branch sometimes starts no Codefresh build, and the required status
    then never arrives. For every pair of -BuildContext (a repository and the status context its pull requests need)
    the open pull requests are read through the GitHub REST API, and each one gets a state:
      1. Fork: the head is in another repository. Codefresh starts no build for a fork, so it is never reported.
      2. Started: the head commit has a status in the context, in any state (pending, success, failure, error).
      3. Draft: a draft without that status.
      4. Waiting: no status, and the wait is at most -BuildThresholdMinutes old. The wait starts at the committer
         date of the head commit, or at the opening of the pull request when that is later (a commit that was made
         long before it was pushed has not waited since it was made).
      5. NotStarted: everything else.
    A closed or merged pull request is not listed and so never reported; a push to the default branch is no pull
    request. The reads need no permission on a public repository; with GITHUB_TOKEN set they carry it. A repository
    that cannot be read gets a FAIL line, the other repositories are still checked, and the run exits 1.
    One line per pull request without a build, then a PASS or FAIL line; the objects follow those of the stalls
    (they carry Repository, PullRequest and Context instead of App, Version and Environment).

    -Issues. Reconciles GitHub issues of -Repository with the result, through the REST API (GITHUB_API_URL, default
    https://api.github.com) and the token in the environment variable GITHUB_TOKEN, which is never printed:
      - a stall gets one issue titled "Release <version> of <app> has not left <environment>", unless an open issue
        has that exact title, or one with that title was closed after the pin (someone already answered this stall);
      - an open issue that this script wrote (same title shape, and the marker line in its body) whose release is
        promoted, bypassed, rolled back, replaced by a newer release or no longer checked gets one comment saying
        which, and is closed. A release that is frozen or waiting keeps its issue, and so does an issue whose
        release the pin history does not know;
      - with -PullRequestBuilds, a pull request whose build did not start gets one issue titled
        "Build <context> of pull request <owner>/<repo>#<number> has not started", under the same two conditions
        (closed after the wait began: answered). The title names the pull request and not its head, so a closed
        issue that this script wrote about another head commit is no answer: a later head that gets no build
        either gets a new issue, also when the earlier one was closed after that head was committed;
      - an open issue of that kind that this script wrote gets one comment and is closed when the head commit has
        the status, or when the pull request was merged or closed. It is kept while the head still waits, while the
        pull request is a draft, when its repository is not in -BuildContext or could not be read, and when the
        number is no pull request there.
    -DryRun only reads and prints what it would write.

    Exit codes: without -Issues 0 nothing found, 1 at least one stall or one build that did not start; with -Issues
    0 reconciled, 1 a GitHub call failed; a repository of -BuildContext that cannot be read is 1 either way;
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

.PARAMETER PullRequestBuilds
    Also check the open pull requests of -BuildContext for a build that never started.

.PARAMETER BuildContext
    <owner>/<repo>=<status context> pairs: the repositories whose pull requests are checked, each with the commit
    status its pull requests need (default: this repository with codefresh/env-checks and the app repository with
    codefresh/ci, as in .claude/factory-loop.json).

.PARAMETER BuildThresholdMinutes
    How long the head commit of a pull request may wait for its first status (default: 30).

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
    pwsh -NoProfile -File scripts/release/release-stall-check.ps1 -PullRequestBuilds -All

.EXAMPLE
    pwsh -NoProfile -File scripts/release/release-stall-check.ps1 -Issues -PullRequestBuilds -Repository <owner>/<repo> -DryRun
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
    [switch] $PullRequestBuilds,
    [string[]] $BuildContext = @(
        'clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh=codefresh/env-checks',
        'clearmeasure-aisf-sample-apps/20260923-001=codefresh/ci'
    ),
    [ValidateRange(1, 527040)][int] $BuildThresholdMinutes = 30,
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
$buildCheck = 'build-start'
$repositoryPattern = '[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+'
$contextPattern = '[A-Za-z0-9][A-Za-z0-9_./-]*'
$buildContextPattern = "^(?<repository>$repositoryPattern)=(?<context>$contextPattern)`$"
$buildTitlePattern = "^Build (?<context>$contextPattern) of pull request (?<repository>$repositoryPattern)#(?<pull>[1-9][0-9]{0,8}) has not started`$"
$buildStates = @('pending', 'success', 'failure', 'error')

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

function Get-BuildTitle([string] $Repository, [int] $Number, [string] $Context) {
    return "Build $Context of pull request $Repository#$Number has not started"
}

# A value inside a JSON answer by its path of property names, or $null when a step of the path is missing.
function Get-JsonValue($Object, [string[]] $Path) {
    $value = $Object
    foreach ($name in $Path) {
        if ($null -eq $value) {
            return $null
        }
        $property = $value.PSObject.Properties[$name]
        if (-not $property) {
            return $null
        }
        $value = $property.Value
    }
    return $value
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
$buildTargets = [System.Collections.Generic.List[object]]::new()
if ($PullRequestBuilds) {
    foreach ($entry in @($BuildContext | ForEach-Object { "$_".Split(',') } | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        $pair = [regex]::Match($entry, $buildContextPattern)
        if (-not $pair.Success) {
            Stop-Usage "-BuildContext '$entry' is not <owner>/<repo>=<status context>"
        }
        $buildTargets.Add([pscustomobject]@{ Repository = $pair.Groups['repository'].Value; Context = $pair.Groups['context'].Value })
    }
    if ($buildTargets.Count -eq 0) {
        Stop-Usage '-PullRequestBuilds needs at least one -BuildContext <owner>/<repo>=<status context>'
    }
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

# --- GitHub ------------------------------------------------------------------------------------------------------

if ($Issues) {
    if (-not $Repository) {
        $Repository = "$env:GITHUB_REPOSITORY"
    }
    if ($Repository -notmatch "^$repositoryPattern`$") {
        Stop-Usage '-Issues needs -Repository <owner>/<repo> (or GITHUB_REPOSITORY)'
    }
    if (-not $env:GITHUB_TOKEN -and -not $DryRun) {
        Stop-Usage '-Issues needs the token of the workflow in the environment variable GITHUB_TOKEN'
    }
}
$apiBase = if ($env:GITHUB_API_URL) { $env:GITHUB_API_URL.TrimEnd('/') } else { 'https://api.github.com' }
$headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'release-stall-check' }
if ($env:GITHUB_TOKEN) {
    $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)"
}

# One GitHub REST call: whether it was answered with a 2xx status, the answer, and otherwise what went wrong
# (method, path and HTTP status; never a header).
function Invoke-GitHubRequest([string] $Method, [string] $Path, $Body = $null) {
    $request = @{ Method = $Method; Uri = "$apiBase/$Path"; Headers = $headers; SkipHttpErrorCheck = $true; StatusCodeVariable = 'status' }
    if ($null -ne $Body) {
        $request['Body'] = ConvertTo-Json -InputObject $Body -Depth 20
        $request['ContentType'] = 'application/json; charset=utf-8'
    }
    try {
        $answer = Invoke-RestMethod @request
    }
    catch {
        return [pscustomobject]@{ Ok = $false; Answer = $null; Failure = "$Method $Path did not answer ($($_.Exception.GetType().Name))" }
    }
    if ($status -lt 200 -or $status -ge 300) {
        $reason = if ($answer -is [psobject] -and $answer.PSObject.Properties['message']) { ": $($answer.message)" } else { '' }
        return [pscustomobject]@{ Ok = $false; Answer = $null; Failure = "$Method $Path answered HTTP $status$reason" }
    }
    return [pscustomobject]@{ Ok = $true; Answer = $answer; Failure = '' }
}

# One call of the issue reconciliation. A refused call ends the run: the next scheduled run starts from the issues
# as they are.
function Invoke-GitHubApi([string] $Method, [string] $Path, $Body = $null) {
    $result = Invoke-GitHubRequest $Method $Path $Body
    if (-not $result.Ok) {
        Write-Host "FAIL ${check} issues: $($result.Failure)"
        exit 1
    }
    return $result.Answer
}

# One read of the build check. A refused read is an exception that ends the check of that repository only.
function Read-GitHub([string] $Path) {
    $result = Invoke-GitHubRequest 'GET' $Path
    if (-not $result.Ok) {
        throw [System.Net.Http.HttpRequestException]::new($result.Failure)
    }
    return $result.Answer
}

# --- Pull requests whose build never started -----------------------------------------------------------------------

# The state of the status that a commit holds in a context (contexts compare without case, as GitHub does), or ''
# when it holds none.
function Get-BuildState([string] $Repository, [string] $Sha, [string] $Context) {
    for ($page = 1; $page -le 10; $page++) {
        $combined = Read-GitHub "repos/$Repository/commits/$Sha/status?per_page=100&page=$page"
        $batch = @(Get-JsonValue $combined 'statuses' | Where-Object { $null -ne $_ })
        foreach ($entry in $batch) {
            if ("$(Get-JsonValue $entry 'context')" -ieq $Context) {
                $state = "$(Get-JsonValue $entry 'state')"
                return $(if ($buildStates -contains $state) { $state } else { 'unknown' })
            }
        }
        if ($batch.Count -lt 100) {
            break
        }
    }
    return ''
}

# One row per open pull request of a repository, with its state in the context.
function Get-BuildRow([string] $Repository, [string] $Context) {
    $pulls = [System.Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le 50; $page++) {
        $batch = @(Read-GitHub "repos/$Repository/pulls?state=open&sort=created&direction=asc&per_page=100&page=$page")
        foreach ($pull in $batch) {
            if ($null -ne $pull) {
                $pulls.Add($pull)
            }
        }
        if ($batch.Count -lt 100) {
            break
        }
    }
    foreach ($pull in $pulls) {
        $number = [int] (Get-JsonValue $pull 'number')
        $sha = "$(Get-JsonValue $pull 'head', 'sha')"
        if ($sha -notmatch '^[0-9a-f]{40,64}$') {
            throw [System.Net.Http.HttpRequestException]::new("pull request $number has no head commit")
        }
        $branch = "$(Get-JsonValue $pull 'head', 'ref')"
        if ($branch -notmatch '^[A-Za-z0-9][A-Za-z0-9._/-]*$') {
            # Shown in the issue as code; a name with other characters is left out.
            $branch = ''
        }
        $row = [ordered]@{
            Repository       = $Repository
            PullRequest      = $number
            Context          = $Context
            State            = ''
            Head             = $sha
            Branch           = $branch
            BuildState       = ''
            CommittedAt      = ''
            WaitingSince     = ''
            WaitingMinutes   = 0
            ThresholdMinutes = $BuildThresholdMinutes
            Title            = Get-BuildTitle $Repository $number $Context
            Detail           = ''
        }
        if ("$(Get-JsonValue $pull 'head', 'repo', 'full_name')" -ine $Repository) {
            $row.State = 'Fork'
            $row.Detail = 'the head is in another repository, for which Codefresh starts no build'
            Write-Output ([pscustomobject] $row)
            continue
        }
        $row.BuildState = Get-BuildState $Repository $sha $Context
        if ($row.BuildState) {
            $row.State = 'Started'
            $row.Detail = "$Context reported $($row.BuildState) on head $(Get-ShortSha $sha)"
        }
        elseif ((Get-JsonValue $pull 'draft') -eq $true) {
            $row.State = 'Draft'
            $row.Detail = "a draft; head $(Get-ShortSha $sha) has no $Context status"
        }
        else {
            $committed = [DateTimeOffset]::MinValue
            $created = [DateTimeOffset]::MinValue
            $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal
            $commit = Read-GitHub "repos/$Repository/commits/$sha"
            if (-not [DateTimeOffset]::TryParse("$(Get-JsonValue $commit 'commit', 'committer', 'date')", $culture, $styles, [ref] $committed)) {
                throw [System.Net.Http.HttpRequestException]::new("commit $(Get-ShortSha $sha) of pull request $number has no committer date")
            }
            $since = $committed
            if ([DateTimeOffset]::TryParse("$(Get-JsonValue $pull 'created_at')", $culture, $styles, [ref] $created) -and $created -gt $since) {
                $since = $created
            }
            $row.CommittedAt = Format-Moment $committed
            $row.WaitingSince = Format-Moment $since
            $row.WaitingMinutes = [int] [Math]::Floor(($nowMoment - $since).TotalMinutes)
            if ($row.WaitingMinutes -le $BuildThresholdMinutes) {
                $row.State = 'Waiting'
                $row.Detail = "head $(Get-ShortSha $sha) waits $(Format-Minute ([Math]::Max(0, $row.WaitingMinutes))) for $Context; the threshold is $BuildThresholdMinutes minutes"
            }
            else {
                $row.State = 'NotStarted'
                $row.Detail = "head $(Get-ShortSha $sha) has no $Context status after $(Format-Minute $row.WaitingMinutes); the threshold is $BuildThresholdMinutes minutes"
            }
        }
        Write-Output ([pscustomobject] $row)
    }
}

$buildRows = [System.Collections.Generic.List[object]]::new()
$unread = [System.Collections.Generic.List[string]]::new()
foreach ($target in $buildTargets) {
    try {
        $found = @(Get-BuildRow $target.Repository $target.Context)
    }
    catch [System.Net.Http.HttpRequestException] {
        Write-Host "FAIL ${buildCheck}: $($target.Repository) could not be read: $($_.Exception.Message)"
        $unread.Add($target.Repository)
        continue
    }
    foreach ($row in $found) {
        $buildRows.Add($row)
    }
}
$notStarted = @($buildRows | Where-Object { $_.State -eq 'NotStarted' })
if ($PullRequestBuilds) {
    foreach ($row in $buildRows) {
        if ($row.State -eq 'NotStarted') {
            Write-Host ("NOBUILD $($row.Repository)#$($row.PullRequest): no $($row.Context) status on head $(Get-ShortSha $row.Head), " +
                "committed at $($row.CommittedAt); waiting $(Format-Minute $row.WaitingMinutes)")
        }
        elseif ($All) {
            Write-Host "$($row.State.ToLowerInvariant()) $($row.Repository)#$($row.PullRequest) $($row.Context): $($row.Detail)"
        }
    }
    $buildScope = "$($buildRows.Count) open pull request(s) in $($buildTargets.Count - $unread.Count) of $($buildTargets.Count) repositories, as of $(Format-Moment $nowMoment), threshold $BuildThresholdMinutes minutes"
    if ($notStarted.Count -gt 0) {
        Write-Host "FAIL ${buildCheck}: $($notStarted.Count) pull request(s) whose build did not start; $buildScope"
    }
    elseif ($unread.Count -gt 0) {
        Write-Host "FAIL ${buildCheck}: $($unread.Count) repositories could not be read; $buildScope"
    }
    else {
        Write-Host "PASS ${buildCheck}: no pull request waits for a build that did not start; $buildScope"
    }
}

if (-not $Issues) {
    $data = @(if ($All) { $rows } else { $stalls }) + @(if ($All) { $buildRows } else { $notStarted })
    if ($Json) {
        Write-Output (ConvertTo-Json -InputObject $data -Depth 20)
    }
    else {
        Write-Output $data
    }
    exit (($stalls.Count -gt 0 -or $notStarted.Count -gt 0 -or $unread.Count -gt 0) ? 1 : 0)
}

# --- GitHub issues -----------------------------------------------------------------------------------------------

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

function Get-BuildMarker([string] $Repository, [int] $Number, [string] $Context) {
    return "<!-- build-not-started repository=$Repository pull=$Number context=$Context -->"
}

# The line of an issue body that names the head commit the issue is about.
function Get-BuildHeadLine([string] $Sha) {
    return "| Head commit | $Sha |"
}

function Get-BuildIssueBody($Row) {
    $pull = "$($Row.Repository)#$($Row.PullRequest)"
    $branch = if ($Row.Branch) { "``$($Row.Branch)``" } else { 'the branch of the pull request' }
    return @(
        "The head commit of pull request $pull has no **$($Row.Context)** status: Codefresh did not start the build for this push, and the pull request cannot merge without it."
        ''
        '| | |'
        '|---|---|'
        "| Repository | $($Row.Repository) |"
        "| Pull request | $pull |"
        "| Branch | $branch |"
        "| Required status | $($Row.Context) |"
        (Get-BuildHeadLine $Row.Head)
        "| Committed at | $($Row.CommittedAt) |"
        "| Waiting since | $($Row.WaitingSince) (the head commit, or the opening of the pull request when that is later) |"
        "| Waiting when reported | $(Format-Minute $Row.WaitingMinutes) (threshold $($Row.ThresholdMinutes) minutes) |"
        ''
        "What to do: push an empty commit to $branch (``git commit --allow-empty -m `"Start the build`"``, then ``git push``), or start the build of that branch in Codefresh. More: [If a step stalls](../blob/main/docs/runbooks/demo-commit-to-prod.md#if-a-step-stalls)."
        ''
        "The check (``$workflow``, hourly) closes this issue with a comment once the head commit of the pull request has a $($Row.Context) status in any state, or the pull request is merged or closed. Closing it by hand is an answer too: the check does not open it again for this head commit, and opens a new one when a later head commit gets no build either."
        ''
        (Get-BuildMarker $Row.Repository $Row.PullRequest $Row.Context)
    ) -join "`n"
}

# Why an issue's pull request no longer waits for its build, or $null when it still does, or when nothing is known
# about it: the repository is not checked or could not be read, or the number is no pull request there (the issue
# is then left alone).
function Get-BuildResolution([string] $IssueRepository, [int] $Number, [string] $Context) {
    $target = $buildTargets | Where-Object { $_.Repository -ieq $IssueRepository -and $_.Context -ieq $Context } | Select-Object -First 1
    if (-not $target -or $unread -contains $target.Repository) {
        return $null
    }
    $row = $buildRows | Where-Object { $_.Repository -eq $target.Repository -and $_.Context -eq $target.Context -and $_.PullRequest -eq $Number } | Select-Object -First 1
    if ($row) {
        return $(if ($row.State -eq 'Started') { "$($row.Detail)." } else { $null })
    }
    # Not among the open pull requests: merged, closed, or no pull request at all.
    $result = Invoke-GitHubRequest 'GET' "repos/$($target.Repository)/pulls/$Number"
    if (-not $result.Ok) {
        Write-Host "WARN ${buildCheck}: $($result.Failure); the issue of $($target.Repository)#$Number is left alone"
        return $null
    }
    if ("$(Get-JsonValue $result.Answer 'state')" -ne 'closed') {
        return $null
    }
    if ((Get-JsonValue $result.Answer 'merged') -eq $true) {
        return "pull request $($target.Repository)#$Number was merged."
    }
    return "pull request $($target.Repository)#$Number was closed without a merge."
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

# Whether a closed issue with the title of an alert answers it. An alert whose title says everything (a release in an
# environment) has no Marker, and every such issue answers. An alert about something that changes under one title (a
# pull request, whose head moves) has a Marker and a Subject: an issue that this script wrote (the marker is in its
# body) answers only when its body names the same subject. An issue without the marker answers whatever it says.
function Test-AlertAnswer($Alert, $Issue) {
    if (-not $Alert.Marker) {
        return $true
    }
    $body = if ($Issue.PSObject.Properties['body']) { "$($Issue.body)" } else { '' }
    return -not $body.Contains($Alert.Marker) -or $body.Contains($Alert.Subject)
}

# Reconciles the issues of one kind of alert. An alert has a Title, Since (an issue with that title that was closed
# after this moment is an answer, see Test-AlertAnswer), a Body, and a Marker and a Subject (both empty for a stall).
# -Resolve gets the match of -TitlePattern and the body of an open issue, and answers why its alert is over, or $null
# to leave the issue alone.
function Sync-AlertIssue([object[]] $Alerts, [string] $Origin, [string] $TitlePattern, [scriptblock] $Resolve) {
    $opened = 0
    $present = 0
    $answered = 0
    $resolved = 0

    $missing = [System.Collections.Generic.List[object]]::new()
    foreach ($alert in $Alerts) {
        $existing = $open | Where-Object { $_.title -ceq $alert.Title } | Select-Object -First 1
        if ($existing) {
            Write-Host "EXISTS #$($existing.number) $($alert.Title)"
            $present++
        }
        else {
            $missing.Add($alert)
        }
    }
    if ($missing.Count -gt 0) {
        # Closed issues changed since the oldest of these alerts began: an alert that someone closed stays closed.
        $since = ($missing | ForEach-Object { $_.Since } | Sort-Object | Select-Object -First 1)
        $closed = @(Get-GitHubIssue "state=closed&since=$since")
        foreach ($alert in $missing) {
            $began = ConvertTo-Moment $alert.Since 'Since'
            $earlier = $closed | Where-Object { $_.title -ceq $alert.Title -and $_.closed_at -and ([DateTimeOffset] $_.closed_at) -gt $began -and (Test-AlertAnswer $alert $_) } | Select-Object -First 1
            if ($earlier) {
                Write-Host "ANSWERED #$($earlier.number) $($alert.Title): closed after $Origin, not opened again"
                $answered++
            }
            elseif ($DryRun) {
                Write-Host "WOULD OPEN $($alert.Title)"
            }
            else {
                $created = Invoke-GitHubApi 'POST' "repos/$Repository/issues" @{ title = $alert.Title; body = $alert.Body }
                Write-Host "OPENED #$($created.number) $($alert.Title)"
                $opened++
            }
        }
    }

    foreach ($issue in $open) {
        $title = [regex]::Match("$($issue.title)", $TitlePattern)
        if (-not $title.Success) {
            continue
        }
        $body = if ($issue.PSObject.Properties['body']) { "$($issue.body)" } else { '' }
        $resolution = & $Resolve $title $body
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

    return "$opened opened, $present already open, $answered answered earlier, $resolved resolved in $Repository"
}

$open = @(Get-GitHubIssue 'state=open')
$mode = if ($DryRun) { ' (dry run, nothing written)' } else { '' }

$stallAlerts = @($stalls | ForEach-Object { [pscustomobject]@{ Title = $_.Title; Since = $_.PinnedAt; Body = Get-StallIssueBody $_; Marker = ''; Subject = '' } })
$summary = Sync-AlertIssue $stallAlerts 'the pin' $titlePattern {
    param($Title, [string] $Body)
    $app = $Title.Groups['app'].Value
    $version = $Title.Groups['version'].Value
    $environment = $Title.Groups['env'].Value
    if (-not $Body.Contains((Get-StallMarker $app $version $environment))) {
        return $null
    }
    return Get-StallResolution $app $version $environment
}
Write-Host "PASS ${check} issues: $summary$mode"

if ($PullRequestBuilds) {
    $buildAlerts = @($notStarted | ForEach-Object {
            [pscustomobject]@{
                Title   = $_.Title
                Since   = $_.WaitingSince
                Body    = Get-BuildIssueBody $_
                Marker  = Get-BuildMarker $_.Repository $_.PullRequest $_.Context
                Subject = Get-BuildHeadLine $_.Head
            }
        })
    $summary = Sync-AlertIssue $buildAlerts 'the wait began' $buildTitlePattern {
        param($Title, [string] $Body)
        $issueRepository = $Title.Groups['repository'].Value
        $number = [int] $Title.Groups['pull'].Value
        $context = $Title.Groups['context'].Value
        if (-not $Body.Contains((Get-BuildMarker $issueRepository $number $context))) {
            return $null
        }
        return Get-BuildResolution $issueRepository $number $context
    }
    if ($unread.Count -gt 0) {
        Write-Host "FAIL ${buildCheck} issues: $summary$mode; not read, their issues left alone: $($unread -join ', ')"
        exit 1
    }
    Write-Host "PASS ${buildCheck} issues: $summary$mode"
}
exit 0
