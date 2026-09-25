#!/usr/bin/env pwsh
#Requires -Version 7.4
# Registry retention of the shared build registry (contract §7.0 "Registry and supply chain",
# Retention; CAP-CF-010). Run nightly by platform-env/registry-retention with the token
# cf-platform-retention (context platform-registry-retention: read, delete, metadata write on
# apps/* and apps-previews/*).
#
# Keeps, per repository under apps/ and apps-previews/:
#   - the newest KeepReleases final SemVer tags (x.y.z);
#   - every tag pinned under gitops/apps/*/envs/** (Kustomize newTag, Helm repository/tag pairs,
#     raw image: references);
#   - apps/sandbox/unsigned:0.0.0-fixture (the admission fixture; never pinned);
#   - every tag that shares a digest with a kept tag (sha-<sha7>), and the cosign referrer tags
#     sha256-<digest>.{sig,att,sbom} of kept digests;
#   - anything younger than the age limit (apps/*: AppsMaxAgeDays, apps-previews/*: PreviewsMaxAgeDays).
# Deletes the rest: a digest goes only when every tag on it goes. Locked tags and manifests
# (write-enabled false, set by supply_chain) are unlocked first, then the manifest is deleted.
#
# Usage:
#   pwsh registry-retention.ps1 -Registry <acr-name>.azurecr.io [-PinsRoot <env repo>] [-DryRun]
#        [-PlanFile plan.json] [-Auth token|aad] [-InventoryFile inventory.json] [-Now <ISO time>]
#   -Auth token (default): ACR_TOKEN_NAME and ACR_TOKEN_PASSWORD (repository-scoped ACR token).
#   -Auth aad: an Azure CLI session (az acr login --expose-token), for read-only dry runs.
#   -InventoryFile: tag inventory as JSON ({"repositories": {"<repo>": [<ACR tag objects>]}})
#   instead of the registry, for rehearsals and the offline tests; implies -DryRun.
#   -PinsRoot: checkout of this repository (gitops/apps/*/envs/** and apps/*.yaml).
# Exit codes: 0 done (or planned), 1 a registry call or a deletion failed, 2 invalid input.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Auth', Justification = 'Read by the registry functions through the script scope.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Registry,
    [string]$PinsRoot = ".",
    [switch]$DryRun,
    [string]$PlanFile = "",
    [ValidateSet("token", "aad")][string]$Auth = "token",
    [string]$InventoryFile = "",
    [int]$KeepReleases = 10,
    [int]$AppsMaxAgeDays = 30,
    [int]$PreviewsMaxAgeDays = 7,
    [string]$Now = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$fixture = "apps/sandbox/unsigned:0.0.0-fixture"
$semver = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$'
$referrer = '^sha256-([0-9a-f]{64})\.(sig|att|sbom)$'
$registryHost = $Registry.TrimEnd('/')
$clock = if ($Now) { [DateTimeOffset]::Parse($Now, [Globalization.CultureInfo]::InvariantCulture) } else { [DateTimeOffset]::UtcNow }
if ($InventoryFile) { $DryRun = $true }

function Write-Note([string]$message) { [Console]::Error.WriteLine("registry-retention: $message") }

# ---------------------------------------------------------------- pins
function Get-RepositoryPath([string]$reference) {
    $match = [regex]::Match($reference, '(?:^|/)(apps(?:-previews)?/[^:@\s"'']+)')
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

function Get-PinSet([string]$root) {
    $pins = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $apps = Join-Path $root "gitops/apps"
    if (-not (Test-Path -LiteralPath $apps)) {
        Write-Note "no gitops/apps under $root; no pins"
        return , $pins
    }
    $files = Get-ChildItem -LiteralPath $apps -Recurse -File -Include *.yaml, *.yml |
        Where-Object { $_.FullName -match '[\\/]envs[\\/]' }
    foreach ($file in $files) {
        $pendingName = $null
        $pendingRepository = $null
        foreach ($raw in [IO.File]::ReadAllLines($file.FullName)) {
            $line = ($raw -replace '\s+#.*$', '')
            if ($line -match '^\s*-?\s*name:\s*["'']?([^"''\s]+)') { $pendingName = Get-RepositoryPath $Matches[1]; continue }
            if ($line -match '^\s*newTag:\s*["'']?([^"''\s]+)' -and $pendingName) { [void]$pins.Add("${pendingName}:$($Matches[1])"); $pendingName = $null; continue }
            if ($line -match '^\s*repository:\s*["'']?([^"''\s]+)') { $pendingRepository = Get-RepositoryPath $Matches[1]; continue }
            if ($line -match '^\s*tag:\s*["'']?([^"''\s]+)' -and $pendingRepository) { [void]$pins.Add("${pendingRepository}:$($Matches[1])"); $pendingRepository = $null; continue }
            if ($line -match '^\s*-?\s*image:\s*["'']?([^"''\s]+)') {
                $reference = $Matches[1] -replace '@sha256:[0-9a-f]+$', ''
                $repository = Get-RepositoryPath $reference
                $tagMatch = [regex]::Match($reference, ':([^:/]+)$')
                if ($repository -and $tagMatch.Success) { [void]$pins.Add("${repository}:$($tagMatch.Groups[1].Value)") }
            }
        }
    }
    return , $pins
}

# ---------------------------------------------------------------- registry access
$script:basic = $null
function Get-BasicCredential {
    if ($script:basic) { return $script:basic }
    if ($Auth -eq "token") {
        if (-not $env:ACR_TOKEN_NAME -or -not $env:ACR_TOKEN_PASSWORD) { throw "ACR_TOKEN_NAME and ACR_TOKEN_PASSWORD are required (context platform-registry-retention)" }
        $pair = "$($env:ACR_TOKEN_NAME):$($env:ACR_TOKEN_PASSWORD)"
    }
    else {
        $name = $registryHost.Split('.')[0]
        $refresh = (& az acr login --name $name --expose-token --output json | ConvertFrom-Json).accessToken
        if (-not $refresh) { throw "az acr login --expose-token returned no token" }
        $pair = "00000000-0000-0000-0000-000000000000:$refresh"
    }
    $script:basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pair))
    return $script:basic
}

function Get-AccessToken([string]$scope) {
    $uri = "https://$registryHost/oauth2/token?service=$([uri]::EscapeDataString($registryHost))&scope=$([uri]::EscapeDataString($scope))"
    $response = Invoke-RestMethod -Uri $uri -Headers @{ Authorization = "Basic $(Get-BasicCredential)" } -Method Get
    return $response.access_token
}

function Invoke-Registry([string]$method, [string]$path, [string]$scope, $body = $null) {
    $headers = @{ Authorization = "Bearer $(Get-AccessToken $scope)" }
    $parameters = @{ Uri = "https://$registryHost$path"; Method = $method; Headers = $headers }
    if ($null -ne $body) { $parameters.Body = ($body | ConvertTo-Json -Compress); $parameters.ContentType = "application/json" }
    return Invoke-WebRequest @parameters
}

function Get-Paged([string]$path, [string]$scope, [string]$property) {
    $items = [System.Collections.Generic.List[object]]::new()
    $next = $path
    while ($next) {
        $response = Invoke-Registry "GET" $next $scope
        $content = $response.Content | ConvertFrom-Json
        foreach ($item in @($content.$property)) { if ($null -ne $item) { $items.Add($item) } }
        $link = [string]($response.Headers["Link"] | Select-Object -First 1)
        $next = if ($link -match '<([^>]+)>;\s*rel="next"') { $Matches[1] } else { $null }
    }
    return , $items
}

# Repository-scoped tokens cannot list the registry catalog, so the repositories come from the
# app descriptors (apps/<app>.yaml deployables[].images -> apps/<app>/<image> and
# apps-previews/<app>/<image>), the pins and the fixture. -Auth aad adds the catalog.
# Repositories of an app removed from apps/ are left to the operator.
function Get-RetentionRepository([string]$root, $pins) {
    $set = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    [void]$set.Add($fixture.Split(':')[0])
    foreach ($pin in $pins) { [void]$set.Add($pin.Split(':')[0]) }
    $descriptors = Join-Path $root "apps"
    if (Test-Path -LiteralPath $descriptors) {
        foreach ($file in Get-ChildItem -LiteralPath $descriptors -File -Filter *.yaml) {
            $app = $null
            $images = [System.Collections.Generic.List[string]]::new()
            $blockIndent = -1
            foreach ($raw in [IO.File]::ReadAllLines($file.FullName)) {
                $line = $raw -replace '\s+#.*$', ''
                if (-not $app -and $line -match '^name:\s*["'']?([a-z0-9][a-z0-9-]*)') { $app = $Matches[1]; continue }
                if ($blockIndent -ge 0) {
                    if ($line -match '^(\s*)-\s*["'']?([A-Za-z0-9._/-]+)' -and $Matches[1].Length -ge $blockIndent) { $images.Add($Matches[2]); continue }
                    $blockIndent = -1
                }
                if ($line -match '^\s*-?\s*images:\s*\[([^\]]*)\]') {
                    foreach ($item in $Matches[1].Split(',')) { $name = $item.Trim().Trim('"', "'"); if ($name) { $images.Add($name) } }
                }
                elseif ($line -match '^(\s*)-?\s*images:\s*$') { $blockIndent = $Matches[1].Length }
            }
            if (-not $app) { continue }
            foreach ($image in $images) {
                [void]$set.Add("apps/$app/$image")
                [void]$set.Add("apps-previews/$app/$image")
            }
        }
    }
    if ($Auth -eq "aad" -and -not $InventoryFile) {
        foreach ($repository in (Get-Paged "/acr/v1/_catalog?n=100" "registry:catalog:*" "repositories")) {
            if ($repository -match '^apps(-previews)?/') { [void]$set.Add($repository) }
        }
    }
    return , @($set)
}

function Get-Inventory([string[]]$repositories) {
    $inventory = [ordered]@{}
    if ($InventoryFile) {
        $document = Get-Content -LiteralPath $InventoryFile -Raw | ConvertFrom-Json
        foreach ($property in $document.repositories.PSObject.Properties) { $inventory[$property.Name] = @($property.Value) }
        return $inventory
    }
    foreach ($repository in $repositories) {
        try {
            $inventory[$repository] = Get-Paged "/acr/v1/$repository/_tags?n=100&orderby=timedesc" "repository:${repository}:metadata_read" "tags"
        }
        catch {
            $status = 0
            if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
            # [VERIFY] ACR answers 404 NAME_UNKNOWN for a repository that was never pushed.
            if ($status -ne 404) { throw }
            Write-Note "$repository does not exist; skipped"
        }
    }
    return $inventory
}

# ---------------------------------------------------------------- plan
function Get-Plan($inventory, $pins) {
    $plan = [System.Collections.Generic.List[object]]::new()
    foreach ($repository in $inventory.Keys) {
        $tags = @($inventory[$repository])
        $isPreview = $repository.StartsWith("apps-previews/", [StringComparison]::Ordinal)
        $maxAge = if ($isPreview) { $PreviewsMaxAgeDays } else { $AppsMaxAgeDays }
        $reasons = @{}
        foreach ($tag in $tags) { $reasons[$tag.name] = [System.Collections.Generic.List[string]]::new() }

        $releases = $tags | Where-Object { $_.name -match $semver } |
            Sort-Object -Descending -Property { [version]($_.name) } | Select-Object -First $KeepReleases
        foreach ($tag in $releases) { $reasons[$tag.name].Add("recent-release") }
        foreach ($tag in $tags) {
            if ($pins.Contains("${repository}:$($tag.name)")) { $reasons[$tag.name].Add("pinned") }
            if ("${repository}:$($tag.name)" -eq $fixture) { $reasons[$tag.name].Add("fixture") }
            $created = [DateTimeOffset]::Parse([string]$tag.createdTime, [Globalization.CultureInfo]::InvariantCulture)
            if (($clock - $created).TotalDays -lt $maxAge) { $reasons[$tag.name].Add("younger-than-$maxAge-days") }
        }
        $keptDigests = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($tag in $tags) { if ($reasons[$tag.name].Count -gt 0 -and $tag.name -notmatch $referrer) { [void]$keptDigests.Add([string]$tag.digest) } }
        foreach ($tag in $tags) {
            if ($reasons[$tag.name].Count -eq 0 -and $keptDigests.Contains([string]$tag.digest)) { $reasons[$tag.name].Add("same-digest") }
            if ($tag.name -match $referrer -and $keptDigests.Contains("sha256:$($Matches[1])")) { $reasons[$tag.name].Add("referrer") }
        }
        # A referrer of a digest that stays but was never tagged is kept too (no subject to judge).
        $deleteDigests = @{}
        foreach ($tag in $tags) {
            if ($reasons[$tag.name].Count -eq 0) {
                if (-not $deleteDigests.ContainsKey([string]$tag.digest)) { $deleteDigests[[string]$tag.digest] = [System.Collections.Generic.List[string]]::new() }
                $deleteDigests[[string]$tag.digest].Add($tag.name)
            }
        }
        $plan.Add([ordered]@{
            repository = $repository
            keep = @($tags | Where-Object { $reasons[$_.name].Count -gt 0 } | ForEach-Object {
                    [ordered]@{ tag = $_.name; digest = $_.digest; reasons = @($reasons[$_.name]) } })
            delete = @($deleteDigests.Keys | Sort-Object | ForEach-Object {
                    [ordered]@{ digest = $_; tags = @($deleteDigests[$_] | Sort-Object) } })
        })
    }
    return , $plan
}

# ---------------------------------------------------------------- main
try {
    $root = (Resolve-Path -LiteralPath $PinsRoot).Path
    $pins = Get-PinSet $root
    $inventory = Get-Inventory (Get-RetentionRepository $root $pins)
}
catch {
    Write-Note "reading failed: $($_.Exception.Message)"
    exit 1
}
$plan = Get-Plan $inventory $pins
$document = [ordered]@{
    schema = 1
    registry = $registryHost
    generatedAt = $clock.ToString("o", [Globalization.CultureInfo]::InvariantCulture)
    dryRun = [bool]$DryRun
    rules = [ordered]@{ keepReleases = $KeepReleases; appsMaxAgeDays = $AppsMaxAgeDays; previewsMaxAgeDays = $PreviewsMaxAgeDays; fixture = $fixture }
    pins = @($pins | Sort-Object)
    repositories = $plan
    totals = [ordered]@{
        keptTags = [int]($plan | ForEach-Object { $_.keep.Count } | Measure-Object -Sum).Sum
        deletedDigests = [int]($plan | ForEach-Object { $_.delete.Count } | Measure-Object -Sum).Sum
    }
}
$json = $document | ConvertTo-Json -Depth 8
if ($PlanFile) {
    $folder = Split-Path -Parent $PlanFile
    if ($folder) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
    Set-Content -LiteralPath $PlanFile -Value $json
}
Write-Note ("{0} repositories, {1} pins, {2} tags kept, {3} digests to delete{4}" -f $plan.Count, $pins.Count, $document.totals.keptTags, $document.totals.deletedDigests, $(if ($DryRun) { " (dry run)" } else { "" }))
foreach ($repository in $plan) {
    foreach ($item in $repository.delete) { Write-Note "delete $($repository.repository)@$($item.digest) [$($item.tags -join ', ')]" }
}
if ($DryRun) { exit 0 }

$failures = 0
foreach ($repository in $plan) {
    $name = $repository.repository
    foreach ($item in $repository.delete) {
        try {
            $scope = "repository:${name}:metadata_read,metadata_write,delete"
            foreach ($tag in $item.tags) {
                [void](Invoke-Registry "PATCH" "/acr/v1/$name/_tags/$tag" $scope @{ writeEnabled = $true; deleteEnabled = $true })
            }
            [void](Invoke-Registry "PATCH" "/acr/v1/$name/_manifests/$($item.digest)" $scope @{ writeEnabled = $true; deleteEnabled = $true })
            [void](Invoke-Registry "DELETE" "/v2/$name/manifests/$($item.digest)" $scope)
            Write-Note "deleted $name@$($item.digest)"
        }
        catch {
            $failures++
            Write-Note "could not delete $name@$($item.digest): $($_.Exception.Message)"
        }
    }
}
exit ([int]($failures -gt 0))
