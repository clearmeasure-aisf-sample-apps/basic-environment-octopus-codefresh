# Consistency notes

Cross-slice state of the environment repo after the sleep/wake integration pass (ADR-IR33, keyless wake): every check and its outcome, and every finding with its fix or its owner. The integration-review queue items (`Q#`) and the design addenda that settle them (`ADR-IR1` to `ADR-IR32`, design §2.5) follow. Earlier passes are summarised at the end.

| Field | Value |
|---|---|
| Pass | **Sleep/wake integration pass** (chief-architect), after the sleep/wake pass. The app wake became keyless (project `platform-wake`); this pass aligned contracts, checks, Terraform and docs with it and closed S3, S4, S5 and S9 (158 implementation files plus the design record). |
| Runs | 2026-09-24T04:47Z `validate-all.sh all` with `CI=true` and `PLATFORM_BOT_AUTHORS` set to its placeholder (the audit matches no commit); `terraform` again with `TF_VALIDATE=true` (all three layers); negative fixtures for TB18, TB20, C13, C16 and C23 on a scratch copy; gitleaks on the tree with the repo config and with the default rules. Earlier: the sleep/wake pass, 04:09Z and 04:12Z. |
| Root | The environment-repo root (a local copy that becomes the first commits on `main`) |
| Tools | yamllint 1.38.0; kustomize v5.8.1; kubeconform 0.8.0 (`-strict -ignore-missing-schemas`, built-in schemas plus the datreeio CRDs catalog); terraform 1.16.4; gitleaks 8.28.0 with `.gitleaks.toml`; mermaid 11 parser; python3 3.11 with PyYAML 6.0.1; shellcheck 0.11.0 |
| Overall | 8 of 8 sub-commands pass. `consistency`: 120 pass, 0 fail, 2 warn (A1, A2, accepted), 0 skip. 22 of 22 boundary rules and the bot-path audit pass. All three Terraform layers validate. No leaks. |

Severity scale:
- **High**: blocks bootstrap, a phase exit or the release path.
- **Medium**: breaks one procedure, weakens a control, or needs a decision before its phase.
- **Low**: naming, documentation or design-text alignment.

## Sleep/wake integration pass (keyless wake)

After the sleep/wake pass, the app wake became keyless: step 0 of the `workorders` process is a built-in Deploy a Release of the new platform-owned project `platform-wake` (`.octopus/platform-wake/*`), whose one step runs `env-wake` with the key; the `workorders` runbooks got a keyless wait guard; library set `WorkOrders Platform Automation` left project `workorders`. User rule: platform secrets never enter app projects. The chief-architect's integration pass brought the contracts, checks, Terraform, docs and design (ADR-IR33, §7) in line. Threat model (user decision, 2026-09-24, ADR-IR33): the user is the only operator of the Octopus space and the Codefresh account, so the library set in `platform-wake`, the stored variable sets and the key in both gateways are accepted.

| Area | Change |
|---|---|
| Contracts | Project `platform-wake` (owner `platform`, group `Platform`, lifecycle `platform-wake`), `owner` on every project, step 0 as `Octopus.DeployRelease` of `platform-wake` (condition Always), `sleepWake.wakeProject`, `appProjects`, `platformProjects`, `wakeForms` (`deployRelease`, `waitGuard`, `runbookRun`), `runbookRunCallers`, `credential` (library set in `platform-wake` only, step-scoped variable in `workorders-infrastructure`), `workerMachinePolicy.name`, Deployment Creator on `platform-wake` for the deploying teams, the release handoff's `packagesAtVersion` with `PACKAGE_VERSION` forbidden, the foundation's Owner-only resources, cost figures with the foundation and private endpoints, ownership of the preview pages and `docs/owner/` |
| Checks | TB18: runbook runs only in the `workorders-infrastructure` runbooks, `platform-wake` step `run-env-wake` and `wake_nonprod`. TB20: the key only in `run-env-wake` and the four scoped steps, never in project `workorders`. C13: every `Octopus.*` action type, the Deploy a Release project and condition. C15: the `Platform` project group. C16: explicit `PACKAGES`, no `PACKAGE_VERSION`, `platform-wake` never pinned. C23: the three wake forms (keyless in app projects), `platform-wake` itself (one step, literal URL and space, no project variables), the library set only in `platform-wake`, the step scope against the runbooks, the machine policy name and its assignment |
| Terraform | `octopus/terraform`: `workorders-infrastructure` no longer includes the library set; sensitive project variable `Platform.OctopusApiKey` scoped to processes `env-wake`, `env-sleep`, `env-plan`, `env-apply`, `env-destroy`, `rotate-sql-passwords` and steps `wake-environment`, `wait-for-workers-and-gateway`, `decide-sleep`, `stop-cluster` (S5). `terraform/environment`: `agent.machinePolicyName` from new variable `octopus_worker_machine_policy`, default `Sleep-tolerant Kubernetes workers` (S9). `terraform/foundation`: notes on the Owner-only resources and the two-pass apply (ADR-D10 status) |
| Codefresh | `workorders/release` passes `PACKAGES` (each package at `VERSION`) and no `PACKAGE_VERSION`, so the `platform-wake` release that step 0 selects resolves to the latest (ADR-IR33 risk 12) |
| OCL | Error messages and comments of the `workorders-infrastructure` runbooks name the step-scoped key |
| Review | `CODEOWNERS`: security owners alone for `.octopus/workorders-infrastructure/variables.ocl`, `.octopus/platform-wake/`, `octopus/terraform/library-variable-sets.tf` and `docs/owner/` (S4) |
| Docs | Runbook `sleep-and-wake.md` (S3: on-call roles, `Sleep.Force`, keyless forms, costs, risks), bootstrap (provisioner and Owner passes, `platform-wake` release, step-scoped key, revocation at step 10), tool boundaries, credential rotation, database restore, rollback, cutover, README, `codefresh/workorders/README.md`, labs 01, 05 and 06 |
| Design | ADR-IR33 (keyless forms, `platform-wake`, credential scope, threat model, risks 12–14), ADR-D10 status (two-pass apply, recommended split), §3.2, §4, §5.2, §6.1, §6.2, §7.2, §7.7, §7.10, §9, R5, R6, §11 counts (158 files), §12 Q26 and Q29, new Q31–Q35, E53 |

| Finding | Severity | State after the pass |
|---|---|---|
| I1: a default `PACKAGE_VERSION` in the release handoff would also apply to the `platform-wake` release of step 0 and fail release creation [VERIFY] | High | Resolved in files: explicit `PACKAGES`; C16 forbids `PACKAGE_VERSION` |
| I2: C23's machine-policy check matched `machinePolicyName` inside a variable description, so a missing assignment passed | Medium | Resolved: the check requires an assignment (`machinePolicyName =`) and the policy name |
| I3: the foundation's Owner-only resources (locks, policy and PIM-eligible assignments) cannot be created by the provisioner after R6 | Medium | Decided: two passes on the same state now; a separate `terraform/foundation-owner` root recommended before P4 (sre-security) |
| I4: `rotate-sql-passwords`, `env-apply` and `env-destroy` run in-cluster or Terraform steps that would see a project-wide key | Medium | Resolved with S5: the key is scoped to four steps [VERIFY scope IDs, design §12 Q26] |

Negative fixtures, run on a scratch copy before the final run: the key in the app process's step 0, a runbook-run call in `db-backup`, the key in an in-cluster step, the library set on the app project, the step scope removed, the Octopus URL read from a variable in `platform-wake`, `DeploymentCondition` changed, `PACKAGE_VERSION` restored and `machinePolicyName` removed. TB18, TB20, C13, C16 and C23 failed on each one.

## Sleep and wake pass (ADR-IR33)

User directive, approved: sleep by default, wake on the first Codefresh or Octopus job. Each package implemented its part of the binding sleep/wake contract in parallel; the design record carries the decision as ADR-IR33 (§3.4 costs, §7.2 names, §9 phase notes, open questions §12 Q23–Q29). Here `§12 Q#` names a design open question; a bare `Q#` names an integration-review queue item.

### Changes by package

| Package | Change |
|---|---|
| octopus-architect | Runbooks `env-wake` and `env-sleep`. Step `wake-environment` first in the `workorders` process and in seven runbooks; the three Terraform runbooks skip it while no cluster exists. Variables `Sleep.*`, `Wake.TimeoutMinutes`, prompted `Sleep.Force` and `Octopus.Task.ConcurrencyTag` (`cluster-power/#{Octopus.Environment.Id}`, scoped to the two runbooks). In `octopus/terraform`: library set `WorkOrders Platform Automation`, triggers `env-sleep-hourly-{nonprod,prod}` behind `runbook_triggers_enabled`, machine policy `kubernetes_workers`, and `SRE On-call` as Runbook Consumer on `workorders-infrastructure`. The preview script passes the new key variable |
| codefresh-engineer | `wake_nonprod` in `workorders/release`: stage `wake`, `fail_fast: false`, one fire-and-forget request; a commented wake step in `workorders/preview` |
| sre-security | `apr-sleep-<cluster>` in `terraform/environment/monitoring.tf` (created disabled, `ignore_changes = [enabled]`, in `rg-workorders-aks-<cluster>`); `docs/runbooks/sleep-and-wake.md` |
| pragmatist | Contracts: `sleepWake`, triggers, library set, `wakeStep`, `alertProcessingRule`, `Sleep.Force`, the concurrency tag, on-call roles, §3.4 costs. Checks: the TB14 exception for `workorders/release`, TB17–TB20, C23, and changes to C12, C13 and C14. Docs: README, bootstrap steps 3, 3b and 5, tool boundaries, cutover P1, P2 and P4, labs 18–22 and the new lab 23 |
| chief-architect | ADR-IR33, §3.4, §7.2, §9 and §12 Q23–Q29 in `design/platform-design.md` |

### New and changed checks

| Check | Rule |
|---|---|
| TB14 | An Octopus API key appears only as `OCTOPUS_API_KEY` or the `X-Octopus-ApiKey` header, and only in `codefresh/workorders/pipelines/release.yml` (the release handoff and `wake_nonprod`, ADR-IR32). The ban stays everywhere else in `codefresh/` and `containers/` |
| TB17 | `az aks start` and `stop`, their PowerShell and REST forms, and alert-processing-rule changes appear only in `env-wake.ocl` and `env-sleep.ocl` |
| TB18 | Octopus REST calls that run runbooks appear only in `workorders-infrastructure` runbooks, in step `run-env-wake` of `platform-wake` and in step `wake_nonprod` (integration pass: no longer in project `workorders`). The check reads the enclosing OCL step slug and the YAML key path |
| TB19 | No Azure start rights in project `workorders`: no lifecycle account in its files, no Azure account on its `wake-environment` steps, and no foundation grant to an Octopus deploy identity that could start a cluster |
| TB20 | `Platform.OctopusApiKey` and the API-key header appear in `.octopus/` only in step `run-env-wake` of `platform-wake` and in the four steps of `workorders-infrastructure` runbooks that the step-scoped variable reaches; never in project `workorders`; no literal key appears anywhere (integration pass) |
| C12 | Runbooks with Terraform are taken from the contract (`terraformDirectory`), no longer from an `env-` prefix, so `env-wake` and `env-sleep` need no Terraform directory |
| C13 | Thirteen steps, `wake-environment` first; every `Octopus.*` action type; the Deploy a Release project and condition (integration pass) |
| C14 | `Sleep.Force` and `Octopus.Task.ConcurrencyTag` are §7.2 variables; boolean values compare without case |
| C23 | Sleep and wake end to end: the two runbooks (pool, account, start or stop order against the rule toggle, variables, the task check, the re-read before the stop, the re-disable after a failed stop, `Sleep.Force`, a missing cluster); wake first in eight files (pool, the `env-wake` call, the key, no Azure account in `workorders`, every channel, environment coverage, the skip while no cluster exists, the wait); every other file that needs a cluster is listed; the never-wake runbooks; values per infrastructure environment; the concurrency-tag scope; triggers (schedule in both cron forms, time zone, runbook, environment; a schedule never wakes, applies or destroys); the library set, the sensitive variable and the apply-time key; on-call roles; the machine policy; `apr-sleep-<cluster>`; the Codefresh wake (early, non-blocking, nonprod only, `workorders/release` only) |

Fixture runs before the real files landed: the new rules failed on planted violations and passed on corrected fixtures. On the real tree, C13, C14 and C23 failed until the octopus-architect's files arrived, then passed.

### Findings

| ID | Finding | Severity | Owner | State |
|---|---|---|---|---|
| S1 | `env-sleep` left `apr-sleep-<cluster>` enabled when `az aks stop` failed (ADR-IR33 rule 5, risk 9) | Medium | octopus-architect | Resolved in files: the stop step disables the rule again and fails the run |
| S2 | The concurrency tag differed from §7.2 (`#{Octopus.Project.Id}/#{Octopus.Environment.Id}/env-wake` on `env-wake` only) | Low | octopus-architect | Resolved in files: `cluster-power/#{Octopus.Environment.Id}` on `env-wake` and `env-sleep` |
| S3 | `docs/runbooks/sleep-and-wake.md` says that `SRE On-call` holds no role on `workorders-infrastructure` and that `env-sleep` has no override. §7.2, `teams.tf` and the OCL give on-call Runbook Consumer there and the prompted `Sleep.Force`. Lab 23, `tool-boundaries.md` and the README follow §7.2 | Low | sre-security | Resolved in the integration pass: the runbook follows §7.2 |
| S4 | The runbook asks security owners to review `Sleep.*` changes for `infra-prod`, but `CODEOWNERS` sends `.octopus/workorders-infrastructure/variables.ocl` to `platform-owners` only. The file also holds `Azure.LifecycleAccount`. Fix: a `CODEOWNERS` line for that file with `@<org>/security-owners` alone, or drop the claim. `CODEOWNERS` is outside this pass's files | Low | pragmatist (`CODEOWNERS`, needs approval), sre-security | Resolved in the integration pass: `CODEOWNERS` gives security owners alone that file and `.octopus/platform-wake/` |
| S5 | `Platform.OctopusApiKey` reaches every step of both projects through the library set, including steps that run app code (ADR-IR33 risk 6). TB20 checks files, not runtime access | Medium | chief-architect (integration pass, §12 Q26), octopus-architect | Resolved: project `workorders` holds no key (keyless wake); `platform-wake` has one step; `workorders-infrastructure` gets a step-scoped variable [VERIFY scope IDs, §12 Q26] |
| S6 | The idle clock skips `env-sleep` and `provisioner-credential-check` runs by task description. If descriptions do not name the runbook, the hourly runs count as activity and nonprod stays awake through every working window (about $215 a month instead of about $100, §3.4) | Medium | octopus-architect | [VERIFY] in the P2 drill; recorded as design §12 Q34 and ADR-IR33 risk 14 |
| S7 | The forced-sleep log names the user from `Octopus.Deployment.CreatedBy.*`. The system-variables page lists `Octopus.RunbookRun.*` without `CreatedBy` | Low | octopus-architect | [VERIFY] in the OCL; P2 drill; recorded as design §12 Q35; the runbook names the task history as the audit record |
| S8 | `wake-environment` in `workorders` holds a task slot while its `platform-wake` deployment and `env-wake` run in two more, so each waiting deployment uses three slots (§12 Q29) | Low | octopus-architect | [UNVERIFIED] task cap |
| S9 | `octopus/terraform` creates machine policy "Sleep-tolerant Kubernetes workers" and expects `terraform/environment` to register each worker with it through the chart value `agent.machinePolicyName`. `terraform/environment/bootstrap.tf` does not set it, so the workers register under the default policy and fail health checks while their cluster sleeps [VERIFY the default policy's behaviour] | Medium | sre-security (`bootstrap.tf`, with the name from the octopus-architect's output `kubernetes_worker_machine_policy`) | Resolved in the integration pass: `agent.machinePolicyName` from `octopus_worker_machine_policy`; C23 passes |

### Contract gaps raised by this pass

| Gap | Settled by |
|---|---|
| The contract named the no-cluster skip for `env-plan` and `env-destroy` only, so the first `env-apply` would have failed at `wake-environment` | ADR-IR33 and §7.2: all three Terraform runbooks skip; contracts `skipWhenNoCluster`; OCL; bootstrap step 5 |
| `env-sleep` before any cluster exists (phase 1) | ADR-IR33 rule 1 and the OCL: "not found, nothing to stop"; bootstrap step 3b |
| The idle clock would count `env-sleep`'s own hourly runs | The OCL skips them (S6) |
| `env-apply` waits for an `env-wake` that would queue behind it on the default concurrency tag | ADR-IR33 risk 1 and the tag (S2); lab 23 P14; §12 Q23 |
| `wake-environment` runs before `prod-go-no-go`, so a rejected prod deployment still wakes prod | ADR-IR33: the start overlaps the approval; the cost is bounded (risks 5 and 10) |
| P2 latency and drills with a sleeping nonprod | §9: cold starts count; cutover P2 criteria 2 and 9 |
| The prod SLO while prod sleeps | §9: SLO windows count awake time only; cutover P4 criterion 3 |
| Triggers for runbooks stored in Git | §12 Q27 [VERIFY]; `runbook_triggers_enabled`; the manual fallback table in bootstrap step 3b |
| A long bootstrap session loses the cluster to `env-sleep` between portal steps | Bootstrap step 5: pause sleeping for `infra-nonprod` until step 9 |

### Manual cross-slice probes (sleep and wake)

| # | Interface | Outcome |
|---|---|---|
| M20 | `#{...}` and `get_octopusvariable` references in the 12 extracted script bodies against the variables | Pass: `Sleep.*`, `Wake.TimeoutMinutes`, `Sleep.Force` (infrastructure variables), `Environment.Class` (library set), `Platform.OctopusApiKey`, system variables, and the outputs `Sleep.Decision`, `Sleep.Reason`, `Wake.ClusterStarted`; `Octopus.Deployment.CreatedBy.*` is S7 |
| M21 | The runbook-run call in the eight `wake-environment` steps and in `wake_nonprod` | Pass: one endpoint, `/api/spaces/<space>/projects/<id>/refs%2Fheads%2Fmain/runbooks/env-wake/run/v1`, with the environment looked up by name ([VERIFY] early-access endpoint). The key travels in curl configuration on stdin (OCL) or a mode-600 header file (Codefresh), never in process arguments |
| M22 | Script bodies: `bash -n` and shellcheck 0.11.0 | Pass: 12 OCL bodies and `wake_nonprod`. Only finding: SC2016 (info) in `wake_nonprod`, a jq variable in single quotes, as intended |
| M23 | Names in the scripts against `terraform/environment`: `aks-workorders-<class>`, `rg-workorders-aks-<class>`, `apr-sleep-<class>` | Pass. `monitoring.tf` creates the rule in `rg-workorders-aks-<cluster>`, the first group the scripts search, which settles their [VERIFY] on the group |
| M24 | Sleep and wake names across the README, bootstrap, tool boundaries, cutover, labs 18–23, the runbook and the design | Pass except S3 and S4 |
| M25 | Team roles for force-wake and force-sleep | Pass: `teams.tf` `sre-on-call-infra-runbook-consumer` matches §7.2 and the contracts (C23) |

## Package completeness

| Package | Files | Since the integration review | Roots |
|---|---|---|---|
| codefresh-engineer | 23 | +2: `codefresh/preview/register-preview.sh`, `docs/preview-codefresh.md` (phase 0 preview) | `codefresh/**`, `containers/**`, `docs/preview-codefresh.md` |
| gitops-architect | 38 | 0 | `argocd/**`, `gitops/**` |
| octopus-architect | 40 | +9: `env-wake.ocl`, `env-sleep.ocl`; `.octopus/platform-wake/*` (4); `octopus/preview/*` and `docs/preview-octopus.md` (phase 0 preview) | `.octopus/**`, `octopus/**`, `docs/preview-octopus.md` |
| sre-security | 40 | +2: `docs/runbooks/sleep-and-wake.md`; `docs/owner/Grant-ProvisionerRights.ps1` (R6) | `terraform/**`, `policies/**`, `docs/runbooks/**`, `docs/owner/**`, `.gitleaks.toml` |
| pragmatist | 17 | +1: `docs/walkthroughs/06-sleep-and-wake.md` | `README.md`, `CODEOWNERS`, `.yamllint.yaml`, `contracts/**`, `scripts/**`, `docs/*.md` (other than the two preview pages), `docs/walkthroughs/**` |

## Results by check

### `validate-all.sh all`

| Sub-command | Result | Detail |
|---|---|---|
| `yaml` | PASS | 55 files; 1 warning (line of 202 characters, `gitops/workorders/envs/prod/config/kustomization.yaml:22`, A3) |
| `kustomize` | PASS | 6 builds: `gitops/workorders/envs/{tdd,uat,prod}`, `gitops/workorders/previews`, `policies/kyverno/overlays/{nonprod,prod}` |
| `kubeconform` | PASS | 107 resources in 25 files, 107 valid, 0 invalid, 0 skipped (placeholders replaced by `ph-*` stand-ins in scratch copies) |
| `terraform` | PASS | `fmt -check -recursive` on `terraform/` and `octopus/terraform/`; `init -backend=false` and `validate` for all three layers |
| `mermaid` | PASS | 10 Markdown files with diagrams, design record and lab 23 included |
| `boundaries` | PASS | 22 of 22 rules, `codefresh/` and `containers/` included; bot-path audit on `main`: 6 commits audited, none by `platform-bots` |
| `consistency` | PASS | 120 pass, 0 fail, 2 warn, 0 skip (below) |
| `secrets` | PASS | gitleaks 8.28.0 with `.gitleaks.toml`: no leaks, 1.73 MB; also clean with the default rules |

### Terraform validate

| Layer | Result | Providers resolved |
|---|---|---|
| `terraform/foundation` | PASS | azurerm 5.6.0, azuread 3.9.0 |
| `terraform/environment` | PASS | azurerm 5.6.0, helm 3.3.0, kubernetes 3.2.1, random 3.9.1 |
| `octopus/terraform` | PASS | octopusdeploy 1.20.0 |

### `tool-boundaries.sh`

All PASS:
- **Codefresh:** TB01 forbidden step types; TB02 no argocd, kubectl, helm or `az aks` access to app clusters; TB03 no GitOps Runtime or Promotions; TB14 no Octopus API key except `OCTOPUS_API_KEY` and the `X-Octopus-ApiKey` header in `workorders/release` (`codefresh/` and `containers/`); TB16 no commits or pushes.
- **Argo CD:** TB04 no Image Updater; TB05 no syncWindows; TB09 Octopus annotations only on the three named Applications, and no tenant annotation.
- **Desired state and images:** TB06 no `latest` (including `containers/`).
- **Octopus:** TB07 no kubectl, Helm or Kubernetes steps; TB10 Trigger sync off; TB11 Codefresh is the only release creator; TB12 no deployment targets on app clusters.
- **Terraform:** TB08 no role assignments, locks or policy assignments in `terraform/environment`.
- **Stored credentials:** TB13a the provisioner stays out of `workorders`; TB13b stored Codefresh contexts stay unattached; TB13c stored variable sets are included nowhere.
- **Kyverno:** TB15 no `mutateDigest`.
- **Sleep and wake (ADR-IR33):** TB17 cluster power and rule toggles only in `env-wake.ocl` and `env-sleep.ocl`; TB18 runbook-run REST calls only in `workorders-infrastructure` runbooks, `platform-wake` step `run-env-wake` and `wake_nonprod`; TB19 no Azure start rights in `workorders`; TB20 `Platform.OctopusApiKey` only in `run-env-wake` and the four scoped steps, never in `workorders`, and no literal key.

### `consistency.sh`

| Check | What it compares | Pass | Fail | Warn |
|---|---|---|---|---|
| C01 | Contracts parse; all sections present | 1 | 0 | 0 |
| C02 | `argo.octopus.com/*` only on named Applications; slugs equal Octopus environments | 3 | 0 | 0 |
| C03 | Named Applications: project, single Git source, path, destination, automated prune and self-heal, PruneLast, retry, no finalizer | 3 | 0 | 0 |
| C04 | AppProjects per cluster, locked `default`, kind allow-lists, roles | 2 | 0 | 0 |
| C05 | Namespaces and labels per cluster | 2 | 0 | 0 |
| C06 | Root Application `platform-root`; bootstrap values (120 s, `octopus` account, admin off, RBAC policies) | 4 | 0 | 0 |
| C07 | Kustomize `images[].name` = registry + Octopus package ID; untagged base images; no unknown image or package names anywhere, `containers/` included | 7 | 0 | 0 |
| C08 | Pin files have the exact §7.6 shape | 3 | 0 | 0 |
| C09 | Rendered overlays: namespace, pinned images, `/alive` probes, identities, ESO waves, uat/prod redirects | 3 | 0 | 0 |
| C10 | Connection strings start with `Server=`; no `Data Source=` | 1 | 0 | 0 |
| C11 | Key Vault names against every ExternalSecret; secret names in Octopus and Terraform | 7 | 0 | 1 (A1) |
| C12 | Runbook scopes, `env-wake` and `env-sleep` included; `env-destroy` in `infra-nonprod` only; no other destroy | 10 | 0 | 0 |
| C13 | Thirteen steps in order, `wake-environment` first, with scopes, packages, pools, containers and interlocks; no-target connectivity | 15 | 0 | 0 |
| C14 | Project variables against §7.2; no sensitive variable in Git; one toolchain image reference everywhere (ADR-IR11) | 3 | 0 | 0 |
| C15 | §7.2 names in `octopus/terraform` (52, project `platform-wake`, its lifecycle and the `Platform` group included); stored objects looked up, never created; no users, custom roles or OIDC identities | 3 | 0 | 0 |
| C16 | Codefresh specs and pipelines: runtimes, contexts, template (`main` of this repo), concurrency, steps, handoff with `RELEASE_NOTES_FILE` and explicit `PACKAGES` (no `PACKAGE_VERSION`), env-checks sub-commands, `CI=true`, `PLATFORM_BOT_AUTHORS` | 10 | 0 | 0 |
| C17 | Environment config overlays: base, namespace, literals, no `images:`, `bluegreen` unreferenced, `RemotableBus__ApiUrl` | 3 | 0 | 0 |
| C18 | Kyverno identity, image scope and modes; add-on paths | 5 | 0 | 0 |
| C19 | Terraform layers: state keys, grants, bootstrap files that exist | 2 | 0 | 1 (A2) |
| C20 | Previews ApplicationSet (phase 6) | 1 | 0 | 0 |
| C21 | Placeholders outside §7 and the contracts | 0 | 0 | 0 |
| C22 | Runbook `TF_VAR_*` names declared in `terraform/environment` | 3 | 0 | 0 |
| C23 | Sleep and wake end to end, keyless app wake and `platform-wake` included (sections above) | 29 | 0 | 0 |
| **Total** | | **120** | **0** | **2** |

### Manual cross-slice probes

M20 to M25 (sleep and wake) are in the section above. M01 to M19 are from the integration review; M09 was re-run for this pass as M24.

| # | Interface | Outcome |
|---|---|---|
| M01 | Every `#{...}` variable in `.octopus/**` against project, library-set, sensitive, prompted and output variables | Pass (53 names; `package.*`, `commit.*`, `build.*` are loop variables of the release-notes template) |
| M02 | Rendered kinds of each overlay against its AppProject allow-list | Pass (4 overlays) |
| M03 | Kyverno baseline against rendered workloads: resources, pinned tags, privileged, probes | Pass. The Worker is exempt from `require-probes` by label until WI-04. Preview images, the migrator included, get the ApplicationSet override `pr-{{.number}}-{{.head_sha}}`, which matches the `workorders/preview` tag `pr-<n>-<CF_REVISION>`. |
| M04 | Gateway registration names, environments and secret names against §7.2, §7.3 and §7.8 | Pass |
| M05 | Terraform seed of `argocd-repo-creds` against its ExternalSecret | Pass (`creationPolicy: Orphan`, `mergePolicy: Merge`); the seed now arrives from `ArgoCD.RepoReadCredential` |
| M06 | Octopus-issuer federated subjects in the foundation against account and feed subject keys | Pass |
| M07 | Foundation role matrix against §5.2 | Pass, with SQL DB Contributor in `uat` and `prod` (ADR-IR13) and PIM-eligible `secret-writers` (ADR-IR29) |
| M08 | Codefresh `${{...}}` references against contexts, exports and step outputs | Pass (5 pipelines) |
| M09 | Platform names in every Markdown doc against the contracts and the implementation | Pass: no stale names; no `app:.codefresh`, `app:containers`, `--app-repo` or staging path remains |
| M10 | `workorders/db-migrator` entrypoint and `/app/scripts` against the preview Job arguments | Pass |
| M11 | `configure-db-principals-<env>` user names and the `nServiceBus` schema against the UAMIs and migration `022` | Pass: idempotent on both sides |
| M12 | `build.ps1` (`BUILD_BUILDNUMBER` → `/p:Version`) against `verify-version` | Pass |
| M13 | `app-commit:` writer (`buildinfo.sh`, 40-hex, via `RELEASE_NOTES_FILE`) against the `report-commit-status` reader | Pass |
| M14 | Acceptance-step settings against the suite's configuration keys | Pass (`ApplicationBaseUrl`, `StartLocalServer`, `AcceptanceTests__AllowDestructiveReset`) |
| M15 | Channel package rules against process package references | Pass |
| M16 | Bootstrap copy of `platform-addons` against `projects.yaml` | Pass: identical in both clusters |
| M17 | Image users and read-only root filesystem against pod security contexts | Pass: `runAsNonRoot` (numeric `$APP_UID`), `/tmp` and `/app/.diagnostics` emptyDirs (ADR-IR31) |
| M18 | Chart-version placeholders across gitops, Terraform and the contracts | Pass: `<argo-cd-chart-version>` everywhere |
| M19 | Inline scripts: `report-commit-status` (GitHub App token) and `supply_chain` (Docker config from the context) | Pass: `bash -n` and shellcheck (only SC2050 on Octopus `#{...}` substitutions, expected) |

## Integration review queue: resolution

| Q# | Item | State | Fix and decision |
|---|---|---|---|
| 1 | `TF_VAR_*` names | Resolved | `TF_VAR_octopus_worker_registration_token`, `TF_VAR_argocd_repo_read_credential` in `env-{plan,apply,destroy}.ocl` (ADR-IR1); C22 passes |
| 2 | Kyverno PolicyExceptions off | Resolved | `features.policyExceptions.enabled: true`, `namespace: kyverno` in both add-ons (ADR-IR2); CEL kind [VERIFY] |
| 3 | Worker against `require-probes` | Resolved (already in files) | Match condition `worker-exempt-until-wi-04`, removed with WI-04 (ADR-IR3) |
| 4 | `policies/**` ownership | Resolved | Security owners alone: `CODEOWNERS`, §6.1, §6.2 (ADR-IR4) |
| 5 | Provider pins | Resolved | §7.10 and §11.4 say `azurerm ~> 5.6`, `azuread ~> 3.9` (ADR-IR5) |
| 6 | New placeholders; Docker feed | Resolved | Ratified in §7.1 and the contracts; feed `docker-hub` created in `octopus/terraform/feeds.tf`; preview attestor pins `<CF_PREVIEW_PIPELINE_ID>` (ADR-IR6); C21 clean |
| 7 | Per-UAMI client IDs | Resolved | `<client-id-of-<uami name>>` in gitops, add-ons and `terraform.tfvars.example` (ADR-IR7) |
| 8 | Preview migrator image | Resolved | `workorders-previews/db-migrator` built by `workorders/preview`, mapped by the ApplicationSet (ADR-IR8) |
| 9 | `octopus/terraform` state key | Resolved | `octopus-space.tfstate` (ADR-IR9) |
| 10 | Supply-chain registry credentials | Resolved | Secret context `workorders-release` with `ACR_TOKEN_NAME`, `ACR_TOKEN_PASSWORD`; step-local Docker config (ADR-IR10). Unblocks P1 |
| 11 | `StepImage.CiDotnet` home | Resolved | Per project, digest-pinned, equality checked by C14 (ADR-IR11) |
| 12 | Contract-file keys | Resolved | `promptedVariables`, `notationTokens`, `placeholderPatterns` ratified (ADR-IR12) |
| 13 | SQL DB Contributor in UAT | Resolved | Foundation default `["uat", "prod"]` (ADR-IR13) |
| 14 | Committed `{nonprod,prod}.tfvars` | Resolved | Accepted: the repo is private (R2); identifiers only, secrets via `TF_VAR_*` (ADR-IR14). Files are created at bootstrap step 2 |
| 15 | Bootstrap repo credential holder | Resolved | Read-only GitHub App; Octopus sensitive variable `ArgoCD.RepoReadCredential` plus the platform vaults (ADR-IR15) |
| 16 | Octopus System Manager | Superseded | No System Manager exists; the Space Manager key of `AISF-Service-Account` is the only credential and nothing needs system rights (ADR-IR32) |
| 17 | Approvers can override the freeze | Accepted risk | No custom roles (ADR-IR32): approvers hold Project Deployer on their environment; `sod-guard` and the override reason are the controls |
| 18 | Floating `octopuslabs/octopus-cli` | Decided, phased | Accepted for TDD-only phases; pinned freestyle steps before the P3 exit (ADR-IR18). Open: codefresh-engineer |
| 19 | Account-wide registry integrations | Resolved; residual risk accepted | Pull-only `acr-platform-pull`, digest-pinned step image, YAML only from `main` (ADR-IR19) |
| 20 | Worker chart roles forced to namespaced | Resolved | Ratified (ADR-IR20); upgrade behaviour [VERIFY] |
| 21 | `Octopus.ArgoCDUpdateImageTags` | Resolved | Adopted in the OCL, the contracts and §7.2 (ADR-IR21); export comparison [VERIFY] |
| 22 | Output variables keyed by step name | Resolved | §7.2 uses the name; `sod-guard` keeps the slug fallback (ADR-IR22) |
| 23 | `RELEASE_NOTES_FILE` | Resolved | §7.7, E18 and the contracts amended; C16 checks `RELEASE_NOTES_FILE` and the `app-commit:` writer (ADR-IR23) |
| 24 | Preview SQL certificate | Resolved | Trust option added to WI-07; no Service-name workaround (ADR-IR24) |
| 25 | `CreateNamespace` with no cluster-scoped kinds | Open [VERIFY], phase 6 | Fallback recorded (ADR-IR25). Owner: gitops-architect in the phase-6 spike |
| F (directive) | New app repo `20260923-001` | Resolved | Contracts `appRepo`, `GitHub.AppRepository`, OCI source label, the previews ApplicationSet, specs and clones, README, bootstrap, walkthroughs, runbooks, design §6, §7 |
| E, H | All-in-environment-repo layout | Resolved | ADR-D18 rewritten; §6, §7.5, §7.7, §11; check scripts drop `app:` and `--app-repo` and scan `containers/` |
| H | R3, R4, R5 status | Recorded | Design §10 statuses; `infra-nonprod` import documented in `docs/bootstrap.md` step 3 and `octopus/terraform/environments.tf` |
| I | Merge gate of the fork | Resolved | `codefresh/ci` required from day one; Actions stays off (ADR-IR26); contributor guidance in the README |
| I | R16 revisited | Resolved | Statuses-only GitHub App; `report-commit-status` mints an installation token (ADR-IR27) |

## Findings from earlier passes

| ID | Finding | State | Fix |
|---|---|---|---|
| F1 | Runbooks passed `TF_VAR_<worker-registration-token-variable>` | Resolved | Q1 |
| F2 | `RELEASE_NOTES_FILE` against §7.7 | Resolved | Q23 |
| F3 | Placeholders outside §7.1 | Resolved | Q6; `<name>`, `<object_id>` added to `notationTokens` |
| F4 | Per-UAMI client-ID placeholders spelled two ways | Resolved | Q7 |
| F5 | `StepImage.CiDotnet` in both projects | Resolved | Q11 |
| F6 | `octopus/terraform` backend key undecided | Resolved | Q9 |
| F7 | `policies/**` owned by both teams | Resolved | Q4 |
| F8 | Contract-file keys awaiting ratification | Resolved | Q12 |
| N1 | `db-restore-pitr` pool reset through `/_diagnostics/*`, which WI-02 removes | Resolved | The swap runs with the app at zero replicas; the reset step is gone (ADR-IR28; OCL, runbook doc, §7.2) |
| N2 | No human secret writer | Resolved | Entra group `secret-writers`, PIM-eligible Key Vault Secrets Officer (ADR-IR29; foundation, bootstrap, rotation runbook) |
| N3 | Bootstrap ordering: cluster OIDC issuer into the foundation; 24-hour group lag | Resolved | `docs/bootstrap.md` steps 2.5 and 5.5 |
| N4 | Image policies evaluated in the background without registry identity | Resolved | Background evaluation off for both image policies (ADR-IR30) |
| N5 | NServiceBus diagnostics under a read-only root | Resolved in files; [VERIFY] | `/app/.diagnostics` emptyDir (ADR-IR31); fatality proven in the phase-2 spike (gitops-architect) |
| N6 | Prod stays denied until the first prod deployment | Resolved | `docs/bootstrap.md` step 11.5 |
| N7 | `<argocd-chart-version>` spelling; unused `<kyverno-policies-chart-version>` | Resolved | Contracts use `<argo-cd-chart-version>`; the unused one is removed |
| N8 | Writers of `workorders-api-validation-key` and the OpenAI key | Resolved | §7.8 writer column, contracts `writer`, bootstrap step 5 |

## Accepted, no action

| ID | Finding | Owner | Evidence |
|---|---|---|---|
| A1 | No ExternalSecret for `argocd-sso-client-secret`: SSO uses federation (Q15 default; `entra.tf`) | gitops-architect | C11 warn |
| A2 | `terraform/environment` never names `environment-{class}.tfstate`: the runbooks pass `-backend-config=key=#{Terraform.StateKey}` | — | C19 warn |
| A3 | One line of 202 characters (a connection-string literal) | gitops-architect | yamllint warning |

## Open items and owners

Nothing below blocks P0. Each item has an owner and a phase.

| Item | Owner | When |
|---|---|---|
| Owner-only foundation resources: split them into `terraform/foundation-owner` (design ADR-D10 status); until then two passes on one state | sre-security | Before P4 |
| First `platform-wake` release, after replacing `<OCTOPUS_URL>` and `<octopus-space-id>` in its script (bootstrap step 3) | platform engineer | P1 |
| Keyless wake [VERIFY] items: the identity of the child deployment (§12 Q31), Deploy a Release in runbooks (§12 Q32), the CLI's default package version and the child release (§12 Q33), the scope IDs of the step-scoped key (§12 Q26) | octopus-architect | Phase 0 preview or P1 |
| Sleep and wake [VERIFY] items, proven in the P2 sleep-and-wake drill: runbook-run endpoint for Git runbooks, concurrency tag of runbook runs (§12 Q23), a paused deployment counts as Executing (§12 Q24), worker and Argo CD health endpoints (§12 Q25), triggers for Git runbooks and machine-policy settings (§12 Q27), task cap (§12 Q29), `--enabled` flag of `az monitor alert-processing-rule update`, task descriptions for the idle clock (S6), `Octopus.Deployment.CreatedBy.*` in runbook runs (S7) | octopus-architect, sre-security | P2 |
| Handoff on pinned freestyle steps (ADR-IR18) | codefresh-engineer | Before the P3 exit |
| Phase-2 spike [VERIFY] items: Argo CD action type and property keys (ADR-IR21), CEL `PolicyException` flags (ADR-IR2), `JsonEscape` in `EnvVariables` (Q21), agent upgrades under namespaced roles (ADR-IR20), NServiceBus diagnostics (ADR-IR31), token tag lock (Q6), the Codefresh OIDC `sub` | octopus-architect, sre-security, gitops-architect, codefresh-engineer | P2 |
| `CreateNamespace` without cluster-scoped kinds (ADR-IR25) | gitops-architect | P6 |
| Fork pull request shows `codefresh/ci` after a maintainer push (Q19) | codefresh-engineer | First external contribution |
| WI-01 to WI-13, including the WI-07 trust option and the WI-13 base-image pin | App team (R12) | Per §8 |
| User actions: R3 machine user or GitHub App; R4 variable-set scope and P2 retirement; R6, R7, R9, R10, R11, R15, R16, R18, R21, R22 (old bootcamp branch, proxy 403), R23, R24, R25, R26, R27 | User | Design §10 |

## Earlier passes (summary)

- **First pass (CN-01 to CN-16):** files staged, C16 and C18 green, `.gitleaks.toml` in use; CN-05 → Q1, CN-06 → Q15, CN-07 → Q14, CN-08 → Q13, CN-09 → Q11, CN-10 and CN-11 → Q6, CN-12 resolved in files (env-checks sets `CI=true` and guards `PLATFORM_BOT_AUTHORS`), CN-13 to CN-15 accepted (A1–A3), CN-16 → Q4. All resolved above.
- **Final cross-slice pass (before the review):** 7 of 8 sub-commands passed; consistency 82 pass, 4 fail (Q1 ×3, Q23), 21 warn. Every failure and warning is resolved above.
- **Integration review pass (2026-09-24T02:27Z and 02:32Z):** 8 of 8 sub-commands passed; consistency 87 pass, 0 fail, 2 warn (A1, A2); 18 of 18 boundary rules; every queue item settled by ADR-IR1 to ADR-IR32.

## Checker notes

- **Terraform validate.** `TF_VALIDATE=true` needs a short `TMPDIR` (provider plugins open Unix sockets with a path-length limit); CI runners use `/tmp`. A plugin cache (`TF_PLUGIN_CACHE_DIR`) avoids downloads.
- kubeconform validates scratch copies with `ph-*` stand-ins for placeholders; committed files are unchanged.
- C09 accepts `/ready` for readiness only (§7.9, WI-01).
- **Context-aware boundary rules.** TB17, TB18 and TB20 report each hit with its enclosing OCL step slug (braces counted outside strings, comments and heredocs) or YAML key path, so an allowed call in step `wake-environment` or `wake_nonprod` passes while the same line in any other step fails. Comment lines never count.
- **Partial trees.** Each implementer edited in parallel. A file whose top-level directory is absent is SKIP; a missing file in an existing directory is FAIL. The runs above started after the other packages had been quiet for three minutes.
- **Cron forms.** The contracts keep the five-field `0 * * * *` of design §7.2 and the six-field Octopus form `0 0 * * * *` (seconds first); C23 accepts either in `cron_expression`.
- Not checkable from files: console actions, Octopus database values, runtime identity misuse, and whether Octopus honours the step scope of `Platform.OctopusApiKey` at run time (S5, §12 Q26) ([tool-boundaries.md](tool-boundaries.md)).

## Reproduce

From the environment-repo root:

```bash
CI=true PLATFORM_BOT_AUTHORS='<platform-bots-author-regex>' \
  PATH="<tools>/bin:$PATH" MERMAID_VALIDATOR=<node script that parses mermaid blocks> \
  scripts/checks/validate-all.sh all
scripts/checks/tool-boundaries.sh                               # lint only
TF_VALIDATE=true TMPDIR=/tmp/tfv scripts/checks/validate-all.sh terraform   # downloads providers
gitleaks dir . --config .gitleaks.toml --redact
shellcheck -x scripts/checks/*.sh
```
