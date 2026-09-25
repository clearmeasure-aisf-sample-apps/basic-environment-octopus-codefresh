# Scripting standard: PowerShell 7

Every script of the platform is PowerShell 7 (`.ps1`, run by `pwsh`). The app repositories already
build with PowerShell (`build.ps1`), the conformance suite is .NET, and one language means one set of
habits, one linter and scripts that run the same on Windows, macOS and Linux.

## Where pwsh runs

| Place | Image or host | pwsh |
|---|---|---|
| Codefresh steps of the platform and the apps | `platform/ci-dotnet` | 7, with PSScriptAnalyzer |
| Octopus steps on `hosted-ubuntu` | `octopusdeploy/worker-tools` | 7 |
| Octopus container steps | `platform/ci-dotnet` (`#{StepImage.CiDotnet}`) | 7 |
| An operator's machine | Windows, macOS or Linux | 7.4 or later |

## Exceptions (tool-boundary rule TB23)

POSIX `sh` stays only where pwsh is absent or its start-up time matters. Each exception is listed with
its reason in the rule; any other `.sh` file fails the check.

| Exception | Reason |
|---|---|
| `containers/apps/*/*/migrate.sh` | Entrypoints of migrator images built on .NET runtime images, which carry no pwsh |
| `terraform/tier/scripts/aks-token.sh` | Exec credential plugin of the Terraform Kubernetes providers, started for every client |
| Scripts inside Kubernetes manifests under `gitops/` | Run in SQL Server and other runtime images |
| One-line `commands` on third-party step images | Call the tool directly; anything longer becomes a `.ps1` on `platform/ci-dotnet` |
| `docs/owner/*.ps1` | Run by an Azure Owner, also under Windows PowerShell 5.1; exempt from the 7.4 preamble only |

## The preamble

Every script starts like this; `validate-all.ps1 powershell` checks it.

```powershell
#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    One line on what the script does.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
```

`$PSNativeCommandUseErrorActionPreference` makes a failing `az`, `kubectl`, `git`, `docker` or
`terraform` stop the script, the way `set -e` did. When a non-zero exit code is expected, say so where
it happens:

```powershell
$PSNativeCommandUseErrorActionPreference = $false
$output = az aks show --resource-group $group --name $cluster --output json 2>&1
$found = $LASTEXITCODE -eq 0
$PSNativeCommandUseErrorActionPreference = $true
```

## Conventions

- **Names.** Script files keep kebab-case names (`validate-all.ps1`, `supply-chain.ps1`); functions use
  approved verbs (`Get-`, `Test-`, `Invoke-`, `New-`, `Set-`). Parameters are declared in `param()` with
  types, and required ones are `[Parameter(Mandatory)]`.
- **Paths.** `Join-Path` and `$PSScriptRoot`; never `cd` without `Push-Location` and `Pop-Location`.
- **Data.** `ConvertFrom-Json -AsHashtable` and `ConvertTo-Json -Depth 20` instead of `jq`; objects
  instead of `awk` and `sed` over text.
- **Output.** `Write-Host` for the log that people read; `Write-Output` (or a return value) for data.
  Check results print `PASS`, `FAIL`, `WARN` or `SKIP` followed by the check name.
- **Exit codes.** `0` passed, `1` failed, `2` usage error, `3` skipped (a tool missing locally). With
  `CI=true` a missing tool is a failure.
- **Secrets.** Never on a command line: pass them through the environment or standard input, and never
  print them. `Set-StrictMode` catches the misspelt variable that would otherwise send an empty value.
- **Octopus.** Every script that Octopus runs (inline OCL script steps and `octopus/step-templates/*.ps1`)
  adds `$PSNativeCommandArgumentPassing = 'Standard'` after `$ErrorActionPreference = 'Stop'`. Calamari
  starts scripts with Legacy argument passing, which strips the double quotes inside an argument, so a
  JSON body given to `curl --data` arrives broken (Octopus answers 400). The offline tests run the
  scripts under Legacy too, and `ScriptStepTests` requires the line.
- **Codefresh.** Steps run `pwsh -NoProfile -File <script>.ps1 <arguments>` on `platform/ci-dotnet`.
  Variables for later steps go through `cf_export NAME=value`, which Codefresh puts on `PATH` in every
  freestyle step.
- **Octopus.** Inline scripts use `Syntax = "PowerShell"`: read with `$OctopusParameters['Name']`, write
  with `Set-OctopusVariable`, report with `Write-Highlight` and `Write-Warning`, and stop with
  `Fail-Step`. A step template's script lives in `octopus/step-templates/<name>.ps1`, and every inline
  copy equals it (offline tests).

## Migration

The Bash scripts move to PowerShell in five phases; each phase ends with the pipelines, runbooks and
deployments it touched run live.

| Phase | Scope | State |
|---|---|---|
| A | This standard, `PSScriptAnalyzerSettings.psd1`, the `powershell` check, pwsh and the check tools in `platform/ci-dotnet` | Done |
| B | `scripts/checks/validate-all.ps1` and `scripts/diagrams/*.ps1`; `consistency.sh` and `tool-boundaries.sh` become Offline tests | Done (2026-09-25): `Kit/Consistency`, `Kit/Boundaries` and the bot-path audit; the three Bash check scripts are gone |
| C | Codefresh scripts of the apps, the platform and the templates, and `codefresh/register.sh` | In progress: `codefresh/register.ps1` done (2026-09-25); the app, starter and platform scripts pending |
| D | Octopus inline scripts and step templates | Pending |
| E | Kit templates, docs, and TB23 in the tool-boundary rules | Pending |

TB23 lists every shell script and Bash step still pending, as path globs; an app's copies of the starters are pending until the starters are converted, so onboarding keeps working. Each conversion removes its entries in the same change.

## Lint and tests

- `scripts/checks/validate-all.ps1 powershell` runs PSScriptAnalyzer with
  [`PSScriptAnalyzerSettings.psd1`](../PSScriptAnalyzerSettings.psd1) over every tracked `.ps1` and
  checks the preamble. The inline Octopus scripts are parsed by the offline tests.
- Behaviour is tested from the .NET suite (`tests/Platform.Conformance.Offline`), which starts the
  scripts with `pwsh`, so every test result lands in the same TRX files.
