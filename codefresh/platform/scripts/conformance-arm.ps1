#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    platform-env/conformance-arm: arms a conformance run (ADR-IR34 "Test harness", Scheduling).

.DESCRIPTION
    In order:
      1. mints the run ID (PLATFORM_RUN_ID) unless one is given, and exports it;
      2. force-sleeps both app clusters through runbook env-sleep (Sleep.Force=true) in infra-nonprod and infra-prod
         (octopus-runbook.ps1, in parallel, up to 60 minutes each), waits until both clusters report powerState
         Stopped (env-sleep stops without waiting; aks-power.ps1), then CONFORMANCE_STOP_GRACE_MINUTES (default 15;
         Microsoft advises 15-30 minutes between a stop and a start, E50);
      3. holds the hourly env-sleep for the run: runbook sleep-hold in infra-nonprod and infra-prod (octopus-runbook.ps1,
         one after the other, up to 10 minutes each) with Sleep.HoldMinutes CONFORMANCE_HOLD_MINUTES (default 480, at
         most 720: the suite's 6-hour budget plus the sandbox builds queued before it) and Sleep.HoldBy
         conformance:<run id>. Without the hold, env-sleep stops a cluster between two Octopus tasks of the run, so a
         hold that is not set fails the arm; conformance-teardown.ps1 releases it;
      4. pushes the run's sandbox commits to <sandbox-app-repo> (sandbox-git.ps1):
           conformance/<run id>/failing-test   adds toggles/failing-test    -> sandbox/ci fails (CAP-CF-004)
           conformance/<run id>/green          adds conformance/run-id       -> sandbox/ci passes (CAP-CF-004)
           main                                conformance/last-run (canary) -> sandbox/release (CAP-CF-006..009)
         deletes the branches of older runs, and exports CONFORMANCE_FAILING_SHA, CONFORMANCE_GREEN_SHA and
         CONFORMANCE_RELEASE_SHA;
      5. queues a second sandbox/release build of the release commit (the rerun of CAP-CF-008), then
         platform-env/conformance with the run ID, the three commit SHAs and the rerun's build ID, through the
         Codefresh API (curl, the key in a private header file). With one build at a time (BASIC_1) the sandbox
         builds run first [VERIFY Q41].
    A cluster that does not stop in time (env-sleep kept it up because a task was running) is reported and the run
    goes on: the tests that need it wake it themselves. While the clusters' settings hold placeholders only the grace
    period applies. During the waits a heartbeat line every 5 minutes keeps the build log active (Codefresh ends a
    build whose log stays silent for 45 minutes). CONFORMANCE_SKIP_SLEEP=true skips step 2 (debugging only), never step 3.

    Environment: OCTOPUS_URL, OCTOPUS_SPACE_ID, OCTOPUS_API_KEY (platform-octopus); GITHUB_TOKEN, AZURE_TENANT_ID,
    AZURE_CLIENT_ID, AZURE_CLIENT_SECRET (platform-conformance); SANDBOX_APP_REPO (spec variable); CF_API_KEY (the
    build's own key) or CODEFRESH_API_KEY (Q49); TEST_FILTER (optional, passed on); CONFORMANCE_STOP_TIMEOUT_MINUTES
    (default 30); CONFORMANCE_HOLD_MINUTES (default 480); PLATFORM_SETTINGS_FILE and AZURE_SUBSCRIPTION_ID (cluster settings, as in the harness); CF_URL.
    Prints nothing secret and puts no secret on a command line.

    Exit codes: 0 armed; 1 a step failed (the message names it and the object).

.EXAMPLE
    pwsh -NoProfile -File codefresh/platform/scripts/conformance-arm.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. (Join-Path $PSScriptRoot 'sandbox-git.ps1')
. (Join-Path $PSScriptRoot 'aks-power.ps1')

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("conformance-arm: $Message")
}

function Stop-Arm([string] $Message) {
    Write-Note $Message
    exit 1
}

# A variable for the later steps. With Codefresh's cf_export: the value goes into the environment and 'cf_export NAME'
# (with --mask for a secret) reads it there, so no value is on a command line. Without it: a NAME=value line in
# $CF_VOLUME_PATH/env_vars_to_export, which a masked value never takes. Logs the way, never the value.
function Export-BuildVariable([string] $Name, [string] $Value, [switch] $Mask) {
    [Environment]::SetEnvironmentVariable($Name, $Value)
    $cfExport = Get-Command -Name cf_export -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    # Codefresh's cf_export has no shebang line, so pwsh cannot start it ("An error occurred trying to start process"): run it through a shell.
    $cfShell = (Get-Command -Name bash, sh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if ($cfExport) {
        if ($Mask) {
            & $cfShell $cfExport.Source --mask $Name
        }
        else {
            & $cfShell $cfExport.Source $Name
        }
        Write-Note "exported $Name through cf_export"
    }
    elseif ($Mask) {
        Write-Note "cannot export the masked ${Name}: cf_export is not on PATH"
        exit 1
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

# The run ID: lower case, bytes outside [a-z0-9.-] as '-', at most 40 characters.
function ConvertTo-RunId([string] $Value) {
    $characters = foreach ($byte in [System.Text.Encoding]::UTF8.GetBytes($Value)) {
        $character = if ($byte -ge 0x41 -and $byte -le 0x5A) { [char]($byte + 0x20) } else { [char]$byte }
        if ($character -cmatch '^[a-z0-9.-]$') { $character } else { '-' }
    }
    $text = -join $characters
    return $(if ($text.Length -gt 40) { $text.Substring(0, 40) } else { $text })
}

# Whole minutes from a variable, or the default when it is unset or not a whole number.
function Get-Minute([string] $Value, [long] $Default) {
    if ($Value -match '^[0-9]+$') {
        $minutes = 0L
        if ([long]::TryParse($Value, [ref] $minutes)) {
            return $minutes
        }
    }
    return $Default
}

$script:NextHeartbeat = [DateTime]::UtcNow.AddMinutes(5)
function Write-Heartbeat {
    $now = [DateTime]::UtcNow
    if ($now -ge $script:NextHeartbeat) {
        Write-Note "still running at $($now.ToString('HH:mm:ss', [System.Globalization.CultureInfo]::InvariantCulture))Z"
        $script:NextHeartbeat = $now.AddMinutes(5)
    }
}

# Start-Sleep that keeps the heartbeat going.
function Wait-Interval([double] $Seconds) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ($true) {
        $left = ($until - [DateTime]::UtcNow).TotalMilliseconds
        if ($left -le 0) {
            break
        }
        Start-Sleep -Milliseconds ([int][Math]::Min($left, 1000))
        Write-Heartbeat
    }
}

# The PowerShell that runs this script, to start octopus-runbook.ps1 with (the .NET global tool runs as 'dotnet pwsh.dll').
function Get-PwshCommand {
    $processPath = [Environment]::ProcessPath
    if (-not $processPath) {
        return , @('pwsh')
    }
    if ([System.IO.Path]::GetFileNameWithoutExtension($processPath) -eq 'dotnet') {
        return , @($processPath, (Join-Path $PSHOME 'pwsh.dll'))
    }
    return , @($processPath)
}

# Starts octopus-runbook.ps1 in its own process: standard error goes to the log, standard output (the task ID) is dropped.
function Start-EnvSleep([string] $Environment, [string] $Notes) {
    $command = Get-PwshCommand
    $start = [System.Diagnostics.ProcessStartInfo]::new($command[0])
    $arguments = @($command | Select-Object -Skip 1) + @(
        '-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'octopus-runbook.ps1'),
        '-Project', 'platform-infrastructure', '-Runbook', 'env-sleep', '-Environment', $Environment,
        '-Prompt', 'Sleep.Force=true', '-Notes', $Notes, '-WaitMinutes', '60')
    foreach ($argument in $arguments) {
        $start.ArgumentList.Add($argument)
    }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.WorkingDirectory = (Get-Location).ProviderPath
    $process = [System.Diagnostics.Process]::Start($start)
    $process.BeginOutputReadLine()
    return $process
}

# Runs runbook sleep-hold in one environment through octopus-runbook.ps1, in this process (two prompted values do not fit
# 'pwsh -File'), and waits for its task. Returns octopus-runbook's exit code; its log goes to standard error.
function Invoke-SleepHold([string] $Environment, [long] $Minutes, [string] $Holder, [string] $Notes) {
    $global:LASTEXITCODE = 0
    try {
        & (Join-Path $PSScriptRoot 'octopus-runbook.ps1') -Project 'platform-infrastructure' -Runbook 'sleep-hold' -Environment $Environment `
            -Prompt @("Sleep.HoldMinutes=$Minutes", "Sleep.HoldBy=$Holder") -Notes $Notes -WaitMinutes 10 | Out-Null
    }
    catch {
        Write-Note "sleep-hold in $Environment failed: $($_.Exception.Message)"
        return 1
    }
    return $LASTEXITCODE
}

# env-sleep stops without waiting: wait for powerState Stopped of both clusters. A cluster that does not stop in time
# is reported and the run goes on.
function Wait-Stopped([string] $Directory) {
    $limit = Get-Minute $env:CONFORMANCE_STOP_TIMEOUT_MINUTES 30
    if (-not (Initialize-AksPower -Directory $Directory)) {
        Write-Note 'WARN not waiting for Stopped'
        return
    }
    $deadline = [DateTime]::UtcNow.AddMinutes($limit)
    while ($true) {
        $pending = ''
        foreach ($tier in 'nonprod', 'prod') {
            $state = Get-AksPowerState -Tier $tier
            if ($state -cne 'Stopped/Succeeded' -and $state -cne 'unconfigured') {
                $pending += " $tier=$state"
            }
        }
        if (-not $pending) {
            Write-Note 'both clusters report Stopped'
            return
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            Write-Note "WARN not stopped after $limit minute(s):$pending"
            return
        }
        Wait-Interval 30
    }
}

# One sandbox branch from origin/main with one file that carries the run ID, force-pushed; returns its commit. Git's
# own output goes to the log, not into the return value.
function New-SandboxCommit([string] $Clone, [string] $Branch, [string] $Path, [string] $Message) {
    try {
        & git checkout --quiet -B $Branch origin/main | Out-Host
        $file = Join-Path $Clone $Path
        New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
        [System.IO.File]::WriteAllText($file, "run-id: $script:RunId`n", [System.Text.UTF8Encoding]::new($false))
        & git add $Path | Out-Host
        Invoke-SandboxGit commit --quiet -m $Message | Out-Host
        Invoke-SandboxGit push --quiet --force origin "HEAD:refs/heads/$Branch" | Out-Host
        $sha = & git rev-parse HEAD
    }
    catch {
        Stop-Arm "sandbox branch $Branch of $($env:SANDBOX_APP_REPO) not pushed: $($_.Exception.Message)"
    }
    return $sha
}

# A Codefresh API call: curl with the private header file. Returns the exit code and the body; never throws.
function Invoke-Codefresh([string[]] $Arguments) {
    $PSNativeCommandUseErrorActionPreference = $false
    $lines = & curl -fsS --max-time 60 -H "@$script:CodefreshHeaders" @Arguments
    return @{ ExitCode = $LASTEXITCODE; Body = (@($lines) -join "`n") }
}

# The build ID of a run response: the body without its quotes (a JSON string).
function Get-BuildId([hashtable] $Response) {
    return ($Response.Body -replace '"', '').TrimEnd("`n")
}

if (-not (Test-SandboxRequirement)) {
    exit 1
}

$work = [System.IO.Directory]::CreateTempSubdirectory('conformance-arm-').FullName
$entered = $false
try {
    # ------------------------------------------------------------ 1. the run ID
    $runId = $env:PLATFORM_RUN_ID
    if (-not $runId) {
        $build = if ($env:CF_BUILD_ID) { $env:CF_BUILD_ID } else { 'local' }
        $suffix = if ($build.Length -ge 8) { $build.Substring($build.Length - 8) } else { '' }
        $runId = "r$([DateTime]::UtcNow.ToString("yyyyMMdd't'HHmm", [System.Globalization.CultureInfo]::InvariantCulture))-$suffix"
    }
    $script:RunId = ConvertTo-RunId $runId
    Write-Note "run $script:RunId"
    Export-BuildVariable 'PLATFORM_RUN_ID' $script:RunId

    # ------------------------------------------------------------ 2. force-sleep both tiers
    if ($env:CONFORMANCE_SKIP_SLEEP -cne 'true') {
        $runs = foreach ($environment in 'infra-nonprod', 'infra-prod') {
            @{ Environment = $environment; Process = (Start-EnvSleep $environment "conformance:$script:RunId force-sleep") }
        }
        foreach ($run in $runs) {
            while (-not $run.Process.WaitForExit(1000)) {
                Write-Heartbeat
            }
            $run.Process.WaitForExit()
        }
        $failed = @($runs | Where-Object { $_.Process.ExitCode -ne 0 } | ForEach-Object { "$($_.Environment) (octopus-runbook exit $($_.Process.ExitCode))" })
        if ($failed.Count -gt 0) {
            Stop-Arm "env-sleep did not finish successfully in $($failed -join ' and '); see the octopus-runbook lines above"
        }
        Wait-Stopped $work
        $grace = Get-Minute $env:CONFORMANCE_STOP_GRACE_MINUTES 15
        Write-Note "waiting $grace minute(s) before anything starts the clusters"
        Wait-Interval ($grace * 60)
    }

    # ------------------------------------------------------------ 3. hold the hourly env-sleep for the run
    $holdMinutes = [Math]::Min((Get-Minute $env:CONFORMANCE_HOLD_MINUTES 480), 720)
    foreach ($environment in 'infra-nonprod', 'infra-prod') {
        $exitCode = Invoke-SleepHold $environment $holdMinutes "conformance:$script:RunId" "conformance:$script:RunId hold"
        if ($exitCode -ne 0) {
            Stop-Arm "sleep-hold did not finish successfully in $environment (octopus-runbook exit $exitCode): without the hold the hourly env-sleep can stop a cluster during the run"
        }
    }
    Write-Note "env-sleep held for $holdMinutes minute(s) in infra-nonprod and infra-prod"

    # ------------------------------------------------------------ 4. sandbox commits
    $clone = "$work/sandbox"
    $PSNativeCommandUseErrorActionPreference = $false
    Invoke-SandboxGit clone --quiet --branch main (Get-SandboxUrl) $clone
    if ($LASTEXITCODE -ne 0) {
        Stop-Arm "clone of $($env:SANDBOX_APP_REPO) failed (git exit $LASTEXITCODE)"
    }
    Push-Location -LiteralPath $clone
    $entered = $true

    # Branches of earlier runs go first; the results branch stays.
    $heads = @(Invoke-SandboxGit ls-remote --heads origin 'conformance/*')
    if ($LASTEXITCODE -ne 0) {
        Stop-Arm "listing the conformance/* branches of $($env:SANDBOX_APP_REPO) failed (git exit $LASTEXITCODE)"
    }
    foreach ($line in $heads) {
        $fields = @(([string]$line).Trim() -split '\s+')
        $branch = if ($fields.Count -ge 2) { $fields[1] -replace '^refs/heads/', '' } else { '' }
        if ($branch.StartsWith("conformance/$script:RunId/", [StringComparison]::Ordinal)) {
            continue
        }
        Invoke-SandboxGit push --quiet origin --delete $branch
        if ($LASTEXITCODE -ne 0) {
            Write-Note "WARN could not delete $branch"
        }
    }
    $PSNativeCommandUseErrorActionPreference = $true

    $failingSha = New-SandboxCommit $clone "conformance/$script:RunId/failing-test" 'toggles/failing-test' "conformance ${script:RunId}: failing-test toggle (CAP-CF-004)"
    $greenSha = New-SandboxCommit $clone "conformance/$script:RunId/green" 'conformance/run-id' "conformance ${script:RunId}: green branch (CAP-CF-004)"
    try {
        & git checkout --quiet -B main origin/main
        New-Item -ItemType Directory -Path (Join-Path $clone 'conformance') -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $clone 'conformance/last-run'), "run-id: $script:RunId`n", [System.Text.UTF8Encoding]::new($false))
        & git add conformance/last-run
        Invoke-SandboxGit commit --quiet -m "conformance ${script:RunId}: release canary"
        Invoke-SandboxGit push --quiet origin HEAD:refs/heads/main
        $releaseSha = & git rev-parse HEAD
    }
    catch {
        Stop-Arm "the release canary on main of $($env:SANDBOX_APP_REPO) was not pushed: $($_.Exception.Message)"
    }
    Write-Note "failing $failingSha, green $greenSha, release $releaseSha"
    Export-BuildVariable 'CONFORMANCE_FAILING_SHA' $failingSha
    Export-BuildVariable 'CONFORMANCE_GREEN_SHA' $greenSha
    Export-BuildVariable 'CONFORMANCE_RELEASE_SHA' $releaseSha

    # ------------------------------------------------------------ 5. rerun and queue
    $key = if ($env:CODEFRESH_API_KEY) { $env:CODEFRESH_API_KEY } else { $env:CF_API_KEY }
    if (-not $key) {
        Stop-Arm 'no Codefresh API key (CF_API_KEY or CODEFRESH_API_KEY) to queue platform-env/conformance'
    }
    $cfUrl = if ($env:CF_URL) { $env:CF_URL } else { 'https://g.codefresh.io' }
    $script:CodefreshHeaders = Join-Path $work 'cf-headers'
    Write-PrivateFile $script:CodefreshHeaders "Authorization: $key`nContent-Type: application/json`n"

    # A second sandbox/release build of the same commit (CAP-CF-008: a rerun creates no second Octopus release;
    # CAP-CF-014: the rerun reuses the locked images). With one build at a time this build may start before the push's
    # own build; the tests order the builds by start time. The run names the pipeline's git trigger by ID. Not fatal:
    # without it the rerun half of the test is Inconclusive.
    $rerunId = ''
    $triggerId = ''
    $response = Invoke-Codefresh @("$cfUrl/api/pipelines/sandbox%2Frelease")
    if ($response.ExitCode -eq 0) {
        try {
            $pipeline = ConvertFrom-Json -InputObject $response.Body -AsHashtable -NoEnumerate -Depth 100
            $triggers = @()
            if ($pipeline -is [System.Collections.IDictionary] -and $pipeline['spec'] -is [System.Collections.IDictionary]) {
                $list = $pipeline['spec']['triggers']
                if ($list -is [System.Collections.IDictionary]) {
                    $triggers = @($list.Values)
                }
                elseif ($list -is [System.Collections.IEnumerable] -and $list -isnot [string]) {
                    $triggers = @($list)
                }
            }
            foreach ($trigger in $triggers) {
                if ($null -ne $trigger -and $trigger -isnot [System.Collections.IDictionary]) {
                    throw 'a trigger is not an object'
                }
                if ($null -ne $trigger -and $trigger['name'] -is [string] -and $trigger['name'] -ceq 'main-push') {
                    $id = $trigger['id']
                    $triggerId = if ($null -eq $id -or ($id -is [bool] -and -not $id)) { '' } elseif ($id -is [string]) { $id } else { ConvertTo-Json -InputObject $id -Compress }
                    break
                }
            }
        }
        catch {
            $triggerId = ''
        }
    }
    if ($triggerId) {
        $rerunFile = Join-Path $work 'rerun.json'
        [System.IO.File]::WriteAllText($rerunFile, (ConvertTo-Json -InputObject ([ordered]@{ branch = 'main'; sha = $releaseSha; trigger = $triggerId })) + "`n", [System.Text.UTF8Encoding]::new($false))
        $response = Invoke-Codefresh @('-X', 'POST', '--data-binary', "@$rerunFile", "$cfUrl/api/pipelines/run/sandbox%2Frelease")
        if ($response.ExitCode -eq 0) {
            $rerunId = Get-BuildId $response
        }
    }
    if ($rerunId) {
        Write-Note "sandbox/release rerun queued as build $rerunId"
    }
    else {
        Write-Note 'WARN the sandbox/release rerun was not queued'
    }

    $variables = [ordered]@{
        PLATFORM_RUN_ID         = $script:RunId
        CONFORMANCE_FAILING_SHA = $failingSha
        CONFORMANCE_GREEN_SHA   = $greenSha
        CONFORMANCE_RELEASE_SHA = $releaseSha
    }
    if ($rerunId) {
        $variables['CONFORMANCE_RERUN_BUILD_ID'] = $rerunId
    }
    if ($env:TEST_FILTER) {
        $variables['TEST_FILTER'] = $env:TEST_FILTER
    }
    $runFile = Join-Path $work 'run.json'
    [System.IO.File]::WriteAllText($runFile, (ConvertTo-Json -InputObject ([ordered]@{ branch = 'main'; variables = $variables }) -Depth 20) + "`n", [System.Text.UTF8Encoding]::new($false))
    $url = "$cfUrl/api/pipelines/run/platform-env%2Fconformance"
    $response = Invoke-Codefresh @('-X', 'POST', '--data-binary', "@$runFile", $url)
    if ($response.ExitCode -ne 0) {
        Stop-Arm "queueing platform-env/conformance failed (POST $url, curl exit $($response.ExitCode))"
    }
    Write-Note "platform-env/conformance queued as build $(Get-BuildId $response)"
}
finally {
    if ($entered) {
        Pop-Location
    }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
exit 0
