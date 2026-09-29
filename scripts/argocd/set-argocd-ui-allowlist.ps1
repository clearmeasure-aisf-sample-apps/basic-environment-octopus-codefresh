#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Writes the Argo CD UI IP allow-list of a tier from Key Vault into the live SecurityPolicy (docs/argocd-ui-access.md).

.DESCRIPTION
    The allow-list of the Argo CD UI is cluster-only: this repository is public, so Git carries a deny-all placeholder
    in gitops/platform/ingress/base/argocd-ui.yaml, and Application platform-ingress ignores /spec/authorization of
    SecurityPolicy argocd/argocd-ui-allowlist (ignoreDifferences with RespectIgnoreDifferences=true). The real list is
    Key Vault secret argocd-ui-allowlist of the tier's platform vault: comma-separated CIDRs, for example
    "198.51.100.7/32,203.0.113.0/28".

    The script
      1. reads the secret and validates every entry (IPv4 prefix /24 or longer, IPv6 /48 or longer; never /0);
      2. reads the live SecurityPolicy through the cluster API;
      3. replaces spec.authorization with defaultAction Deny and one Allow rule for those CIDRs (JSON merge patch,
         guarded by the object's resourceVersion). With -WhatIf the patch is sent with dryRun=All only;
      4. reads the object back and waits until Envoy Gateway reports it Accepted for the new generation.
    It prints the number of entries, never the addresses.

    Authentication. Two scopes are needed: https://vault.azure.net (Key Vault Secrets User on the vault) and the AKS
    server application 6dae42f8-4368-4678-94ff-3960e28e3630 (Azure RBAC on the cluster allowing patch of
    securitypolicies in namespace argocd). With ARM_CLIENT_ID, ARM_CLIENT_SECRET and ARM_TENANT_ID set, the script
    gets both tokens as that service principal; otherwise it asks the Azure CLI session (az account get-access-token).

    Cluster. -Kubeconfig names a kubeconfig written by 'az aks get-credentials' for aks-platform-<tier>; only the
    server address and certificate authority of cluster entry aks-platform-<tier> are read from it (its user entry is
    not used). A running cluster is required: wake a sleeping tier with Octopus runbook env-wake first.

    Exit codes: 0 applied (or validated with -WhatIf), 1 a call failed or the policy was not accepted, 2 invalid input.

.PARAMETER Tier
    nonprod or prod.

.PARAMETER Kubeconfig
    Kubeconfig holding cluster entry aks-platform-<tier> (default: $env:KUBECONFIG, else ~/.kube/config).

.PARAMETER VaultName
    Platform vault of the tier (default: the vault of the tier listed in docs/argocd-ui-access.md).

.EXAMPLE
    az aks get-credentials --resource-group rg-platform-nonprod-aks --name aks-platform-nonprod --file ./kc-nonprod
    pwsh -NoProfile -File scripts/argocd/set-argocd-ui-allowlist.ps1 -Tier nonprod -Kubeconfig ./kc-nonprod -WhatIf
    pwsh -NoProfile -File scripts/argocd/set-argocd-ui-allowlist.ps1 -Tier nonprod -Kubeconfig ./kc-nonprod
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateSet('nonprod', 'prod')][string]$Tier,
    [string]$Kubeconfig = '',
    [string]$VaultName = '',
    [ValidateRange(10, 600)][int]$AcceptTimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

$secretName = 'argocd-ui-allowlist'
$aksServerApp = '6dae42f8-4368-4678-94ff-3960e28e3630'
$policyPath = '/apis/gateway.envoyproxy.io/v1alpha1/namespaces/argocd/securitypolicies/argocd-ui-allowlist'
$fieldManager = 'set-argocd-ui-allowlist'
$vaults = @{ nonprod = 'kv-platform-np-i3aldz'; prod = 'kv-platform-pr-i3aldz' }
$cluster = "aks-platform-$Tier"
if (-not $VaultName) { $VaultName = $vaults[$Tier] }

function Stop-Usage([string]$message) {
    Write-Host "FAIL argocd-ui-allowlist: $message"
    exit 2
}

function Get-AccessToken([string]$scope) {
    if ($env:ARM_CLIENT_ID -and $env:ARM_CLIENT_SECRET -and $env:ARM_TENANT_ID) {
        $body = @{
            grant_type    = 'client_credentials'
            client_id     = $env:ARM_CLIENT_ID
            client_secret = $env:ARM_CLIENT_SECRET
            scope         = "$scope/.default"
        }
        $uri = "https://login.microsoftonline.com/$($env:ARM_TENANT_ID)/oauth2/v2.0/token"
        return (Invoke-RestMethod -Method Post -Uri $uri -Body $body -ContentType 'application/x-www-form-urlencoded').access_token
    }
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        Stop-Usage 'no Azure login: set ARM_CLIENT_ID, ARM_CLIENT_SECRET and ARM_TENANT_ID, or sign in with az login'
    }
    return (az account get-access-token --scope "$scope/.default" --query accessToken --output tsv)
}

function Get-ClusterEndpoint([string]$path, [string]$name) {
    if (-not (Test-Path -LiteralPath $path)) { Stop-Usage "kubeconfig not found: $path" }
    $text = Get-Content -LiteralPath $path -Raw
    foreach ($block in [regex]::Split($text, '(?m)^\s*-\s+cluster:\s*$') | Select-Object -Skip 1) {
        $nameMatch = [regex]::Match($block, '(?m)^\s+name:\s*(\S+)\s*$')
        if (-not $nameMatch.Success -or $nameMatch.Groups[1].Value -ne $name) { continue }
        $server = [regex]::Match($block, '(?m)^\s+server:\s*(\S+)\s*$')
        $ca = [regex]::Match($block, '(?m)^\s+certificate-authority-data:\s*(\S+)\s*$')
        if (-not $server.Success -or -not $ca.Success) { Stop-Usage "cluster entry $name in $path lacks server or certificate-authority-data" }
        return @{ Server = $server.Groups[1].Value.TrimEnd('/'); Ca = [Convert]::FromBase64String($ca.Groups[1].Value) }
    }
    Stop-Usage "kubeconfig $path has no cluster entry named $name"
}

function ConvertTo-AllowList([string]$value) {
    $entries = @($value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($entries.Count -eq 0) { Stop-Usage "secret $secretName in $VaultName is empty" }
    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $entries) {
        $network = [System.Net.IPNetwork]::new([System.Net.IPAddress]::Any, 0)
        if (-not [System.Net.IPNetwork]::TryParse($entry, [ref]$network) -or -not $entry.Contains('/')) {
            Stop-Usage "entry $($result.Count + 1) of $secretName is not a CIDR (address/prefix)"
        }
        $minimum = if ($network.BaseAddress.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork) { 24 } else { 48 }
        if ($network.PrefixLength -lt $minimum) {
            Stop-Usage "entry $($result.Count + 1) of $secretName is wider than /$minimum; the UI must never be open to large ranges"
        }
        $result.Add($network.ToString())
    }
    return , @($result | Sort-Object -Unique)
}

# The cluster API server presents a certificate of the cluster's own CA; trust that CA only, for these calls only.
Add-Type -TypeDefinition @'
using System;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
public static class ClusterHttp {
    public static HttpClient Create(byte[] caPem) {
        var ca = X509Certificate2.CreateFromPem(System.Text.Encoding.ASCII.GetString(caPem));
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) => {
            if (certificate == null) { return false; }
            using var custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.Add(ca);
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (!custom.Build(new X509Certificate2(certificate))) { return false; }
            return (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) == System.Net.Security.SslPolicyErrors.None;
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }
}
'@

function Invoke-Cluster([string]$method, [string]$path, [string]$body = '', [string]$contentType = 'application/json') {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), "$($endpoint.Server)$path")
    $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $clusterToken)
    if ($body) {
        $request.Content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8)
        $request.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new($contentType)
    }
    $response = $http.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        Write-Host "FAIL argocd-ui-allowlist: $method $path returned $([int]$response.StatusCode)"
        exit 1
    }
    return ($text | ConvertFrom-Json -AsHashtable)
}

function Get-AllowedCidr($policy) {
    $authorization = $policy['spec']['authorization']
    if (-not $authorization -or $authorization['defaultAction'] -ne 'Deny') { return $null }
    $cidrs = foreach ($rule in @($authorization['rules'])) {
        if ($rule -and $rule['action'] -eq 'Allow' -and $rule['principal'] -and $rule['principal']['clientCIDRs']) { $rule['principal']['clientCIDRs'] }
    }
    return , @($cidrs | Sort-Object -Unique)
}

function Test-SameSet($left, $right) {
    if ($null -eq $left -or $null -eq $right -or $left.Count -ne $right.Count) { return $false }
    if ($left.Count -eq 0) { return $true }
    return -not (Compare-Object $left $right)
}

if (-not $Kubeconfig) {
    $Kubeconfig = if ($env:KUBECONFIG) { ($env:KUBECONFIG -split [IO.Path]::PathSeparator)[0] } else { Join-Path $HOME '.kube' 'config' }
}
$endpoint = Get-ClusterEndpoint $Kubeconfig $cluster
if ($endpoint.Server -notmatch "^https://$([regex]::Escape($cluster))-") {
    Stop-Usage "cluster entry $cluster points at $($endpoint.Server), not at an AKS API server of $cluster"
}

$vaultToken = Get-AccessToken 'https://vault.azure.net'
$secretUri = "https://$VaultName.vault.azure.net/secrets/$($secretName)?api-version=7.4"
$secret = Invoke-RestMethod -Uri $secretUri -Headers @{ Authorization = "Bearer $vaultToken" }
$allowList = ConvertTo-AllowList $secret.value
Write-Host "argocd-ui-allowlist: $($allowList.Count) entries in $VaultName/$secretName (tier $Tier)"

$clusterToken = Get-AccessToken $aksServerApp
$http = [ClusterHttp]::Create($endpoint.Ca)
$live = Invoke-Cluster 'GET' $policyPath
$current = Get-AllowedCidr $live
if (Test-SameSet $current $allowList) {
    Write-Host "argocd-ui-allowlist: live policy already holds the $($allowList.Count) entries"
}

$patch = @{
    metadata = @{ resourceVersion = $live['metadata']['resourceVersion'] }
    spec     = @{
        authorization = @{
            defaultAction = 'Deny'
            rules         = @(@{ name = 'allow-listed-clients'; action = 'Allow'; principal = @{ clientCIDRs = $allowList } })
        }
    }
} | ConvertTo-Json -Depth 20 -Compress

$query = "?fieldManager=$fieldManager"
if (-not $PSCmdlet.ShouldProcess("SecurityPolicy argocd/argocd-ui-allowlist on $cluster", 'replace spec.authorization')) {
    $null = Invoke-Cluster 'PATCH' "$policyPath$query&dryRun=All" $patch 'application/merge-patch+json'
    Write-Host "PASS argocd-ui-allowlist: dry run accepted by the $cluster API server ($($allowList.Count) entries)"
    exit 0
}
$patched = Invoke-Cluster 'PATCH' "$policyPath$query" $patch 'application/merge-patch+json'
$generation = $patched['metadata']['generation']
$applied = Get-AllowedCidr $patched
if (-not (Test-SameSet $applied $allowList)) {
    Write-Host 'FAIL argocd-ui-allowlist: the patched policy does not hold the Key Vault entries with defaultAction Deny'
    exit 1
}

$deadline = [DateTime]::UtcNow.AddSeconds($AcceptTimeoutSeconds)
do {
    $state = Invoke-Cluster 'GET' $policyPath
    $status = $state['status']
    $ancestors = if ($status -and $status['ancestors']) { @($status['ancestors']) } else { @() }
    $conditions = foreach ($ancestor in $ancestors) {
        if ($ancestor) { @($ancestor['conditions']) | Where-Object { $_['type'] -eq 'Accepted' } }
    }
    $accepted = @($conditions | Where-Object { $_['status'] -eq 'True' -and $_['observedGeneration'] -ge $generation })
    if ($accepted.Count -gt 0) {
        Write-Host "PASS argocd-ui-allowlist: $cluster policy generation $generation Accepted with $($allowList.Count) entries"
        exit 0
    }
    Start-Sleep -Seconds 5
} while ([DateTime]::UtcNow -lt $deadline)
Write-Host "FAIL argocd-ui-allowlist: $cluster policy generation $generation not Accepted within $AcceptTimeoutSeconds s"
exit 1
