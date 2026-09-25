#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Step guard of workorders/preview: a preview builds only for a pull request that carries the preview label and
    comes from a branch of the application repo.

.DESCRIPTION
    Runs in the application checkout (the pull request head) with the step's environment and exports, for the
    later steps:
      PREVIEW_BUILD               true when the pull request carries the label `preview` and comes from the same
                                  repository, else false (the later steps then do not run).
      VERSION, BUILD_BUILDNUMBER  version.ps1.

    Label: CF_PULL_REQUEST_LABELS is a JSON array (of names, or of objects with a name) or a comma-separated list
    [VERIFY format]; both are accepted, and only an exact `preview` label counts. The JSON is parsed strictly
    (System.Text.Json), so text that is not valid JSON counts as a comma-separated list.
    Same repository: the head branch CF_PULL_REQUEST_HEAD_BRANCH must exist in the application repo and point at
    the triggering commit CF_REVISION.

    Exports use cf_export, which Codefresh puts on PATH in every freestyle step; the value travels in the
    environment, never on a command line. Without cf_export the value is appended to
    ${CF_VOLUME_PATH}/env_vars_to_export.

    Usage (Codefresh step guard, in the application checkout):
      pwsh -NoProfile -File <env-repo>/codefresh/apps/workorders/scripts/preview-guard.ps1

.NOTES
    Exit codes: 0 decided (build or not); 1 failed (version.ps1 failed, an export failed).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Exit-Failure([string] $Message) {
    [Console]::Error.WriteLine("preview-guard.ps1: $Message")
    exit 1
}

# Exports a variable to the later steps: cf_export NAME when Codefresh put it on PATH (the value in the environment),
# else a NAME=value line in ${CF_VOLUME_PATH}/env_vars_to_export (the file cf_export writes). Logs the path, never the value.
function Export-CodefreshVariable([string] $Name, [string] $Value) {
    $cfExport = Get-Command -Name cf_export -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cfExport) {
        [Environment]::SetEnvironmentVariable($Name, $Value)
        & $cfExport.Source $Name
        Write-Host "preview-guard.ps1: exported $Name with cf_export"
        return
    }
    Add-Content -LiteralPath (Join-Path $env:CF_VOLUME_PATH 'env_vars_to_export') -Value "$Name=$Value"
    Write-Host "preview-guard.ps1: exported $Name to env_vars_to_export (no cf_export on PATH)"
}

# The label texts: the elements of a JSON array (an object's name), else the comma-separated items.
function Get-LabelText([string] $Raw) {
    $document = $null
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($Raw)
    }
    catch {
        $document = $null
    }
    if ($null -ne $document -and $document.RootElement.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($element in $document.RootElement.EnumerateArray()) {
            $value = $element
            if ($element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
                $name = [System.Text.Json.JsonElement]::new()
                $value = if ($element.TryGetProperty('name', [ref] $name)) { $name } else { $null }
            }
            if ($null -eq $value) {
                'null'
            }
            elseif ($value.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
                $value.GetString()
            }
            else {
                $value.GetRawText()
            }
        }
        return
    }
    $Raw -split ','
}

# true when one label, on its own line and without surrounding white space, is exactly `preview`.
function Test-PreviewLabel([string] $Raw) {
    foreach ($text in @(Get-LabelText $Raw)) {
        foreach ($line in ([string] $text) -split "`n") {
            if ($line.Trim([char[]] " `t`n`v`f`r") -ceq 'preview') {
                return $true
            }
        }
    }
    return $false
}

# true when the head branch exists in the application repo and points at the triggering commit.
function Test-SameRepository {
    $PSNativeCommandUseErrorActionPreference = $false
    $headBranch = $env:CF_PULL_REQUEST_HEAD_BRANCH
    if (-not $headBranch) {
        return $false
    }
    git fetch --no-tags --quiet origin "refs/heads/$headBranch" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        return $false
    }
    return ((git rev-parse FETCH_HEAD) -join "`n") -ceq $env:CF_REVISION
}

function Format-Flag([bool] $Value) {
    if ($Value) { 'true' } else { 'false' }
}

$hasLabel = Test-PreviewLabel $(if ($env:CF_PULL_REQUEST_LABELS) { $env:CF_PULL_REQUEST_LABELS } else { '' })
$sameRepository = Test-SameRepository
$build = $hasLabel -and $sameRepository
Write-Host "PR #$($env:CF_PULL_REQUEST_NUMBER): preview label $(Format-Flag $hasLabel), same repository $(Format-Flag $sameRepository), build $(Format-Flag $build)"

$version = @(& (Join-Path $PSScriptRoot 'version.ps1')) -join "`n"
if ($LASTEXITCODE -ne 0) {
    Exit-Failure 'version.ps1 failed'
}
Export-CodefreshVariable 'PREVIEW_BUILD' (Format-Flag $build)
Export-CodefreshVariable 'VERSION' $version
Export-CodefreshVariable 'BUILD_BUILDNUMBER' $version
exit 0
