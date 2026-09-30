#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Plans and applies octopus/terraform (ADR-IR34 §11.7.2 item 6, §11.9): the P1-06 full apply on a copy of the phase 0
    preview state, and every later apply (onboarding, the re-applies after env-apply and terraform/apps/grants).

.DESCRIPTION
    Procedure, inputs and checks: docs/preview-octopus.md. Each parameter defaults to its environment variable, so
    'STATE_FILE=... TFVARS=... PLAN_ONLY=1 pwsh -NoProfile -File octopus/apply.ps1' works like the parameters.

    Environment:
      OCTOPUS_API_KEY   the Space Manager key of AISF-Service-Account (ADR-IR32). It reaches Terraform only through
                        TF_VAR_octopus_api_key and TF_VAR_platform_octopus_api_key, never through an argument. An
                        already exported TF_VAR_octopus_api_key or OCTOPUS_APIKEY is used when it is unset.
      TF_VAR_argocd_repo_read_credential  the JSON repository credential of Argo CD (ArgoCD.RepoReadCredential). Give it
                        on every run once it has been set: without it the plan deletes the variable, and the next
                        env-apply that creates a cluster (the other tier, a rebuild) seeds Argo CD without a credential.
      TF_VAR_e2e_github_token  the GitHub token of runbook e2e-pass (E2E.GitHubToken). Give it on every run once it has
                        been set: without it the plan deletes the variable, and e2e-pass fails at its first step.
    Azure: the configuration reads identities and ingress IPs (azure.tf) and the backend is azurerm, so the shell holds
    an Azure login with read access to the subscription (ARM_* variables or az login), as for terraform/foundation.

    What it does:
      1. Copies octopus/terraform to a private temporary directory; with a state file it adds a local-backend override.
      2. terraform init, then plan with -parallelism=1 (provider 1.20.0 panicked on concurrent team creates).
      3. Refuses the plan when it deletes or replaces anything but scoped user roles and variables, or when it deletes
         ArgoCD.RepoReadCredential or E2E.GitHubToken.
      4. Applies the saved plan. When the only errors are "Provider produced inconsistent result after apply" (known
         provider 1.20.0 behaviour for converted projects, see projects.tf), it plans, checks and applies once more:
         Terraform has saved the new values, so the second pass converges.
    The plan file and the state hold the key: the temporary directory is private and removed on exit, and on Linux and
    macOS every file the run creates is private (umask 077).

.PARAMETER TfVars
    The untracked terraform.tfvars (terraform.tfvars.example lists the values). Default: TFVARS.

.PARAMETER StateFile
    A local state file outside the repository (P1-06: the copy of the preview state). Empty: the azurerm backend
    octopus-space.tfstate in the backend storage account (after the migration). Default: STATE_FILE.

.PARAMETER BackendStorageAccount
    <tfstate-storage-account-global>; needed without a state file and to migrate. Default: TF_BACKEND_STORAGE_ACCOUNT.

.PARAMETER TerraformPath
    Terraform 1.7 or later. Default: TF_BIN, else terraform on PATH.

.PARAMETER PlanOnly
    Plan and check only. Default: PLAN_ONLY=1.

.PARAMETER MigrateState
    With a state file: copy that state to the azurerm backend and stop (no plan, no apply). Default: MIGRATE_STATE=1.

.PARAMETER AllowDestroy
    Let the plan check pass deletes and replacements (never needed at P1-06). Default: ALLOW_DESTROY=1.

.OUTPUTS
    The Terraform log and outputs project_ids and env_sleep_triggers. Exit code 0 when applied, planned (PlanOnly) or
    migrated; 1 with a message 'apply: ...' on standard error otherwise.
#>
[CmdletBinding()]
param(
    [string]$TfVars = $env:TFVARS,
    [string]$StateFile = $env:STATE_FILE,
    [string]$BackendStorageAccount = $env:TF_BACKEND_STORAGE_ACCOUNT,
    [string]$TerraformPath = $(if ($env:TF_BIN) { $env:TF_BIN } else { 'terraform' }),
    [switch]$PlanOnly = ($env:PLAN_ONLY -eq '1'),
    [switch]$MigrateState = ($env:MIGRATE_STATE -eq '1'),
    [switch]$AllowDestroy = ($env:ALLOW_DESTROY -eq '1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Stop-Apply([string]$Message) {
    [Console]::Error.WriteLine("apply: $Message")
    exit 1
}

# The physical path of an existing directory or file: every symbolic link resolved, as 'pwd -P' did.
function Resolve-PhysicalPath([string]$Path, [int]$Depth = 0) {
    if ($Depth -gt 40) {
        Stop-Apply "too many levels of symbolic links: $Path"
    }
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $resolved = $root
    $separators = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    foreach ($part in $full.Substring($root.Length).Split($separators, [StringSplitOptions]::RemoveEmptyEntries)) {
        $candidate = Join-Path $resolved $part
        $target = [IO.FileInfo]::new($candidate).LinkTarget
        if ($target) {
            if (-not [IO.Path]::IsPathRooted($target)) {
                $target = Join-Path $resolved $target
            }
            $candidate = Resolve-PhysicalPath $target ($Depth + 1)
        }
        $resolved = $candidate
    }
    return $resolved
}

# Files that Terraform writes (plans, the local state and its backup) are private, as with 'umask 077'.
if (-not $IsWindows) {
    Add-Type -Namespace PlatformApply -Name Posix -MemberDefinition '[System.Runtime.InteropServices.DllImport("libc")] public static extern uint umask(uint mask);'
    $null = [PlatformApply.Posix]::umask(63)
}

$repoRoot = Resolve-PhysicalPath (Join-Path $PSScriptRoot '..')
$tfSource = Join-Path $repoRoot 'octopus/terraform'
if (-not (Test-Path -LiteralPath $tfSource -PathType Container)) {
    Stop-Apply "missing $tfSource"
}
if (-not (Get-Command $TerraformPath -CommandType Application -ErrorAction SilentlyContinue)) {
    Stop-Apply "Terraform not found (TF_BIN=$TerraformPath)"
}

# ---------------------------------------------------------------- credentials: environment only
$key = @($env:OCTOPUS_API_KEY, $env:TF_VAR_octopus_api_key, $env:OCTOPUS_APIKEY) | Where-Object { $_ } | Select-Object -First 1
if (-not $key) {
    Stop-Apply 'set OCTOPUS_API_KEY (the Space Manager key; ADR-IR32)'
}
if (-not $key.StartsWith('API-', [StringComparison]::Ordinal)) {
    Stop-Apply 'OCTOPUS_API_KEY is not an Octopus API key (API-...)'
}
$env:TF_VAR_octopus_api_key = $key
if (-not $env:TF_VAR_platform_octopus_api_key) {
    $env:TF_VAR_platform_octopus_api_key = $key
}
Remove-Variable -Name key
$env:TF_IN_AUTOMATION = '1'

# ---------------------------------------------------------------- working copy
$workDir = [IO.Directory]::CreateTempSubdirectory('octopus-apply-').FullName
try {
    Copy-Item -Path (Join-Path $tfSource '*.tf') -Destination $workDir
    $lockFile = Join-Path $tfSource '.terraform.lock.hcl'
    if (Test-Path -LiteralPath $lockFile -PathType Leaf) {
        Copy-Item -LiteralPath $lockFile -Destination $workDir
    }

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $separator = [IO.Path]::DirectorySeparatorChar
    $backendArgs = @()
    $stateFilePath = ''
    if ($StateFile) {
        $stateParent = Split-Path -Parent $StateFile
        if (-not $stateParent) {
            $stateParent = '.'
        }
        if (-not (Test-Path -LiteralPath $stateParent -PathType Container)) {
            Stop-Apply 'the directory of STATE_FILE does not exist'
        }
        $stateDir = Resolve-PhysicalPath $stateParent
        if ("$stateDir$separator".StartsWith("$repoRoot$separator", $comparison)) {
            Stop-Apply 'STATE_FILE must be outside the repository (it holds the Space Manager key)'
        }
        $stateFilePath = Join-Path $stateDir (Split-Path -Leaf $StateFile)
        if (-not $MigrateState -and -not (Test-Path -LiteralPath $stateFilePath -PathType Leaf)) {
            Write-Host "apply: $stateFilePath does not exist yet; Terraform starts an empty state"
        }
        $hclPath = $stateFilePath.Replace('\', '\\').Replace('"', '\"')
        $override = @(
            '# Generated by octopus/apply.ps1: local state outside the repository.'
            'terraform {'
            '  backend "local" {'
            "    path = `"$hclPath`""
            '  }'
            '}'
        )
        [IO.File]::WriteAllText((Join-Path $workDir 'backend_override.tf'), -join ($override | ForEach-Object { "$_`n" }))
    }
    else {
        if (-not $BackendStorageAccount) {
            Stop-Apply 'set STATE_FILE, or TF_BACKEND_STORAGE_ACCOUNT for the azurerm backend'
        }
        if ($MigrateState) {
            Stop-Apply 'MIGRATE_STATE=1 needs STATE_FILE (the state to copy)'
        }
    }

    $azurermBackendArgs = @(
        '-backend-config=resource_group_name=rg-platform-global'
        "-backend-config=storage_account_name=$BackendStorageAccount"
        '-backend-config=container_name=tfstate'
        '-backend-config=key=octopus-space.tfstate'
        '-backend-config=use_azuread_auth=true'
    )
    if (-not $StateFile) {
        $backendArgs = $azurermBackendArgs
    }
    $chdir = "-chdir=$workDir"

    $null = & $TerraformPath $chdir init -input=false -no-color @backendArgs
    Write-Host "terraform init: done ($(if ($StateFile) { "local state $stateFilePath" } else { 'azurerm octopus-space.tfstate' }))"

    if ($MigrateState) {
        if (-not $BackendStorageAccount) {
            Stop-Apply 'MIGRATE_STATE=1 needs TF_BACKEND_STORAGE_ACCOUNT'
        }
        if (-not (Test-Path -LiteralPath $stateFilePath -PathType Leaf)) {
            Stop-Apply "$stateFilePath does not exist"
        }
        Remove-Item -LiteralPath (Join-Path $workDir 'backend_override.tf') -Force
        & $TerraformPath $chdir init -input=false -no-color -migrate-state -force-copy @azurermBackendArgs | Out-Host
        Write-Host "State copied to octopus-space.tfstate in $BackendStorageAccount. Later runs: unset STATE_FILE."
        exit 0
    }

    # ---------------------------------------------------------------- plan and check
    $tfVarsArgs = @()
    if ($TfVars) {
        if (-not (Test-Path -LiteralPath $TfVars -PathType Leaf)) {
            Stop-Apply "TFVARS $TfVars does not exist"
        }
        $tfVarsParent = Split-Path -Parent $TfVars
        $tfVarsArgs = @('-var-file=' + (Join-Path (Resolve-PhysicalPath $(if ($tfVarsParent) { $tfVarsParent } else { '.' })) (Split-Path -Leaf $TfVars)))
    }

    # Deletes and replacements pass only for objects that carry no history. A run without
    # TF_VAR_argocd_repo_read_credential would delete ArgoCD.RepoReadCredential, which every env-apply that creates a
    # cluster needs (terraform/tier seeds Argo CD's repository credential from it); one without TF_VAR_e2e_github_token
    # would delete E2E.GitHubToken, which runbook e2e-pass needs.
    $replaceable = @('octopusdeploy_scoped_user_role', 'octopusdeploy_variable')
    $kept = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $kept['octopusdeploy_variable.infrastructure_argocd_repo_read_credential[0]'] = 'set TF_VAR_argocd_repo_read_credential'
    $kept['octopusdeploy_variable.infrastructure_e2e_github_token[0]'] = 'set TF_VAR_e2e_github_token'

    # Writes <label>.tfplan, prints the planned actions and refuses a plan that deletes or replaces objects with history
    # unless deletes are allowed.
    function Invoke-PlanCheck([string]$Label, [bool]$AllowDeletes) {
        $planFile = Join-Path $workDir "$Label.tfplan"
        & $TerraformPath $chdir plan -input=false -no-color -parallelism=1 -var "repo_root=$repoRoot" @tfVarsArgs "-out=$planFile" | Out-Host
        $plan = (& $TerraformPath $chdir show -json $planFile) -join "`n" | ConvertFrom-Json -AsHashtable
        $changes = @(if ($plan.ContainsKey('resource_changes') -and $null -ne $plan['resource_changes']) { $plan['resource_changes'] })
        $counts = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
        $bad = [Collections.Generic.List[string]]::new()
        foreach ($change in $changes) {
            if ($change['mode'] -cne 'managed') {
                continue
            }
            $actions = @($change['change']['actions'])
            if ($actions.Count -eq 1 -and $actions[0] -ceq 'no-op') {
                continue
            }
            $action = $actions -join '/'
            $counts[$action] = 1 + $(if ($counts.ContainsKey($action)) { $counts[$action] } else { 0 })
            if ($actions -ccontains 'delete' -and $change['type'] -cnotin $replaceable) {
                $bad.Add("$($change['address']) ($action)")
            }
            elseif ($actions.Count -eq 1 -and $actions[0] -ceq 'delete' -and $kept.ContainsKey([string]$change['address'])) {
                $bad.Add("$($change['address']) ($action; $($kept[[string]$change['address']]))")
            }
        }
        $moves = @($changes | Where-Object { $_['previous_address'] }).Count
        $actionNames = [string[]]$counts.Keys
        [Array]::Sort($actionNames, [StringComparer]::Ordinal)
        $planned = if ($actionNames.Count -gt 0) { ($actionNames | ForEach-Object { "$_=$($counts[$_])" }) -join ', ' } else { 'no changes' }
        Write-Host "[$Label] planned: $planned; moved addresses: $moves"

        # R3, issue #42: the projects whose Git persistence settings change kind (Git credential to GitHub App connection
        # or back). A switch is an in-place update; a delete or replace of a project is refused above.
        $gitKinds = @{ git_github_app_persistence_settings = 'GitHub App connection'; git_library_persistence_settings = 'Git credential' }
        foreach ($change in $changes) {
            if ($change['mode'] -cne 'managed' -or $change['type'] -cne 'octopusdeploy_project') {
                continue
            }
            $before = $change['change']['before']
            $after = $change['change']['after']
            if ($before -isnot [hashtable] -or $after -isnot [hashtable]) {
                continue
            }
            $from = @($gitKinds.Keys | Where-Object { @($before[$_]).Count -gt 0 -and $null -ne $before[$_] } | ForEach-Object { $gitKinds[$_] })
            $to = @($gitKinds.Keys | Where-Object { @($after[$_]).Count -gt 0 -and $null -ne $after[$_] } | ForEach-Object { $gitKinds[$_] })
            if (($from -join ',') -cne ($to -join ',')) {
                Write-Host "[$Label] Git persistence of $($change['address']): $(if ($from) { $from -join ',' } else { 'none' }) -> $(if ($to) { $to -join ',' } else { 'none (unknown until apply)' })"
            }
        }
        if ($bad.Count -gt 0 -and -not $AllowDeletes) {
            Stop-Apply "refusing, the plan deletes or replaces: $($bad -join ', ') (ALLOW_DESTROY=1 overrides)"
        }
    }

    # Applies <label>.tfplan. Returns 0, or 2 when every error is an inconsistent-result error, or 1.
    function Invoke-ApplyPass([string]$Label) {
        $PSNativeCommandUseErrorActionPreference = $false
        $log = [Collections.Generic.List[string]]::new()
        & $TerraformPath $chdir apply -input=false -no-color -parallelism=1 (Join-Path $workDir "$Label.tfplan") 2>&1 | ForEach-Object {
            $line = "$_"
            $log.Add($line)
            Write-Host $line
        }
        if ($LASTEXITCODE -eq 0) {
            return 0
        }
        $errors = @($log | Where-Object { $_.StartsWith('Error: ', [StringComparison]::Ordinal) }).Count
        $inconsistent = @($log | Where-Object { $_.StartsWith('Error: Provider produced inconsistent result after apply', [StringComparison]::Ordinal) }).Count
        if ($errors -gt 0 -and $errors -eq $inconsistent) {
            return 2
        }
        return 1
    }

    Invoke-PlanCheck 'pass1' $AllowDestroy
    if ($PlanOnly) {
        Write-Host 'PLAN_ONLY=1: nothing applied.'
        exit 0
    }

    $result = Invoke-ApplyPass 'pass1'
    if ($result -eq 2) {
        Write-Host 'apply: only inconsistent-result errors (provider 1.20.0); planning and applying once more.'
        Invoke-PlanCheck 'pass2' $AllowDestroy
        $result = Invoke-ApplyPass 'pass2'
    }
    if ($result -ne 0) {
        Stop-Apply 'terraform apply failed (see the log above)'
    }

    & $TerraformPath $chdir output -no-color project_ids | Out-Host
    & $TerraformPath $chdir output -no-color env_sleep_triggers | Out-Host
    Write-Host 'Done. Next: the checks and API calls of docs/preview-octopus.md.'
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
