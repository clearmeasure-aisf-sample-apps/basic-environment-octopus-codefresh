#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Supply-chain evidence for release images (ADR-D11, contract §7.5): SBOM and provenance attestations, keyless, and
    the tag lock; or the reuse check of a re-run.

.DESCRIPTION
      1. SBOM: Syft (SPDX JSON) per image, attached with
         `cosign attest --type spdxjson` (keyless).
      2. Provenance: a SLSA v1 predicate per image, attached with
         `cosign attest --type slsaprovenance1` (keyless). It is step-authored:
         the pipeline step, not a hardened isolated builder, writes it, so it
         claims SLSA Build L2 at most. Kyverno keeps provenance in Audit (ADR-D11).
      3. Tag lock: every -Tag of every image gets write-enabled=false in ACR,
         so no later push can move a released tag (ADR-C7, ADR-D11).
    Image signing itself happens in the Codefresh build steps (`cosign.sign: true`).

    Inputs are digests: -Image <registry>/<repo>@sha256:<digest> (one or more).
    All attestation work targets the digest; tags are only locked.

    Credentials:
      - Registry: a Docker config at $DOCKER_CONFIG/config.json with an `auths`
        entry for -Registry (repository-scoped ACR token, §5.2). Syft, cosign
        and the tag lock all use it. In a release pipeline supply-chain-step.ps1
        writes it from ACR_TOKEN_NAME and ACR_TOKEN_PASSWORD of the secret context
        platform-registry (ADR-IR10; token cf-apps-release).
      - Sigstore: a fresh Codefresh OIDC ID token per attestation (they expire
        after 5 minutes, E15), requested exactly as the marketplace step
        obtain-oidc-id-token 1.2.3 does: GET $CF_OIDC_REQUEST_URL?audience=sigstore
        with header "Authorization: $CF_OIDC_REQUEST_TOKEN" -> .id_token
        (codefresh-io/steps incubating/obtain-oidc-id-token/step.yaml).
        [VERIFY] both variables are injected into plain freestyle steps.
    No token, password or credential is ever printed or put on a command line
    (docs/scripting.md, "Secrets"): curl reads the OIDC request header from its
    configuration on standard input, cosign reads each ID token from a private
    file (--identity-token accepts a path), and az reads the registry token's
    password from standard input (--password @-).

    Provenance records two sources: the application commit that was built (the
    triggering repo at CF_REVISION) and the environment repo commit whose pipeline
    YAML and scripts built it (the checkout that holds this script,
    codefresh/apps/<app>/scripts/; PIPELINE_YAML overrides the release YAML path).

    Usage: supply-chain.ps1 -Registry <acr-name>.azurecr.io -Image <ref@digest>[,...] -Tag <tag>[,...] -Out <dir> [-NoLock]
           supply-chain.ps1 -Registry <acr-name>.azurecr.io -CheckReuse -Repository <repo>[,...] -Tag <tag>[,...]
    Lists given from a shell are comma-separated.

    Reuse check (-CheckReuse), run before the image builds so that a re-run of a release for the
    same commit does not rebuild images whose tags are already locked (a push to a locked tag fails).
    Prints one word on stdout:
      reuse  every -Repository holds every -Tag and each tag is locked (write-enabled false). The lock is
             the last act of a completed supply chain, so the images are signed and attested.
      build  no tag exists yet, or a tag exists but is still unlocked (an earlier run stopped before
             the lock; a new push may overwrite it).
    A mixed state (some tags locked, others missing or unlocked) fails: no push can repair it.
    Requires: syft, cosign, az and curl; git (optional) names the pipeline commit.
    The reuse check requires only az.

.PARAMETER Registry
    The registry login server, <acr-name>.azurecr.io.

.PARAMETER Image
    Image references by digest, on the registry.

.PARAMETER Tag
    The tags to lock (attestation) or to check (-CheckReuse).

.PARAMETER Out
    The folder of the evidence (SBOM and provenance files).

.PARAMETER NoLock
    Attest without locking the tags.

.PARAMETER CheckReuse
    Answer reuse or build for -Repository and -Tag instead of attesting.

.PARAMETER Repository
    The repositories of the reuse check, for example apps/<app>/web.

.OUTPUTS
    With -CheckReuse, reuse or build; otherwise nothing (the evidence files). The log goes to standard error.

.NOTES
    Exit codes: 0 done; 1 failed (missing input, tool or credential, no id_token, a failing tool, a mixed reuse state).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Registry,
    [string[]] $Image = @(),
    [string[]] $Tag = @(),
    [string] $Out = '',
    [switch] $NoLock,
    [switch] $CheckReuse,
    [string[]] $Repository = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$envRepoUrl = 'https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh'
# This script lives at codefresh/apps/<app>/scripts/; the release YAML next to it names the build.
$appName = Split-Path -Leaf (Split-Path -Parent $PSScriptRoot)
$pipelineYaml = if ($env:PIPELINE_YAML) { $env:PIPELINE_YAML } else { "codefresh/apps/$appName/pipelines/release.yml" }
$dockerConfig = "$(if ($env:DOCKER_CONFIG) { $env:DOCKER_CONFIG } else { "$($env:HOME)/.docker" })/config.json"
$registryName = $Registry.Split('.')[0]

# Lists from a shell arrive as one comma-separated string.
$Image = @($Image | ForEach-Object { $_ -split ',' })
$Tag = @($Tag | ForEach-Object { $_ -split ',' })
$Repository = @($Repository | ForEach-Object { $_ -split ',' })

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("supply-chain.ps1: $Message")
}

function Exit-Failure([string] $Message) {
    Write-Note $Message
    exit 1
}

function Assert-Tool([string] $Name, [string] $Message) {
    if (-not (Get-Command -Name $Name -CommandType Application -ErrorAction SilentlyContinue)) {
        Exit-Failure $Message
    }
}

# The user and password of the registry from the Docker config; never printed.
function Get-RegistryCredential {
    $config = [System.IO.File]::ReadAllText($dockerConfig) | ConvertFrom-Json -AsHashtable
    $auth = $null
    if ($config -is [System.Collections.IDictionary] -and $config['auths'] -is [System.Collections.IDictionary] -and $config['auths'][$Registry] -is [System.Collections.IDictionary]) {
        $auth = $config['auths'][$Registry]['auth']
    }
    if (-not $auth) {
        Exit-Failure "no auths entry for $Registry in $dockerConfig"
    }
    $pair = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String([string] $auth))
    $colon = $pair.IndexOf(':')
    if ($colon -lt 0) {
        return [pscustomobject]@{ User = $pair; Password = $pair }
    }
    return [pscustomobject]@{ User = $pair.Substring(0, $colon); Password = $pair.Substring($colon + 1) }
}

# A Sigstore-audience ID token from the Codefresh OIDC provider; the request token travels only in the header.
function Get-SigstoreToken {
    foreach ($name in @('CF_OIDC_REQUEST_URL', 'CF_OIDC_REQUEST_TOKEN')) {
        if (-not [Environment]::GetEnvironmentVariable($name)) {
            Exit-Failure "$name is not set"
        }
    }
    $PSNativeCommandUseErrorActionPreference = $false
    # The header travels in curl's configuration on standard input, never in the arguments.
    $header = "Authorization: $($env:CF_OIDC_REQUEST_TOKEN)".Replace('\', '\\').Replace('"', '\"')
    $response = "header = `"$header`"" | curl --config - -fsS "$($env:CF_OIDC_REQUEST_URL)?audience=sigstore"
    $token = $null
    try {
        $document = (@($response) -join "`n") | ConvertFrom-Json -AsHashtable
        if ($document -is [System.Collections.IDictionary]) {
            $token = $document['id_token']
        }
    }
    catch {
        $token = $null
    }
    if ($null -eq $token -or "$token" -eq '' -or "$token" -ceq 'null') {
        Exit-Failure 'the Codefresh OIDC provider returned no id_token'
    }
    return [string] $token
}

# Runs one cosign attestation with a fresh ID token in a private file (mode 0600): cosign's --identity-token takes the
# token or a path to a file holding it, so only the path is on the command line. The file is removed afterwards.
function Invoke-CosignAttest([string] $Type, [string] $Predicate, [string] $Reference) {
    $token = Get-SigstoreToken
    $folder = if ($env:TMPDIR) { $env:TMPDIR } else { [System.IO.Path]::GetTempPath() }
    $tokenFile = Join-Path $folder "sigstore-$([Guid]::NewGuid().ToString('N')).token"
    $options = [System.IO.FileStreamOptions]::new()
    $options.Mode = [System.IO.FileMode]::CreateNew
    $options.Access = [System.IO.FileAccess]::Write
    if (-not $IsWindows) {
        $options.UnixCreateMode = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite
    }
    try {
        $stream = [System.IO.FileStream]::new($tokenFile, $options)
        try {
            $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($token)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally {
            $stream.Dispose()
        }
        $token = $null
        cosign attest --yes --type $Type --predicate $Predicate `
            --identity-token $tokenFile $Reference
    }
    finally {
        Remove-Item -LiteralPath $tokenFile -Force -ErrorAction SilentlyContinue
    }
}

# Writes a step-authored SLSA v1 provenance predicate for one image digest.
function Write-Provenance([string] $Reference, [string] $File, [string] $PipelineCommit) {
    $now = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    $repo = "https://github.com/$(if ($env:CF_REPO_OWNER) { $env:CF_REPO_OWNER } else { 'clearmeasure-aisf-sample-apps' })/$(if ($env:CF_REPO_NAME) { $env:CF_REPO_NAME } else { '20260923-001' })"
    $revision = if ($env:CF_REVISION) { $env:CF_REVISION } else { 'unknown' }
    $branch = if ($env:CF_BRANCH) { $env:CF_BRANCH } else { 'unknown' }
    $pipeline = if ($env:CF_PIPELINE_NAME) { $env:CF_PIPELINE_NAME } else { "$appName/release" }
    $predicate = [ordered]@{
        buildDefinition = [ordered]@{
            buildType            = "$envRepoUrl/$pipelineYaml@v1"
            externalParameters   = [ordered]@{
                repository = $repo
                ref        = "refs/heads/$branch"
                revision   = $revision
                pipeline   = $pipeline
                workflow   = [ordered]@{ repository = $envRepoUrl; ref = 'refs/heads/main'; path = $pipelineYaml }
                version    = $(if ($env:VERSION) { $env:VERSION } else { 'unknown' })
            }
            internalParameters   = [ordered]@{
                provenanceAuthor    = "step-authored (codefresh/apps/$appName/scripts/supply-chain.ps1)"
                slsaBuildLevelClaim = 'L2 at most'
            }
            resolvedDependencies = @(
                [ordered]@{ uri = "git+$repo@refs/heads/$branch"; digest = [ordered]@{ gitCommit = $revision } }
                [ordered]@{ uri = "git+$envRepoUrl@refs/heads/main"; digest = [ordered]@{ gitCommit = $PipelineCommit } }
            )
        }
        runDetails      = [ordered]@{
            builder    = [ordered]@{ id = "https://g.codefresh.io/pipelines/$pipeline" }
            metadata   = [ordered]@{ invocationId = $(if ($env:CF_BUILD_URL) { $env:CF_BUILD_URL } else { '' }); startedOn = $now; finishedOn = $now }
            byproducts = @(
                [ordered]@{ name = 'image'; uri = $Reference }
                [ordered]@{ name = 'codefresh-build-id'; content = $(if ($env:CF_BUILD_ID) { $env:CF_BUILD_ID } else { 'local' }) }
            )
        }
    }
    $full = [System.IO.Path]::GetFullPath($File, $PWD.ProviderPath)
    [System.IO.File]::WriteAllText($full, ($predicate | ConvertTo-Json -Depth 20) + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Invoke-ReuseCheck {
    if ($Repository.Count -eq 0) {
        Exit-Failure 'check-reuse needs at least one -Repository'
    }
    if ($Tag.Count -eq 0) {
        Exit-Failure 'check-reuse needs at least one -Tag'
    }
    if ($Image.Count -ne 0) {
        Exit-Failure 'check-reuse takes -Repository, not -Image'
    }
    Assert-Tool 'az' 'az is required'
    if (-not (Test-Path -LiteralPath $dockerConfig -PathType Leaf)) {
        Exit-Failure "no registry credentials at $dockerConfig (ACR_TOKEN_NAME and ACR_TOKEN_PASSWORD, ADR-IR10)"
    }
    $credential = Get-RegistryCredential
    $locked = 0
    $unlocked = 0
    $missing = 0
    $errorFile = "$(if ($env:TMPDIR) { $env:TMPDIR } else { '/tmp' })/check-reuse.err"
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        foreach ($repo in $Repository) {
            foreach ($tagName in $Tag) {
                # Data-plane read with the repository-scoped token (metadata read).
                # --password @- reads the password from standard input (Azure CLI expands @- in any argument value).
                $attributes = $credential.Password | az acr repository show `
                    --name $registryName `
                    --image "${repo}:$tagName" `
                    --username $credential.User `
                    --password '@-' `
                    --query changeableAttributes.writeEnabled `
                    --output tsv 2> $errorFile
                if ($LASTEXITCODE -eq 0) {
                    $value = (@($attributes) -join "`n").TrimEnd("`n")
                    switch -CaseSensitive ($value.ToLowerInvariant()) {
                        'false' {
                            $locked++
                            Write-Note "check-reuse: ${repo}:$tagName exists and is locked"
                        }
                        'true' {
                            $unlocked++
                            Write-Note "check-reuse: ${repo}:$tagName exists and is not locked"
                        }
                        default {
                            Exit-Failure "check-reuse: unexpected writeEnabled '$value' for ${repo}:$tagName"
                        }
                    }
                }
                elseif ([System.IO.File]::ReadAllText($errorFile) -match 'not ?found|MANIFEST_UNKNOWN|NAME_UNKNOWN|does not exist') {
                    $missing++
                    Write-Note "check-reuse: ${repo}:$tagName does not exist"
                }
                else {
                    [Console]::Error.Write([System.IO.File]::ReadAllText($errorFile))
                    Exit-Failure "check-reuse: cannot read ${repo}:$tagName; failing closed"
                }
            }
        }
    }
    finally {
        $credential = $null
        Remove-Item -LiteralPath $errorFile -Force -ErrorAction SilentlyContinue
    }
    $total = $Repository.Count * $Tag.Count
    if ($locked -eq $total) {
        Write-Output 'reuse'
    }
    elseif ($locked -eq 0) {
        Write-Output 'build'
    }
    else {
        Exit-Failure "check-reuse: $locked of $total tags are locked, $unlocked unlocked, $missing missing; a mixed state needs an operator (no push can overwrite a locked tag)"
    }
}

function Invoke-Attestation([string] $Evidence, [bool] $Lock) {
    if (-not $Evidence) {
        Exit-Failure '-Out is required'
    }
    if ($Image.Count -eq 0) {
        Exit-Failure 'at least one -Image <ref@sha256:digest> is required'
    }
    if ($Lock -and $Tag.Count -eq 0) {
        Exit-Failure '-Tag is required unless -NoLock'
    }
    foreach ($tool in @('syft', 'cosign', 'curl')) {
        Assert-Tool $tool "$tool is required"
    }
    if ($Lock) {
        Assert-Tool 'az' 'az is required for the tag lock'
    }
    if (-not (Test-Path -LiteralPath $dockerConfig -PathType Leaf)) {
        Exit-Failure "no registry credentials at $dockerConfig (ACR_TOKEN_NAME and ACR_TOKEN_PASSWORD, ADR-IR10)"
    }
    $null = New-Item -ItemType Directory -Force -Path $Evidence

    # The environment repo commit that supplied the pipeline definition and scripts.
    $pipelineCommit = 'unknown'
    try {
        $PSNativeCommandUseErrorActionPreference = $false
        $head = git -C $PSScriptRoot rev-parse HEAD 2> $null
        if ($LASTEXITCODE -eq 0) {
            $pipelineCommit = @($head) -join "`n"
        }
    }
    catch {
        $pipelineCommit = 'unknown'
    }
    finally {
        $PSNativeCommandUseErrorActionPreference = $true
    }

    $index = 0
    foreach ($reference in $Image) {
        if (-not ($reference.StartsWith("$Registry/", [System.StringComparison]::Ordinal) -and $reference.Substring($Registry.Length + 1).Contains('@sha256:'))) {
            Exit-Failure "-Image must be <registry>/<repo>@sha256:<digest> on ${Registry}: $reference"
        }
        $index++
        $name = $reference.Substring($Registry.Length + 1)
        $name = $name.Substring(0, $name.LastIndexOf('@'))
        $safeName = $name.Replace('/', '_')
        $sbom = "$Evidence/$safeName.spdx.json"
        $provenance = "$Evidence/$safeName.provenance.json"

        Write-Note "[$index/$($Image.Count)] SBOM for $reference"
        syft scan "registry:$reference" -o "spdx-json=$sbom"

        # A fresh token per attestation, fetched right before it is used.
        Write-Note "[$index/$($Image.Count)] attesting SBOM"
        Invoke-CosignAttest spdxjson $sbom $reference

        Write-Note "[$index/$($Image.Count)] attesting step-authored provenance"
        Write-Provenance $reference $provenance $pipelineCommit
        Invoke-CosignAttest slsaprovenance1 $provenance $reference

        if ($Lock) {
            $credential = Get-RegistryCredential
            foreach ($tagName in $Tag) {
                Write-Note "[$index/$($Image.Count)] locking ${name}:$tagName"
                # Data-plane call with the repository-scoped token (metadata write) [VERIFY token scope].
                $credential.Password | az acr repository update `
                    --name $registryName `
                    --image "${name}:$tagName" `
                    --write-enabled false `
                    --delete-enabled false `
                    --username $credential.User `
                    --password '@-' `
                    --output none
            }
            $credential = $null
        }
    }

    Write-Note "done: $($Image.Count) image(s); evidence in $Evidence"
}

if ($CheckReuse) {
    Invoke-ReuseCheck
}
else {
    Invoke-Attestation -Evidence $Out -Lock (-not $NoLock)
}
exit 0
