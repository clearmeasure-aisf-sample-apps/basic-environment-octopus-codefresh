#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    platform-db-backup: back up an app database before a release reaches it (ADR-IR34 decision 1; CAP-OCT-015).

.DESCRIPTION
    octopus/terraform creates step template platform-db-backup from this file; the parameters default to the template
    parameters DbBackup.*. App processes may also inline it between "# >>> octopus/step-templates/db-backup.ps1" and
    "# <<< octopus/step-templates/db-backup.ps1", inside a script block that the step calls with its inputs (offline
    drift test). No dollar-brace or percent-brace sequences (OCL heredoc template syntax) and no hash-brace sequences
    (Octopus variable substitution).

    Where it runs: the shared Kubernetes worker pool k8s-<env> (variable Platform.WorkerPool), whose script pods run in
    namespace octopus-worker-<env> as service account octopus-worker-<env>-scripts (terraform/tier). It creates a Job
    from the tenant chart's CronJob db-backup-<app>-<env> in namespace platform-backup (uat and prod only: tdd databases
    are disposable), labels it platform/trigger=pre-release and waits for it. The Job writes the backup to container
    <app>-<env> of <backup-storage-account-<tier>> as id-db-backup-<tier>.
    Cross-package interface (gitops): that service account needs, in namespace platform-backup, create on jobs, get on
    cronjobs, get and list on jobs and pods, and get on pods/log [VERIFY in the tenant chart or the bootstrap RBAC].

.PARAMETER App
    App slug, ^[a-z][a-z0-9]{2,11}$ (DbBackup.App).

.PARAMETER Environment
    uat or prod (DbBackup.Environment).

.PARAMETER TimeoutSeconds
    Longest wait for the Job; empty means 1800 (DbBackup.TimeoutSeconds).

.OUTPUTS
    Octopus output variables DbBackup.JobName and DbBackup.CompletedAt. A missing CronJob, a failed Job or the timeout
    fails the step (Fail-Step); a failing kubectl call stops it.
#>
[CmdletBinding()]
param(
    [string]$App = $OctopusParameters['DbBackup.App'],
    [string]$Environment = $OctopusParameters['DbBackup.Environment'],
    [string]$TimeoutSeconds = $OctopusParameters['DbBackup.TimeoutSeconds']
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if (-not $TimeoutSeconds) {
    $TimeoutSeconds = '1800'
}
if ($App -cnotmatch '^[a-z][a-z0-9]{2,11}$') {
    Fail-Step "DbBackup.App (-App) '$App' is not an app slug."
}
if ($Environment -ceq 'tdd') {
    Fail-Step 'tdd has no backup CronJob: tdd databases are disposable (ADR-IR34).'
}
if ($Environment -cnotin 'uat', 'prod') {
    Fail-Step "DbBackup.Environment (-Environment) must be uat or prod, not '$Environment'."
}
if ($TimeoutSeconds -cnotmatch '\A[0-9]+\z') {
    Fail-Step "DbBackup.TimeoutSeconds (-TimeoutSeconds) must be a whole number of seconds, not '$TimeoutSeconds'."
}
if (-not (Get-Command kubectl -CommandType Application -ErrorAction SilentlyContinue)) {
    Fail-Step 'kubectl is missing from the step container.'
}

$namespace = 'platform-backup'
$cronJob = "db-backup-$App-$Environment"

# Shows the last lines of the Job's log; a failing kubectl logs only loses the log.
function Show-BackupJobLog {
    $PSNativeCommandUseErrorActionPreference = $false
    kubectl --namespace $namespace logs "job/$job" --tail=100 | Out-Host
}

$PSNativeCommandUseErrorActionPreference = $false
$null = kubectl --namespace $namespace get cronjob $cronJob --output name
$found = $LASTEXITCODE -eq 0
$PSNativeCommandUseErrorActionPreference = $true
if (-not $found) {
    Fail-Step "CronJob $namespace/$cronJob was not found. The tenant chart renders it for uat and prod when the descriptor declares a database."
}

$job = "$cronJob-pre-" + [DateTime]::UtcNow.ToString('yyMMddHHmmss', [Globalization.CultureInfo]::InvariantCulture)
$release = [regex]::Replace([string]$OctopusParameters['Octopus.Release.Number'], '[^A-Za-z0-9._-]', '-')
if ($release.Length -gt 63) {
    $release = $release.Substring(0, 63)
}
if (-not $release) {
    $release = 'none'
}
$null = kubectl --namespace $namespace create job $job "--from=cronjob/$cronJob"
$null = kubectl --namespace $namespace label job $job 'platform/trigger=pre-release' "platform/release=$release" --overwrite
Write-Host "Backup Job $namespace/$job started from CronJob $cronJob for release $release."

$deadline = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() + [long]$TimeoutSeconds
while ($true) {
    $succeeded = (kubectl --namespace $namespace get job $job --output 'jsonpath={.status.succeeded}') -join "`n"
    $failed = (kubectl --namespace $namespace get job $job --output 'jsonpath={.status.conditions[?(@.type=="Failed")].status}') -join "`n"
    if ($succeeded -ceq '1') {
        break
    }
    if ($failed -ceq 'True') {
        Show-BackupJobLog
        Fail-Step "Backup Job $namespace/$job failed; the release does not proceed to the pin."
    }
    if ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() -ge $deadline) {
        Show-BackupJobLog
        Fail-Step "Backup Job $namespace/$job did not complete within $TimeoutSeconds seconds."
    }
    Start-Sleep -Seconds 10
}

$completedAt = (kubectl --namespace $namespace get job $job --output 'jsonpath={.status.completionTime}') -join "`n"
Set-OctopusVariable -name 'DbBackup.JobName' -value $job
Set-OctopusVariable -name 'DbBackup.CompletedAt' -value $completedAt
Write-Highlight "Database of $App in $Environment backed up by Job $namespace/$job at $completedAt."
