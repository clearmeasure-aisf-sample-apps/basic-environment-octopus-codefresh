#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Steps image_reuse, supply_chain and supply_chain_reuse of a release pipeline: the registry token as a step-local
    Docker config, then supply-chain.ps1.

.DESCRIPTION
    Registry credentials (ADR-IR10): a freestyle step cannot use a registry integration, so the secret context
    platform-registry carries the registry (ACR_REGISTRY) and the cf-apps-release token (ACR_TOKEN_NAME and
    ACR_TOKEN_PASSWORD). The script writes them as a Docker config in a private temporary folder (DOCKER_CONFIG for
    every tool it starts), removed when the script ends. Without them the step fails closed. The tags are VERSION and
    sha-CF_SHORT_REVISION.

      image_reuse         supply-chain.ps1 -CheckReuse over the repositories; exports IMAGES_REUSED=true when every tag
                          is already locked (a re-run of a release for the same commit), else false. A re-run then
                          skips the image builds and supply_chain, whose push to a locked tag would fail.
      supply_chain        crane digest of each repository's VERSION tag, then supply-chain.ps1 with those digests: SBOM
                          and provenance attestations, tag lock; the evidence goes to ${ARTIFACTS_DIR}/supply-chain.
      supply_chain_reuse  the reuse check again; fails unless it answers reuse, then names the digests the handoff
                          releases.

    Exports use cf_export, which Codefresh puts on PATH in every freestyle step; the value travels in the environment,
    never on a command line. Without cf_export the value is appended to ${CF_VOLUME_PATH}/env_vars_to_export.

    Usage (Codefresh steps image_reuse, supply_chain, supply_chain_reuse):
      pwsh -NoProfile -File <env-repo>/codefresh/apps/<app>/scripts/supply-chain-step.ps1 -Step <step> -Repository <repo>[,<repo>...]

.PARAMETER Step
    image_reuse, supply_chain or supply_chain_reuse.

.PARAMETER Repository
    The image repositories of the release, for example apps/<app>/web (a comma-separated list from a shell).

.NOTES
    Exit codes: 0 done; 1 failed (a context variable missing, the reuse check failed or did not answer reuse, a
    failing tool).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('image_reuse', 'supply_chain', 'supply_chain_reuse', IgnoreCase = $false)]
    [string] $Step,
    [Parameter(Mandatory)]
    [string[]] $Repository
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$Repository = @($Repository | ForEach-Object { $_ -split ',' })
$supplyChain = Join-Path $PSScriptRoot 'supply-chain.ps1'

function Exit-Failure([string] $Message) {
    [Console]::Error.WriteLine("${Step}: $Message")
    exit 1
}

# Exports a variable to the later steps: cf_export NAME when Codefresh put it on PATH (the value in the environment),
# else a NAME=value line in ${CF_VOLUME_PATH}/env_vars_to_export (the file cf_export writes). Logs the path, never the value.
function Export-CodefreshVariable([string] $Name, [string] $Value) {
    $cfExport = Get-Command -Name cf_export -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cfExport) {
        [Environment]::SetEnvironmentVariable($Name, $Value)
        & $cfExport.Source $Name
        Write-Host "supply-chain-step.ps1: exported $Name with cf_export"
        return
    }
    Add-Content -LiteralPath (Join-Path $env:CF_VOLUME_PATH 'env_vars_to_export') -Value "$Name=$Value"
    Write-Host "supply-chain-step.ps1: exported $Name to env_vars_to_export (no cf_export on PATH)"
}

# The reuse check of supply-chain.ps1 (reuse or build); fails the step when the check fails.
function Get-ReuseDecision {
    try {
        $decision = & $supplyChain -Registry $env:ACR_REGISTRY -CheckReuse -Repository $Repository -Tag $tags
        $checked = $LASTEXITCODE -eq 0
    }
    catch {
        $checked = $false
    }
    if (-not $checked) {
        Exit-Failure 'the reuse check failed'
    }
    return (@($decision) -join "`n")
}

foreach ($name in @('ACR_REGISTRY', 'ACR_TOKEN_NAME', 'ACR_TOKEN_PASSWORD')) {
    if (-not [Environment]::GetEnvironmentVariable($name)) {
        Exit-Failure "$name is not set (context platform-registry)"
    }
}
$tags = @($env:VERSION, "sha-$($env:CF_SHORT_REVISION)")

$dockerConfig = [System.IO.Directory]::CreateTempSubdirectory('docker-config-').FullName
$env:DOCKER_CONFIG = $dockerConfig
try {
    $auth = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes("$($env:ACR_TOKEN_NAME):$($env:ACR_TOKEN_PASSWORD)"))
    $config = [ordered]@{ auths = [ordered]@{ $env:ACR_REGISTRY = [ordered]@{ auth = $auth } } }
    [System.IO.File]::WriteAllText((Join-Path $dockerConfig 'config.json'), ($config | ConvertTo-Json -Depth 20) + "`n", [System.Text.UTF8Encoding]::new($false))
    $auth = $null
    $config = $null

    switch ($Step) {
        'image_reuse' {
            $decision = Get-ReuseDecision
            Write-Host "image_reuse: $decision"
            Export-CodefreshVariable 'IMAGES_REUSED' $(if ($decision -ceq 'reuse') { 'true' } else { 'false' })
        }
        'supply_chain' {
            $images = foreach ($repo in $Repository) {
                $digest = (crane digest "$($env:ACR_REGISTRY)/${repo}:$($env:VERSION)") -join "`n"
                "$($env:ACR_REGISTRY)/$repo@$digest"
            }
            & $supplyChain -Registry $env:ACR_REGISTRY -Image @($images) -Tag $tags -Out "$($env:ARTIFACTS_DIR)/supply-chain"
            if ($LASTEXITCODE -ne 0) {
                exit 1
            }
        }
        'supply_chain_reuse' {
            $decision = Get-ReuseDecision
            if ($decision -cne 'reuse') {
                Exit-Failure "IMAGES_REUSED is '$($env:IMAGES_REUSED)' but the reuse check answered '$decision'"
            }
            # The digests are for the log only: a failing lookup prints an empty digest, as before.
            $PSNativeCommandUseErrorActionPreference = $false
            foreach ($repo in $Repository) {
                $digest = (crane digest "$($env:ACR_REGISTRY)/${repo}:$($env:VERSION)") -join "`n"
                Write-Host "supply_chain_reuse: reusing $($env:ACR_REGISTRY)/$repo@$digest"
            }
        }
    }
}
finally {
    Remove-Item -LiteralPath $dockerConfig -Recurse -Force -ErrorAction SilentlyContinue
}
exit 0
