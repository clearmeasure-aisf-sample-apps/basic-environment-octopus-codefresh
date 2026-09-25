#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    AKS power state of the app clusters through Azure Resource Manager; dot-sourced by conformance-arm.ps1 and
    conformance-run.ps1.

.DESCRIPTION
    Reads with sp-platform-conformance (context platform-conformance; read access to the clusters). Cluster names
    and groups come from tests/platform.settings.json (Tiers.<tier>); PLATFORM_SETTINGS_FILE, AZURE_SUBSCRIPTION_ID
    and AZURE_TENANT_ID override it as in the harness.

    Functions (after '. aks-power.ps1'):
      Initialize-AksPower -Directory <private dir>
          $true when ARM is readable; $false, with a warning, when the settings hold placeholders or the
          credentials are missing. One Entra ID token request (client credentials, curl --data-urlencode).
      Get-AksPowerState -Tier <tier>
          <powerState>/<provisioningState>, for example Stopped/Succeeded; 'unconfigured' while Tiers.<tier>
          holds placeholders; 'unknown' when ARM is not readable. One GET of the managed cluster.

    The client secret and the token only ever sit in files of the private folder (mode 0600), never on a command
    line. Warnings go to standard error.

    Environment: AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET (platform-conformance); AZURE_SUBSCRIPTION_ID
    and PLATFORM_SETTINGS_FILE (optional).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$script:AksPowerSettings = if ($env:PLATFORM_SETTINGS_FILE) {
    $env:PLATFORM_SETTINGS_FILE
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..' '..' 'tests' 'platform.settings.json'))
}
$script:AksPowerDirectory = ''
$script:AksPowerSubscription = ''

function Write-AksPowerWarning([string] $Message) {
    [Console]::Error.WriteLine("aks-power: WARN $Message")
}

# A new file readable only by its owner (mode 0600 outside Windows).
function Write-AksPowerFile([string] $Path, [string] $Content) {
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

# jq -r '<path> // empty' over JSON text: '' for a missing, null or false value, and when the text is not JSON or the
# path runs through a value that is not an object.
function Get-AksPowerValue([string] $Json, [string[]] $Path) {
    try {
        $node = ConvertFrom-Json -InputObject $Json -AsHashtable -NoEnumerate -Depth 100
        foreach ($name in $Path) {
            if ($null -eq $node) {
                return ''
            }
            if ($node -isnot [System.Collections.IDictionary]) {
                return ''
            }
            $node = $node[$name]
        }
    }
    catch {
        return ''
    }
    if ($null -eq $node -or ($node -is [bool] -and -not $node)) {
        return ''
    }
    if ($node -is [string]) {
        return $node
    }
    if ($node -is [bool]) {
        return 'true'
    }
    return ConvertTo-Json -InputObject $node -Depth 100 -Compress
}

function Read-AksPowerSetting {
    try {
        return [System.IO.File]::ReadAllText($script:AksPowerSettings)
    }
    catch {
        return ''
    }
}

function Initialize-AksPower {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string] $Directory
    )

    if (-not (Test-Path -LiteralPath $script:AksPowerSettings -PathType Leaf)) {
        Write-AksPowerWarning "no $script:AksPowerSettings"
        return $false
    }
    $settings = Read-AksPowerSetting
    $tenant = if ($env:AZURE_TENANT_ID) { $env:AZURE_TENANT_ID } else { Get-AksPowerValue $settings 'AzureTenantId' }
    $subscription = if ($env:AZURE_SUBSCRIPTION_ID) { $env:AZURE_SUBSCRIPTION_ID } else { Get-AksPowerValue $settings 'AzureSubscriptionId' }
    if (-not $tenant -or -not $subscription -or -not $env:AZURE_CLIENT_ID -or -not $env:AZURE_CLIENT_SECRET) {
        Write-AksPowerWarning 'no Azure credentials or IDs (platform-conformance, settings)'
        return $false
    }
    if ("$tenant$subscription$($env:AZURE_CLIENT_ID)" -match '<[\s\S]*>') {
        Write-AksPowerWarning 'Azure settings hold placeholders'
        return $false
    }

    $secretFile = "$Directory/azure-secret"
    Write-AksPowerFile $secretFile $env:AZURE_CLIENT_SECRET
    try {
        $PSNativeCommandUseErrorActionPreference = $false
        $lines = & curl -fsS --max-time 60 -X POST "https://login.microsoftonline.com/$tenant/oauth2/v2.0/token" `
            --data-urlencode grant_type=client_credentials --data-urlencode "client_id=$($env:AZURE_CLIENT_ID)" `
            --data-urlencode "client_secret@$secretFile" `
            --data-urlencode scope=https://management.azure.com/.default
        $token = if ($LASTEXITCODE -eq 0) { Get-AksPowerValue (@($lines) -join "`n") 'access_token' } else { '' }
    }
    finally {
        Remove-Item -LiteralPath $secretFile -Force -ErrorAction SilentlyContinue
    }
    if (-not $token) {
        Write-AksPowerWarning 'Entra ID token request failed'
        return $false
    }
    Write-AksPowerFile "$Directory/arm-headers" "Authorization: Bearer $token`n"
    $script:AksPowerDirectory = $Directory
    $script:AksPowerSubscription = $subscription
    return $true
}

# One field of '.properties' as jq's '"\(.properties.<path> // "unknown")"' prints it; throws where jq fails.
function Get-AksPowerField([object] $Cluster, [string[]] $Path) {
    $node = $Cluster
    foreach ($key in @('properties') + $Path) {
        if ($null -eq $node) {
            break
        }
        if ($node -isnot [System.Collections.IDictionary]) {
            throw 'not an object'
        }
        $node = $node[$key]
    }
    if ($null -eq $node -or ($node -is [bool] -and -not $node)) {
        return 'unknown'
    }
    if ($node -is [string]) {
        return $node
    }
    return ConvertTo-Json -InputObject $node -Depth 100 -Compress
}

function Get-AksPowerState {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string] $Tier
    )

    if (-not $script:AksPowerDirectory) {
        return 'unknown'
    }
    $settings = Read-AksPowerSetting
    $group = Get-AksPowerValue $settings 'Tiers', $Tier, 'ResourceGroup'
    $name = Get-AksPowerValue $settings 'Tiers', $Tier, 'ClusterName'
    if ("$group$name" -eq '' -or "$group$name" -match '<[\s\S]*>') {
        return 'unconfigured'
    }
    $PSNativeCommandUseErrorActionPreference = $false
    $lines = & curl -fsS --max-time 60 -H "@$($script:AksPowerDirectory)/arm-headers" `
        "https://management.azure.com/subscriptions/$($script:AksPowerSubscription)/resourceGroups/$group/providers/Microsoft.ContainerService/managedClusters/${name}?api-version=2024-10-01"
    if ($LASTEXITCODE -ne 0) {
        return 'unknown'
    }
    # jq -r '"\(.properties.powerState.code // "unknown")/\(.properties.provisioningState // "unknown")"': an empty
    # body prints nothing, and any error (not JSON, a path through a value that is not an object) prints nothing, which
    # the caller reads as 'unknown'.
    $body = @($lines) -join "`n"
    if ([string]::IsNullOrWhiteSpace($body)) {
        return 'unknown'
    }
    try {
        $cluster = ConvertFrom-Json -InputObject $body -AsHashtable -NoEnumerate -Depth 100
        $code = Get-AksPowerField $cluster 'powerState', 'code'
        $provisioning = Get-AksPowerField $cluster 'provisioningState'
        return "$code/$provisioning"
    }
    catch {
        return 'unknown'
    }
}
