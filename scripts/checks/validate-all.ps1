#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    One entry point for every environment-repo check (design §11.7.5, ADR-IR34, docs/scripting.md).

.DESCRIPTION
    Called by Codefresh platform-env/env-checks (codefresh/platform/pipelines/env-checks.yml) with one
    sub-command per step, and by people locally with 'all'.

    Sub-commands
      all             Every sub-command below, in order; exits 1 if any fails.
      yaml            yamllint with .yamllint.yaml over every *.yaml and *.yml file.
      kustomize       'kustomize build' of every app overlay (gitops/apps/*/envs/*/*, gitops/apps/*/previews), every
                      platform kustomization under gitops/platform (Components excluded) and policies/kyverno/overlays/*;
                      renders into $RENDER_DIR. Starters (gitops/templates) hold tokens and are not built.
      kubeconform     Schema-validates the rendered overlays and argocd/clusters/**, argocd/optional/**.
      terraform       'terraform fmt -check -recursive' on terraform/ and octopus/terraform/; with TF_VALIDATE=true
                      also 'init -backend=false' and 'validate' in a temporary copy.
      mermaid         Parses every ```mermaid block in Markdown files.
      diagrams        scripts/diagrams/check.ps1: design/diagrams rendered, current and embedded.
      powershell      PSScriptAnalyzer (PSScriptAnalyzerSettings.psd1) over every .ps1, and the preamble of
                      docs/scripting.md in every script outside docs/owner/.
      boundaries      The tool-boundary rules (TB01-TB23) and the bot-path audit: the offline tests of
                      tests/Platform.Conformance.Offline/Kit/Boundaries and Octopus/PinWriterTests.
      consistency     The contract checks (C01-C25): the offline tests of tests/Platform.Conformance.Offline/Kit/Consistency.
      secrets         gitleaks over the tree with .gitleaks.toml when present.
      onboarding      The onboarding tool's check (tools/Platform.Onboarding): descriptors against apps/schema.json,
                      the apps' own files and, off the main branch, the blast radius against origin/<main>.
      dotnet-offline  dotnet test tests/Platform.Conformance.sln --filter TestCategory=Offline (TRX results), plus the
                      onboarding tool's unit tests when the solution does not list them, then the capability summary
                      of the run (Platform.Conformance.Report report).
      catalogue       docs/capabilities.md equals the catalogue rendered by Platform.Conformance.Report render-catalogue.

    Several sub-commands in one call run in order, and the call fails if any fails (like 'all').

    A missing tool is skipped with a warning locally and fails when CI=true. Absent content (a directory
    not present in a partial tree) is skipped, never failed.

    Environment
      CI                   "true" makes a missing tool fail.
      RENDER_DIR           Rendered-overlay directory shared by kustomize and kubeconform
                           (default: $CF_VOLUME_PATH/platform-render in Codefresh, else the temp directory).
      YAMLLINT, KUSTOMIZE, KUBECONFORM, TERRAFORM, GITLEAKS, NODE, MMDC, DOTNET
                           Tool paths overriding the PATH lookup.
      MERMAID_VALIDATOR    Node script that parses the mermaid blocks of one Markdown file (exit 0 when valid);
                           otherwise 'mmdc' (mermaid-cli) is used.
      KUBECONFORM_SCHEMA_LOCATIONS
                           Space-separated -schema-location values (default: the built-in Kubernetes schemas plus the
                           datreeio CRDs catalog).
      KUBERNETES_VERSION   Passed to kubeconform -kubernetes-version when set.
      TF_VALIDATE          "true" also runs terraform init/validate (downloads providers).
      PLATFORM_MAIN_BRANCH Branch that is the onboarding base (default: main).
      PLATFORM_BOT_AUTHORS Identity regex of the platform bots, read by the bot-path audit test.
      ONBOARDING_BASE      Base reference of the blast-radius check (default: origin/<main> off the main branch).
      TEST_RESULTS_DIR     Where the dotnet sub-commands write TRX files (default: <root>/tests/TestResults/offline).
      DIAGRAMS_SYNTAX      "skip" leaves the PlantUML syntax check to its own step (env-checks runs it in the
                           plantuml/plantuml image).

.PARAMETER Command
    One or more sub-commands, or 'all'.

.PARAMETER Root
    The environment-repo root (default: two levels above this script).

.EXAMPLE
    pwsh scripts/checks/validate-all.ps1 all
#>
[CmdletBinding()]
param(
    [string] $Root = '',

    [Parameter(Position = 0, ValueFromRemainingArguments)]
    [string[]] $Command = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$SubCommands = @('yaml', 'kustomize', 'kubeconform', 'terraform', 'mermaid', 'diagrams', 'powershell',
    'boundaries', 'consistency', 'secrets', 'onboarding', 'dotnet-offline', 'catalogue')
$CrdCatalog = 'https://raw.githubusercontent.com/datreeio/CRDs-catalog/main/{{.Group}}/{{.ResourceKind}}_{{.ResourceAPIVersion}}.json'
$PrunedDirectories = @('.git', '.terraform', 'node_modules', 'bin', 'obj', 'TestResults')

$usage = "Usage: validate-all.ps1 [-Root DIR] <all|$($SubCommands -join '|')> [<sub-command> ...]"
if ($Command.Count -eq 1 -and $Command[0] -in @('-h', '--help', 'help')) {
    Write-Host $usage
    exit 0
}
$unknown = @($Command | Where-Object { $_ -ne 'all' -and $_ -notin $SubCommands })
if ($Command.Count -eq 0 -or $unknown.Count -gt 0 -or ($Command -contains 'all' -and $Command.Count -gt 1)) {
    if ($unknown.Count -gt 0) {
        Write-Host "validate-all: unknown sub-command '$($unknown -join "', '")'"
    }
    Write-Host $usage
    exit 2
}

if (-not $Root) {
    $Root = if ($env:PLATFORM_ROOT) { $env:PLATFORM_ROOT } else { Join-Path $PSScriptRoot '..' '..' }
}
if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
    Write-Host "validate-all: root '$Root' not found"
    exit 2
}
$Root = (Resolve-Path -LiteralPath $Root).Path
$RenderDir = if ($env:RENDER_DIR) {
    $env:RENDER_DIR
}
elseif ($env:CF_VOLUME_PATH) {
    Join-Path $env:CF_VOLUME_PATH 'platform-render'
}
else {
    Join-Path ([System.IO.Path]::GetTempPath()) 'platform-render'
}

function Test-Ci {
    return $env:CI -in @('true', 'TRUE', 'True', '1', 'yes')
}

function Write-Pass([string] $Message) { Write-Host "PASS $Message" }
function Write-Fail([string] $Message) { Write-Host "FAIL $Message" }
function Write-Skip([string] $Message) { Write-Host "SKIP $Message" }
function Write-Warn([string] $Message) { Write-Host "WARN $Message" }

function Get-RelativePath([string] $Path) {
    return [System.IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

# The path of a tool: its override variable (upper case, dashes as underscores) or the PATH lookup; $null when missing.
function Get-ToolPath([string] $Name) {
    $variable = $Name.ToUpperInvariant().Replace('-', '_')
    $override = [Environment]::GetEnvironmentVariable($variable)
    $candidate = if ($override) { $override } else { $Name }
    $found = Get-Command -Name $candidate -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) {
        return $found.Source
    }
    return $null
}

# A missing tool: 1 when CI=true, 3 (skipped) locally.
function Get-MissingStatus([string] $SubCommand, [string] $Tool) {
    if (Test-Ci) {
        Write-Fail "${SubCommand}: required tool '$Tool' not found (CI=true)"
        return 1
    }
    Write-Warn "${SubCommand}: tool '$Tool' not found; skipped locally (fails when CI=true)"
    return 3
}

# Runs a native tool with its output on the host and returns its exit code instead of throwing.
function Invoke-Tool {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [string[]] $ArgumentList = @(),
        [string] $WorkingDirectory = ''
    )
    $PSNativeCommandUseErrorActionPreference = $false
    if ($WorkingDirectory) {
        Push-Location -LiteralPath $WorkingDirectory
    }
    try {
        & $FilePath @ArgumentList | Out-Host
        return $LASTEXITCODE
    }
    finally {
        if ($WorkingDirectory) {
            Pop-Location
        }
    }
}

# Every file under a directory whose name matches one of the patterns, skipping VCS, caches and build output; sorted.
function Find-RepositoryFile {
    param(
        [Parameter(Mandatory)] [string[]] $Pattern,
        [string] $Directory = $Root
    )
    $found = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        return
    }
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($Directory)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($sub in [System.IO.Directory]::EnumerateDirectories($current)) {
            if ([System.IO.Path]::GetFileName($sub) -notin $PrunedDirectories) {
                $pending.Push($sub)
            }
        }
        foreach ($file in [System.IO.Directory]::EnumerateFiles($current)) {
            $name = [System.IO.Path]::GetFileName($file)
            foreach ($p in $Pattern) {
                if ($name -like $p) {
                    $found.Add($file)
                    break
                }
            }
        }
    }
    $found | Sort-Object
}

# ---------------------------------------------------------------- yaml
function Invoke-YamlCheck {
    $yamllint = Get-ToolPath 'yamllint'
    if (-not $yamllint) { return Get-MissingStatus 'yaml' 'yamllint' }
    $files = @(Find-RepositoryFile -Pattern '*.yaml', '*.yml')
    if ($files.Count -eq 0) {
        Write-Skip 'yaml: no YAML files'
        return 3
    }
    $arguments = [System.Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath (Join-Path $Root '.yamllint.yaml')) {
        $arguments.AddRange([string[]]@('-c', (Join-Path $Root '.yamllint.yaml')))
    }
    $arguments.AddRange([string[]]@('-f', 'parsable'))
    # Paths relative to the root, so the root-anchored ignore patterns of .yamllint.yaml apply.
    $arguments.AddRange([string[]]@($files | ForEach-Object { Get-RelativePath $_ }))
    Write-Host "yaml: yamllint $($files.Count) files"
    if ((Invoke-Tool -FilePath $yamllint -ArgumentList $arguments -WorkingDirectory $Root) -eq 0) {
        Write-Pass 'yaml'
        return 0
    }
    Write-Fail 'yaml: yamllint reported errors'
    return 1
}

# ---------------------------------------------------------------- kustomize
function Get-KustomizeTarget {
    $targets = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    $patterns = @('gitops/apps/*/envs/*/*', 'gitops/apps/*/previews', 'policies/kyverno/overlays/*')
    foreach ($pattern in $patterns) {
        foreach ($dir in (Get-ChildItem -Path (Join-Path $Root $pattern) -Directory -ErrorAction SilentlyContinue)) {
            if (Test-Path -LiteralPath (Join-Path $dir.FullName 'kustomization.yaml')) {
                [void] $targets.Add($dir.FullName)
            }
        }
    }
    $platform = Join-Path $Root 'gitops/platform'
    foreach ($file in @(Find-RepositoryFile -Pattern 'kustomization.yaml' -Directory $platform)) {
        # A Component builds only inside an overlay that includes it.
        if (-not (Select-String -LiteralPath $file -Pattern '^kind:\s*Component' -Quiet)) {
            [void] $targets.Add([System.IO.Path]::GetDirectoryName($file))
        }
    }
    $targets | ForEach-Object { $_ }
}

# 0 all rendered, 1 a build failed, 3 nothing to render.
function Invoke-Render([string] $Kustomize) {
    if (Test-Path -LiteralPath $RenderDir) {
        Remove-Item -LiteralPath $RenderDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $RenderDir -Force | Out-Null
    $targets = @(Get-KustomizeTarget)
    if ($targets.Count -eq 0) {
        return 3
    }
    $status = 0
    $PSNativeCommandUseErrorActionPreference = $false
    foreach ($target in $targets) {
        $relative = Get-RelativePath $target
        $name = $relative.Replace('/', '_')
        $yaml = Join-Path $RenderDir "$name.yaml"
        $errors = Join-Path $RenderDir "$name.err"
        & $Kustomize build $target 1> $yaml 2> $errors
        if ($LASTEXITCODE -eq 0) {
            Write-Pass "kustomize build $relative"
            Remove-Item -LiteralPath $errors -Force
        }
        else {
            $tail = (Get-Content -LiteralPath $errors -Tail 3) -join ' '
            Write-Fail "kustomize build ${relative}: $tail"
            Remove-Item -LiteralPath $yaml -Force
            $status = 1
        }
    }
    return $status
}

function Invoke-KustomizeCheck {
    $kustomize = Get-ToolPath 'kustomize'
    if (-not $kustomize) { return Get-MissingStatus 'kustomize' 'kustomize' }
    $status = Invoke-Render $kustomize
    if ($status -eq 3) {
        Write-Skip 'kustomize: no overlays present'
    }
    return $status
}

# ---------------------------------------------------------------- kubeconform
# Placeholders of §7.1 are not valid DNS or Kubernetes names, so schema validation runs on copies where each
# <placeholder> becomes a valid stand-in (<acr-name>.azurecr.io -> ph-acr-name.azurecr.io).
function ConvertTo-StandIn([string] $Text) {
    return [regex]::Replace($Text, '<[A-Za-z0-9{][^<>"]*>', {
            param($match)
            $token = $match.Value.Substring(1, $match.Value.Length - 2).ToLowerInvariant()
            'ph-' + [regex]::Replace($token, '[^a-z0-9.-]', '-')
        })
}

function Invoke-KubeconformCheck {
    $kubeconform = Get-ToolPath 'kubeconform'
    if (-not $kubeconform) { return Get-MissingStatus 'kubeconform' 'kubeconform' }
    $status = 0
    $kustomize = Get-ToolPath 'kustomize'
    if ($kustomize) {
        $rendered = Invoke-Render $kustomize
        # 3 (nothing to render) is fine; 1 means a build failed.
        if ($rendered -eq 1) {
            $status = 1
        }
    }
    elseif (Test-Path -Path (Join-Path $RenderDir '*.yaml')) {
        Write-Warn "kubeconform: kustomize not found; using overlays rendered earlier in $RenderDir"
    }
    elseif (@(Get-KustomizeTarget).Count -gt 0) {
        if (Test-Ci) {
            Write-Fail 'kubeconform: kustomize is required to render the overlays (CI=true)'
            return 1
        }
        Write-Warn 'kubeconform: kustomize not found; overlays not rendered, raw manifests only'
    }
    $files = [System.Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $RenderDir) {
        $files.AddRange([string[]]@(Get-ChildItem -LiteralPath $RenderDir -Filter '*.yaml' -File | ForEach-Object FullName))
    }
    # Raw manifests. Helm values (argocd/bootstrap) are not manifests.
    foreach ($dir in @('argocd/clusters', 'argocd/optional')) {
        $files.AddRange([string[]]@(Find-RepositoryFile -Pattern '*.yaml', '*.yml' -Directory (Join-Path $Root $dir)))
    }
    if ($files.Count -eq 0) {
        Write-Skip 'kubeconform: no manifests present'
        return 3
    }
    $locations = if ($env:KUBECONFORM_SCHEMA_LOCATIONS) {
        $env:KUBECONFORM_SCHEMA_LOCATIONS -split '\s+' | Where-Object { $_ }
    }
    else {
        @('default', $CrdCatalog)
    }
    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.AddRange([string[]]@('-strict', '-summary', '-ignore-missing-schemas'))
    foreach ($location in $locations) {
        $arguments.AddRange([string[]]@('-schema-location', $location))
    }
    if ($env:KUBERNETES_VERSION) {
        $arguments.AddRange([string[]]@('-kubernetes-version', $env:KUBERNETES_VERSION))
    }
    $work = Join-Path ([System.IO.Path]::GetTempPath()) "kubeconform-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        foreach ($file in $files) {
            $name = if ($file.StartsWith($RenderDir)) { [System.IO.Path]::GetFileName($file) } else { Get-RelativePath $file }
            $target = Join-Path $work ($name.Replace('/', '__'))
            [System.IO.File]::WriteAllText($target, (ConvertTo-StandIn ([System.IO.File]::ReadAllText($file))))
        }
        Write-Host "kubeconform: $($files.Count) files (placeholders replaced by ph-* stand-ins)"
        $arguments.AddRange([string[]]@(Get-ChildItem -LiteralPath $work -File | Sort-Object Name | ForEach-Object FullName))
        if ((Invoke-Tool -FilePath $kubeconform -ArgumentList $arguments) -eq 0) {
            if ($status -eq 0) {
                Write-Pass 'kubeconform'
            }
            return $status
        }
        Write-Fail 'kubeconform: schema validation failed'
        return 1
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}

# ---------------------------------------------------------------- terraform
function Invoke-TerraformCheck {
    $terraform = Get-ToolPath 'terraform'
    if (-not $terraform) { return Get-MissingStatus 'terraform' 'terraform' }
    $dirs = @(@('terraform', 'octopus/terraform') | ForEach-Object { Join-Path $Root $_ } | Where-Object {
        @(Find-RepositoryFile -Pattern '*.tf' -Directory $_).Count -gt 0
    })
    if ($dirs.Count -eq 0) {
        Write-Skip 'terraform: no Terraform present'
        return 3
    }
    $status = 0
    foreach ($dir in $dirs) {
        if ((Invoke-Tool -FilePath $terraform -ArgumentList @('fmt', '-check', '-recursive', '-diff', $dir)) -eq 0) {
            Write-Pass "terraform fmt $(Get-RelativePath $dir)"
        }
        else {
            Write-Fail "terraform fmt $(Get-RelativePath $dir): files need formatting"
            $status = 1
        }
    }
    if ($env:TF_VALIDATE -eq 'true') {
        # Validate in a temporary copy so init never writes .terraform/ into the tree.
        $copy = Join-Path ([System.IO.Path]::GetTempPath()) "tf-validate-$([Guid]::NewGuid().ToString('N'))"
        try {
            foreach ($top in @('terraform', 'octopus')) {
                $source = Join-Path $Root $top
                if (Test-Path -LiteralPath $source) {
                    Copy-Item -LiteralPath $source -Destination (Join-Path $copy $top) -Recurse -Exclude '.terraform'
                }
            }
            $modules = @('terraform', 'octopus/terraform') | ForEach-Object { Join-Path $copy $_ } |
                ForEach-Object { Find-RepositoryFile -Pattern '*.tf' -Directory $_ } |
                ForEach-Object { [System.IO.Path]::GetDirectoryName($_) } | Sort-Object -Unique
            foreach ($module in $modules) {
                $name = [System.IO.Path]::GetRelativePath($copy, $module).Replace('\', '/')
                $init = Invoke-Tool -FilePath $terraform -ArgumentList @("-chdir=$module", 'init', '-backend=false', '-input=false')
                if ($init -eq 0 -and (Invoke-Tool -FilePath $terraform -ArgumentList @("-chdir=$module", 'validate', '-no-color')) -eq 0) {
                    Write-Pass "terraform validate $name"
                }
                else {
                    Write-Fail "terraform validate $name"
                    $status = 1
                }
            }
        }
        finally {
            if (Test-Path -LiteralPath $copy) {
                Remove-Item -LiteralPath $copy -Recurse -Force
            }
        }
    }
    return $status
}

# ---------------------------------------------------------------- mermaid
function Invoke-MermaidCheck {
    $files = @(@(Find-RepositoryFile -Pattern '*.md') | Where-Object { Select-String -LiteralPath $_ -Pattern '^```mermaid' -Quiet })
    if ($files.Count -eq 0) {
        Write-Skip 'mermaid: no mermaid blocks'
        return 3
    }
    $node = Get-ToolPath 'node'
    $mmdc = Get-ToolPath 'mmdc'
    $status = 0
    if ($env:MERMAID_VALIDATOR -and $node -and (Test-Path -LiteralPath $env:MERMAID_VALIDATOR)) {
        $PSNativeCommandUseErrorActionPreference = $false
        foreach ($file in $files) {
            $output = & $node $env:MERMAID_VALIDATOR $file 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Pass "mermaid $(Get-RelativePath $file)"
            }
            else {
                $reason = ($output | Where-Object { "$_" -match 'FAIL' } | Select-Object -First 3) -join ' '
                Write-Fail "mermaid $(Get-RelativePath $file): $reason"
                $status = 1
            }
        }
        return $status
    }
    if ($mmdc) {
        $work = Join-Path ([System.IO.Path]::GetTempPath()) "mermaid-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $work | Out-Null
        try {
            $PSNativeCommandUseErrorActionPreference = $false
            foreach ($file in $files) {
                & $mmdc -q -i $file -o (Join-Path $work 'out.md') *> $null
                if ($LASTEXITCODE -eq 0) {
                    Write-Pass "mermaid $(Get-RelativePath $file)"
                }
                else {
                    Write-Fail "mermaid $(Get-RelativePath $file): mmdc could not render a block"
                    $status = 1
                }
            }
        }
        finally {
            Remove-Item -LiteralPath $work -Recurse -Force
        }
        return $status
    }
    return Get-MissingStatus 'mermaid' 'MERMAID_VALIDATOR (node script) or mmdc'
}

# ---------------------------------------------------------------- diagrams
function Invoke-DiagramsCheck {
    $arguments = @{ Root = $Root }
    if ($env:DIAGRAMS_SYNTAX -eq 'skip') {
        $arguments.SkipSyntax = $true
    }
    & (Join-Path $Root 'scripts/diagrams/check.ps1') @arguments | Out-Host
    return $LASTEXITCODE
}

# ---------------------------------------------------------------- powershell
$PreambleRules = [ordered]@{
    '#Requires -Version 7.4'                           = '(?m)^#Requires -Version 7\.[4-9]'
    'Set-StrictMode -Version Latest'                   = '(?m)^\s*Set-StrictMode -Version Latest'
    '$ErrorActionPreference = ''Stop'''                = '(?m)^\s*\$ErrorActionPreference\s*=\s*[''"]Stop[''"]'
    '$PSNativeCommandUseErrorActionPreference = $true' = '(?m)^\s*\$PSNativeCommandUseErrorActionPreference\s*=\s*\$true'
}

function Invoke-PowerShellCheck {
    $files = @(Find-RepositoryFile -Pattern '*.ps1', '*.psm1')
    if ($files.Count -eq 0) {
        Write-Skip 'powershell: no PowerShell files'
        return 3
    }
    $status = 0
    foreach ($file in $files) {
        $relative = Get-RelativePath $file
        if ($relative.StartsWith('docs/owner/') -or $file.EndsWith('.psm1')) {
            continue
        }
        $text = [System.IO.File]::ReadAllText($file)
        $missing = @($PreambleRules.Keys | Where-Object { $text -notmatch $PreambleRules[$_] })
        if ($missing) {
            Write-Fail "powershell ${relative}: preamble lacks $($missing -join '; ') (docs/scripting.md)"
            $status = 1
        }
    }
    if (-not (Get-Module -ListAvailable -Name PSScriptAnalyzer)) {
        $missingStatus = Get-MissingStatus 'powershell' 'PSScriptAnalyzer module'
        if ($missingStatus -eq 1) {
            return 1
        }
        return $(if ($status -eq 1) { 1 } else { 3 })
    }
    Import-Module PSScriptAnalyzer
    $settings = Join-Path $Root 'PSScriptAnalyzerSettings.psd1'
    $findings = foreach ($file in $files) {
        if (Test-Path -LiteralPath $settings) {
            Invoke-ScriptAnalyzer -Path $file -Settings $settings
        }
        else {
            Invoke-ScriptAnalyzer -Path $file
        }
    }
    foreach ($finding in @($findings)) {
        Write-Fail "powershell $(Get-RelativePath $finding.ScriptPath):$($finding.Line) $($finding.RuleName): $($finding.Message)"
        $status = 1
    }
    if ($status -eq 0) {
        Write-Pass "powershell: $($files.Count) files"
    }
    return $status
}

# ---------------------------------------------------------------- dotnet helpers
function Get-ResultsDirectory([string] $Name) {
    if ($env:TEST_RESULTS_DIR) {
        return $(if ($Name -eq 'offline') { $env:TEST_RESULTS_DIR } else { Join-Path $env:TEST_RESULTS_DIR $Name })
    }
    # A new folder per run: the capability summary reads every TRX file of the folder.
    return Join-Path $Root "tests/TestResults/$Name/$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
}

function Get-TrxTestCount([string] $Directory) {
    $total = 0
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -Filter '*.trx' -File -ErrorAction SilentlyContinue)) {
        $counters = ([xml] [System.IO.File]::ReadAllText($file.FullName)).SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
        if ($counters) {
            $total += [int] $counters.GetAttribute('total')
        }
    }
    return $total
}

function Invoke-OfflineTest([string] $SubCommand, [string] $Filter, [string] $Prefix) {
    $dotnet = Get-ToolPath 'dotnet'
    if (-not $dotnet) { return Get-MissingStatus $SubCommand 'dotnet' }
    $project = Join-Path $Root 'tests/Platform.Conformance.Offline/Platform.Conformance.Offline.csproj'
    if (-not (Test-Path -LiteralPath $project)) {
        Write-Skip "${SubCommand}: tests/Platform.Conformance.Offline absent"
        return 3
    }
    $results = Get-ResultsDirectory $SubCommand
    $arguments = @('test', $project, '-c', 'Release', '--filter', $Filter, '--logger', "trx;LogFilePrefix=$Prefix",
        '--logger', 'console;verbosity=normal', '--results-directory', $results)
    if ((Invoke-Tool -FilePath $dotnet -ArgumentList $arguments) -eq 0) {
        # dotnet test passes a filter that matches nothing.
        if ((Get-TrxTestCount $results) -eq 0) {
            Write-Fail "${SubCommand}: the filter matched no test ($Filter)"
            return 1
        }
        Write-Pass $SubCommand
        return 0
    }
    Write-Fail "${SubCommand}: offline tests failed (TRX in $(Get-RelativePath $results))"
    return 1
}

# ---------------------------------------------------------------- boundaries and consistency
function Invoke-BoundariesCheck {
    return Invoke-OfflineTest 'boundaries' 'TestCategory=Offline&(FullyQualifiedName~Platform.Conformance.Offline.Kit.Boundaries|FullyQualifiedName~Platform.Conformance.Offline.Octopus.PinWriterTests)' 'boundaries'
}

function Invoke-ConsistencyCheck {
    return Invoke-OfflineTest 'consistency' 'TestCategory=Offline&FullyQualifiedName~Platform.Conformance.Offline.Kit.Consistency' 'consistency'
}

# ---------------------------------------------------------------- secrets
function Invoke-SecretsCheck {
    $gitleaks = Get-ToolPath 'gitleaks'
    if (-not $gitleaks) { return Get-MissingStatus 'secrets' 'gitleaks' }
    $config = @()
    if (Test-Path -LiteralPath (Join-Path $Root '.gitleaks.toml')) {
        $config = @('--config', (Join-Path $Root '.gitleaks.toml'))
    }
    else {
        Write-Warn 'secrets: .gitleaks.toml absent; using the default rules'
    }
    $PSNativeCommandUseErrorActionPreference = $false
    & $gitleaks dir --help *> $null
    $hasDir = $LASTEXITCODE -eq 0
    $arguments = if ($hasDir) {
        @('dir', $Root) + $config + @('--redact', '--no-banner', '--exit-code', '1')
    }
    else {
        @('detect', '--no-git', '--source', $Root) + $config + @('--redact', '--no-banner', '--exit-code', '1')
    }
    if ((Invoke-Tool -FilePath $gitleaks -ArgumentList $arguments) -eq 0) {
        Write-Pass 'secrets'
        return 0
    }
    Write-Fail 'secrets: gitleaks reported findings'
    return 1
}

# ---------------------------------------------------------------- onboarding
function Get-OnboardingBase {
    if ($env:ONBOARDING_BASE) {
        return $env:ONBOARDING_BASE
    }
    $main = if ($env:PLATFORM_MAIN_BRANCH) { $env:PLATFORM_MAIN_BRANCH } else { 'main' }
    $PSNativeCommandUseErrorActionPreference = $false
    $branch = if ($env:CF_BRANCH) { $env:CF_BRANCH } else { (& git -C $Root rev-parse --abbrev-ref HEAD 2>$null) }
    if ($branch -and $branch -ne $main -and $branch -ne 'HEAD') {
        & git -C $Root rev-parse --verify --quiet "origin/$main" *> $null
        if ($LASTEXITCODE -eq 0) {
            return "origin/$main"
        }
    }
    return ''
}

function Invoke-OnboardingCheck {
    $project = Join-Path $Root 'tools/Platform.Onboarding/Platform.Onboarding.csproj'
    if (-not (Test-Path -LiteralPath $project) -or -not (Test-Path -LiteralPath (Join-Path $Root 'apps/schema.json'))) {
        Write-Skip 'onboarding: tools/Platform.Onboarding or apps/schema.json absent'
        return 3
    }
    $dotnet = Get-ToolPath 'dotnet'
    if (-not $dotnet) { return Get-MissingStatus 'onboarding' 'dotnet' }
    $arguments = @('run', '--project', $project, '-c', 'Release', '--', 'check', '--root', $Root)
    $base = Get-OnboardingBase
    if ($base) {
        $arguments += @('--base', $base)
        Write-Host "onboarding: blast radius against $base"
    }
    if ((Invoke-Tool -FilePath $dotnet -ArgumentList $arguments) -eq 0) {
        Write-Pass 'onboarding'
        return 0
    }
    Write-Fail 'onboarding: Platform.Onboarding check reported errors'
    return 1
}

# ---------------------------------------------------------------- dotnet-offline
function Invoke-DotnetOfflineCheck {
    $solution = Join-Path $Root 'tests/Platform.Conformance.sln'
    if (-not (Test-Path -LiteralPath $solution)) {
        Write-Skip 'dotnet-offline: tests/Platform.Conformance.sln absent'
        return 3
    }
    $dotnet = Get-ToolPath 'dotnet'
    if (-not $dotnet) { return Get-MissingStatus 'dotnet-offline' 'dotnet' }
    # An unreplaced placeholder would match no commit and let the bot-path audit pass vacuously.
    if ($env:PLATFORM_BOT_AUTHORS -match '^<.*>$') {
        Write-Fail 'dotnet-offline: PLATFORM_BOT_AUTHORS still holds its placeholder'
        return 1
    }
    $results = Get-ResultsDirectory 'offline'
    $status = 0
    $arguments = @('test', $solution, '-c', 'Release', '--filter', 'TestCategory=Offline', '--logger', 'trx;LogFilePrefix=offline',
        '--results-directory', $results)
    if ((Invoke-Tool -FilePath $dotnet -ArgumentList $arguments) -eq 0) {
        if ((Get-TrxTestCount $results) -eq 0) {
            Write-Fail 'dotnet-offline: no Offline test ran'
            $status = 1
        }
        else {
            Write-Pass 'dotnet-offline tests/Platform.Conformance.sln'
        }
    }
    else {
        Write-Fail "dotnet-offline: offline tests failed (TRX in $(Get-RelativePath $results))"
        $status = 1
    }
    $report = Join-Path $Root 'tests/Platform.Conformance.Report'
    $assembly = Join-Path $Root 'tests/Platform.Conformance.Offline/bin/Release/net10.0/Platform.Conformance.Offline.dll'
    if ((Test-Path -LiteralPath $report) -and (Test-Path -LiteralPath $assembly) -and
        @(Get-ChildItem -LiteralPath $results -Filter '*.trx' -File -ErrorAction SilentlyContinue).Count -gt 0) {
        $PSNativeCommandUseErrorActionPreference = $false
        $branch = if ($env:CF_BRANCH) { $env:CF_BRANCH } else { (& git -C $Root rev-parse --abbrev-ref HEAD 2>$null) }
        $PSNativeCommandUseErrorActionPreference = $true
        $arguments = @('run', '--project', $report, '-c', 'Release', '--', 'report', '--trx', $results, '--assembly', $assembly,
            '--repo-root', $Root, '--out', $results, '--title', "env-checks $branch (Offline)")
        if ((Invoke-Tool -FilePath $dotnet -ArgumentList $arguments) -eq 0) {
            $summary = Join-Path $results 'summary.md'
            if (Test-Path -LiteralPath $summary) {
                Get-Content -LiteralPath $summary | ForEach-Object { Write-Host $_ }
            }
        }
        else {
            Write-Fail 'dotnet-offline: the capability summary failed'
            $status = 1
        }
    }
    $kit = Join-Path $Root 'tools/Platform.Onboarding.Tests/Platform.Onboarding.Tests.csproj'
    if ((Test-Path -LiteralPath $kit) -and -not (Select-String -LiteralPath $solution -Pattern 'Platform.Onboarding.Tests' -SimpleMatch -Quiet)) {
        $arguments = @('test', $kit, '-c', 'Release', '--filter', 'TestCategory=Offline', '--logger', 'trx;LogFilePrefix=onboarding',
            '--results-directory', $results)
        if ((Invoke-Tool -FilePath $dotnet -ArgumentList $arguments) -eq 0) {
            Write-Pass 'dotnet-offline tools/Platform.Onboarding.Tests'
        }
        else {
            Write-Fail 'dotnet-offline: onboarding tool tests failed'
            $status = 1
        }
    }
    return $status
}

# ---------------------------------------------------------------- catalogue
function Invoke-CatalogueCheck {
    $report = Join-Path $Root 'tests/Platform.Conformance.Report'
    $page = Join-Path $Root 'docs/capabilities.md'
    if (-not (Test-Path -LiteralPath $report) -or -not (Test-Path -LiteralPath $page)) {
        Write-Skip 'catalogue: tests/Platform.Conformance.Report or docs/capabilities.md absent'
        return 3
    }
    $dotnet = Get-ToolPath 'dotnet'
    if (-not $dotnet) { return Get-MissingStatus 'catalogue' 'dotnet' }
    $rendered = Join-Path ([System.IO.Path]::GetTempPath()) "capabilities-$([Guid]::NewGuid().ToString('N')).md"
    try {
        $arguments = @('run', '--project', $report, '-c', 'Release', '--', 'render-catalogue', '--repo-root', $Root, '--out', $rendered)
        if ((Invoke-Tool -FilePath $dotnet -ArgumentList $arguments) -ne 0) {
            Write-Fail 'catalogue: render-catalogue failed'
            return 1
        }
        $want = [System.IO.File]::ReadAllText($rendered).Replace("`r`n", "`n")
        $have = [System.IO.File]::ReadAllText($page).Replace("`r`n", "`n")
        if ($want -eq $have) {
            Write-Pass 'catalogue: docs/capabilities.md is current'
            return 0
        }
        Write-Fail 'catalogue: docs/capabilities.md is stale; run dotnet run --project tests/Platform.Conformance.Report -- render-catalogue'
        Compare-Object -ReferenceObject ($want -split "`n") -DifferenceObject ($have -split "`n") |
            Select-Object -First 20 | ForEach-Object { Write-Host "  $($_.SideIndicator) $($_.InputObject)" }
        return 1
    }
    finally {
        if (Test-Path -LiteralPath $rendered) {
            Remove-Item -LiteralPath $rendered -Force
        }
    }
}

function Invoke-SubCommand([string] $Name) {
    Write-Host "== $Name"
    switch ($Name) {
        'yaml' { return Invoke-YamlCheck }
        'kustomize' { return Invoke-KustomizeCheck }
        'kubeconform' { return Invoke-KubeconformCheck }
        'terraform' { return Invoke-TerraformCheck }
        'mermaid' { return Invoke-MermaidCheck }
        'diagrams' { return Invoke-DiagramsCheck }
        'powershell' { return Invoke-PowerShellCheck }
        'boundaries' { return Invoke-BoundariesCheck }
        'consistency' { return Invoke-ConsistencyCheck }
        'secrets' { return Invoke-SecretsCheck }
        'onboarding' { return Invoke-OnboardingCheck }
        'dotnet-offline' { return Invoke-DotnetOfflineCheck }
        'catalogue' { return Invoke-CatalogueCheck }
    }
    return 2
}

$selected = @(if ($Command -contains 'all') { $SubCommands } else { $Command })
if ($selected.Count -gt 1) {
    $summary = [System.Collections.Generic.List[string]]::new()
    $overall = 0
    foreach ($sub in $selected) {
        $status = Invoke-SubCommand $sub
        switch ($status) {
            0 { $summary.Add("PASS $sub") }
            3 { $summary.Add("SKIP $sub") }
            default {
                $summary.Add("FAIL $sub")
                $overall = 1
            }
        }
    }
    Write-Host "== summary (root=$Root, CI=$(if ($env:CI) { $env:CI } else { 'false' }))"
    $summary | ForEach-Object { Write-Host $_ }
    exit $overall
}

$result = Invoke-SubCommand $selected[0]
exit $(if ($result -eq 3) { 0 } else { $result })
