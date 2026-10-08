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

Shell (POSIX `sh`, or Bash where noted) stays only where pwsh is absent or its start-up time matters. Each
exception is listed with its reason in the rule; any other `.sh` file fails the check.

| Exception | Reason |
|---|---|
| `containers/apps/*/*/migrate.sh` | Entrypoints of migrator images built on .NET runtime images, which carry no pwsh |
| `terraform/tier/scripts/aks-token.sh` | Exec credential plugin of the Terraform Kubernetes providers, started for every client |
| Scripts inside Kubernetes manifests under `gitops/` | Run in SQL Server and other runtime images |
| One-line `commands` on third-party step images | Call the tool directly; anything longer becomes a `.ps1` on `platform/ci-dotnet` |
| `.claude/hooks/session-start.sh` | SessionStart hook of Claude Code on the web (Bash); installs the .NET SDK and pwsh, so it runs before pwsh exists |
| `docs/owner/*.ps1` | Run by an Azure Owner, also under Windows PowerShell 5.1; exempt from the 7.4 preamble only |

## The preamble

Every script starts like this; `validate-all.ps1 powershell` checks it. The blank line after `#Requires` matters:
`Get-Help` ignores a help block that directly follows it.

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
- **Web calls.** A script that calls `Invoke-RestMethod` or `Invoke-WebRequest` sets
  `$ProgressPreference = 'SilentlyContinue'` after the preamble: otherwise PowerShell writes a progress bar
  into the CI or Octopus log for every response. `validate-all.ps1 powershell` checks it.
- **Paths.** `Join-Path` and `$PSScriptRoot`; never `cd` without `Push-Location` and `Pop-Location`.
- **Data.** `ConvertFrom-Json -AsHashtable` and `ConvertTo-Json -Depth 20` instead of `jq`; objects
  instead of `awk` and `sed` over text.
- **Output.** `Write-Host` for the log that people read; `Write-Output` (or a return value) for data.
  Check results print `PASS`, `FAIL`, `WARN` or `SKIP` followed by the check name.
- **Exit codes.** `0` passed, `1` failed, `2` usage error, `3` skipped (a tool missing locally). With
  `CI=true` a missing tool is a failure.
- **Secrets.** Never on a command line: pass them through the environment, standard input or a private
  file (mode 0600, removed afterwards), and never print them. curl takes a header in its configuration on
  standard input (`"header = ..." | curl --config -`); az reads any argument value from standard input
  (`--password '@-'`) or a file (`'@<path>'`); cosign's `--identity-token` takes a path to a file holding
  the token. `SecretArgumentTests` (CAP-KIT-007) fails a script that passes a secret as an argument.
  `Set-StrictMode` catches the misspelt variable that would otherwise send an empty value.
- **GitHub App tokens.** A script that needs a GitHub token dot-sources `scripts/github/GitHubAppAuth.ps1` (`Resolve-GitHubToken`: pre-minted App token, an installation token minted from the App's private key, then `gh auth token`); no script reads a personal access token variable. The key is read from a file path or an environment variable (or passed in memory with `-PrivateKey`) and never printed, logged, written to disk or put on a command line. The Octopus step `report-commit-status` cannot dot-source repository files, so it carries the marked region of that helper (`# >>> scripts/github/GitHubAppAuth.ps1` ... `# <<<`) as an inline copy that a conformance test keeps identical (CAP-KIT-010, `docs/runbooks/credential-rotation.md` section 11). The conformance scripts use the second App, `aisf-conformance` (`-Prefix AISF_CONFORMANCE_APP`, `Resolve-GitHubAppToken`, one wrapper `codefresh/platform/scripts/conformance-github.ps1`): they mint a one-hour installation token per run, keep it in `GITHUB_TOKEN` of their own process and a mode-0600 `GITHUB_TOKEN_FILE`, and never fall back to another token (section 12 of the same runbook). One script takes no App token: `scripts/release/release-stall-check.ps1 -Issues` runs only inside its GitHub Actions workflow and uses that run's `GITHUB_TOKEN` from the environment (`contents: read`, `issues: write`), which needs no storing; with `-PullRequestBuilds` it also reads the open pull requests and commit statuses of this repository and the app repository, which are public data and need no permission ([tool-boundaries.md](tool-boundaries.md#release-stall-alert-the-alert-only-workflow)).
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
| C | Codefresh scripts of the apps, the platform and the templates, and `codefresh/register.sh` | Done (2026-09-25): every app, starter and platform script is `.ps1`; `cf_export` runs through a shell (it has no shebang line). Live: every pipeline ran on 2026-09-25 (sandbox and workorders ci and release, conformance-arm, conformance, conformance-destructive, env-checks, ci-image-dotnet, registry-retention as a dry run) |
| D | Octopus inline scripts and step templates | Done (2026-09-25): every script step and step template is PowerShell with `$PSNativeCommandArgumentPassing = 'Standard'`; runbooks and a platform-wake release (`0.0.2`) run live |
| E | Kit templates, docs, and TB23 in the tool-boundary rules | Done (2026-09-25): TB23's pending lists are empty, so a new shell script or Bash step fails the boundaries check; the preamble check also requires the blank line after `#Requires`. Stability run (2026-09-25, sleep and wake paused): Live run `r20260925t1034-ce1fc8e1`, 51 of 61 passed; two failures were test defects, fixed and passing live since (CAP-GIT-004 reads ESO's refusal from the `UpdateFailed` Event, CAP-KIT-008 skips Codefresh's empty `default` project); three failures and three inconclusive results need a sleeping tier (CAP-OCT-010, CAP-GIT-011, CAP-AZ-004); CAP-AZ-014 lacks read access to budgets at subscription scope and CAP-CF-005 the fork of R33. The uat and prod tests passed 10 of 10. The end-to-end pass (CAP-KIT-009) exposed a ci build race on its branch, fixed by creating the branch and its commit in one push (`ff1d2a9`); its rerun passed: conformance build `6ab681885431916a629a466c` (1 h 41 min), workorders ci `6ab68283721535efb055b2b1` and release `6ab689102379153f0a5f78eb`, `2.5.722` deployed to tdd, uat and prod (Deployments-54263, -54265, -54267), env-wake ServerTasks-11910007, -11910092 and -11910121 successful. Sleep and wake resumed afterwards (`a031767`); an env-sleep dry run on infra-nonprod (ServerTasks-11910251) decided `stay`. An env-sleep dry run on infra-prod (ServerTasks-11910391) also decided `stay` (active 44 minutes ago, limit 120). The hourly env-sleep stopped nonprod at 18:00 UTC (ServerTasks-11910766, idle 127 minutes) and prod the same hour (ServerTasks-11910765); both stayed Stopped at 19:00 and 20:00 UTC. Sleeping-tier run (conformance-arm `6ab6d97da300cf4d10230732`, conformance `6ab6df2c8ffa79c72cec6624`, run `r20260925t2032-10230732`, capability filter CAP-OCT-008 to -011, CAP-GIT-011, CAP-AZ-004, CAP-AZ-005): 13 of 16 passed, 3 inconclusive, none failed. Passed: CAP-OCT-009 (3 of 3), CAP-OCT-010 (3 of 4), CAP-OCT-011, CAP-GIT-011, CAP-AZ-005, CAP-AZ-004 (3 of 4), CAP-OCT-008 (tdd). Inconclusive: the nonprod asleep half of CAP-AZ-004 (env-sleep kept nonprod up for the early platform-wake deployment to tdd of the arm's sandbox release), and the prod force-wake of CAP-OCT-010 and the prod promotion of CAP-OCT-008 (an earlier test had woken prod, and the run's own sleep hold keeps env-sleep's rules from stopping it again). The one-cycle-per-tier harness (`6958a1f`, quiesce before the sleep, the arm's prod stop as the asleep phase) targets all three; its proof run follows. Proof (2026-09-26, harness `6958a1f` + `16b7732` + teardown `14496b5`, conformance-arm `6ab734be12c13d5efe9f1a56` 10.8 min, conformance `6ab73740718b2dc516616e17` tests 44.5 min against 2 h 36 min): 15 of 16 passed, none inconclusive; the three earlier inconclusive tests pass. One failure, a least-privilege finding since P1-03: CAP-AZ-005 `Should_RunEnvSleep_KyvernoWebhooksRegistered_StopTheCluster` gets 403 listing `validatingpolicies.policies.kyverno.io` (and `validatingwebhookconfigurations`) at cluster scope with AKS RBAC Reader; a read-only grant awaits the owner. The teardown now sleeps a tier the arm stopped even when an early wake ran first |

Known issue: a branch created in GitHub's web UI and committed to right after can leave a terminated ci build on
the head commit. A rerun of the ci build clears it.

TB23 held every shell script and Bash step still pending, as path globs, while the phases ran; both lists are now empty, and only the exceptions at the top of this page may stay shell.

## Lint and tests

- `scripts/checks/validate-all.ps1 powershell` runs PSScriptAnalyzer with
  [`PSScriptAnalyzerSettings.psd1`](../PSScriptAnalyzerSettings.psd1) over every tracked `.ps1` and
  checks the preamble. The inline Octopus scripts are parsed by the offline tests.
- Behaviour is tested from the .NET suite (`tests/Platform.Conformance.Offline`), which starts the
  scripts with `pwsh`, so every test result lands in the same TRX files.
