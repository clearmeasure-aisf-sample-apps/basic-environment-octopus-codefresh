# Lab 22: Environment Lifecycle and On-Call Runbooks

**Curriculum Section:** Sections 06-07 (Operate/Execute & Reporting)
**Estimated Time:** 40 minutes
**Type:** Operate + Report
**Builds on:** Lab 10 (stability monitoring), Lab 21 (drift and rollback)

---

## Objective

Explain how the platform's tiers and each app's Azure objects are created, changed, rebuilt and destroyed, who may do each, and which identity acts. Connect the platform runbooks to an app's on-call runbooks and to the delivery metrics that show whether the platform is healthy.

The tiers are shared by every app; each app adds its own objects through one state per app (ADR-IR34 decision 12). App #1, `workorders`, is the worked example.

## Background

**Terraform layers** (design §7.0; every grant is the provisioner's, decision 3):

![Level 3: Terraform layers, their state and apply order](../../design/diagrams/c4-3-terraform-layers.png)

*Level 3, the Terraform layers, grouped by where their state lives. `terraform/foundation`, `terraform/build`, `terraform/apps/grants` and `octopus/terraform` run from operator sessions and keep state in `<tfstate-storage-account-global>`, which only the provisioner writes. `terraform/tier` and `terraform/apps/tier` keep one state per tier in `<tfstate-storage-account-<tier>>`, written by `id-platform-lifecycle-<tier>` through the env-* and apps-* runbooks. The numbers give the apply order and what each layer hands to the next: objects are found by name or passed through tfvars, never through remote state. `octopus/terraform` runs again after env-apply and after the grants, and apps-apply runs again after the grants.*

| Layer | State | Applied by | Creates | Never |
|---|---|---|---|---|
| `terraform/foundation` | `foundation.tfstate` (global) | The provisioner, from an operator session | Resource groups, the registry and scope maps, state and backup accounts, platform identities with their Octopus-issuer federated credentials, `sp-platform-conformance`, `platform-operators`, **every platform grant**, budgets | Runs from a pipeline |
| `terraform/build` | `build.tfstate` (global) | The provisioner | `aks-platform-build` | Any role assignment |
| `terraform/tier` | `tier-<tier>.tfstate` (tier) | `env-*` runbooks as `id-platform-lifecycle-<tier>` | Network, IPs, workspace, the cluster, the platform vault, `apr-sleep-<tier>`, workload federated credentials, the Argo CD bootstrap, Octopus workers | Role assignments, locks, policy (TB08) |
| `terraform/apps/tier` | `apps-<app>.tfstate` (tier) | `apps-*` runbooks, prompted `App.Name` | Per app and environment: vault and generated passwords, disk, backup container, App Insights and alerts | Role assignments (TB08) |
| `terraform/apps/grants` | `app-grants-<app>.tfstate` (global) | The provisioner, only for apps with Azure access | `rg-app-<app>-<tier>`, deploy and app identities, their grants | — |
| `octopus/terraform` | `octopus-space.tfstate` (global) | A Space Manager | Space objects and app project shells (`for_each` over `apps/*.yaml`) | — |

Grants are made in advance at resource-group scope, so resources a tier layer creates later inherit them, and identities survive a cluster rebuild.

**Runbooks** (project `platform-infrastructure`, environments `infra-nonprod` and `infra-prod`, account `azure-platform-lifecycle-<tier>`):

| Runbook | Environments | What it does |
|---|---|---|
| `env-plan` | Both | Plans `terraform/tier`; the plan is saved as an artifact |
| `env-apply` | Both | Plan → approval (always) → apply |
| `env-destroy` | `infra-nonprod` **only** | Approval → destroys the nonprod tier state; never a resource group, never a database disk |
| `apps-plan`, `apps-apply` | Both | Plan, then (apply) approval and apply of `terraform/apps/tier` for the prompted `App.Name` |
| `rotate-db-passwords` | Both | By hand, prompted `App.Name`, in each app environment of the tier: check that `sa` logs in to `db-0` with the mounted `db-sa` password → `<app>_migrator` and `<app>_app`, each new password into the app vault first, then `ALTER LOGIN` as `sa` (the old value goes back on failure) → login check of `<app>_app` → ESO refresh of `db-migrator` and `db-app` → restart of the app's own Deployments by name; then `sa` last: vault → `ALTER LOGIN` → ESO refresh of `db-sa` and, in uat and prod, its backup copy → annotation on `db-0` to remount `db-sa` → wait, up to 3 minutes, until `sa` logs in with the mounted password |
| `env-wake` | Both | Starts the tier's cluster if it sleeps and waits until it runs, then reports its workers' health without waiting for them (gap, tracked in [sleep-and-wake.md](../runbooks/sleep-and-wake.md)); run by `platform-wake`, by these runbooks' `wake-environment` steps, by `wake_nonprod` and by on-call ([06-sleep-and-wake.md](06-sleep-and-wake.md)) |
| `env-sleep` | Both | Hourly: stops the cluster outside the working window or after `Sleep.IdleMinutes` idle, never while a task runs |

**App runbooks** (app #1, project `workorders`): `db-restore` (`uat`, `prod`) and `run-acceptance-tests` (`tdd`). They wait for a sleeping cluster with `Wake.WaitMinutes` and never wake it themselves. Backups are CronJobs `db-backup-<app>-<env>` in `platform-backup` (uat and prod); the prod deployment also backs up before its pin. Human procedures are in [../runbooks/](../runbooks/): break-glass, rollback and forward fix, database backup and restore, credential rotation, SLO fast burn, sleep and wake, conformance.

## Steps (online)

A platform engineer drives; students observe the runbook runs in Octopus with the `Developers` role.

### Step 1: Plan

Run `env-plan` in `infra-nonprod`. Open the plan artifact. Classify each change: expected (a pull request changed `terraform/tier`), drift (someone changed Azure by hand), or surprise.

### Step 2: Apply

Run `env-apply` in `infra-nonprod`. Note where the run stops for the approval and who approves it.

### Step 3: An app's objects

Run `apps-plan` with `App.Name=workorders` in `infra-nonprod`. Compare the plan with `dotnet run --project tools/Platform.Onboarding -- render workorders`: vaults `kv-workorders-t-<hash4>` and `kv-workorders-u-<hash4>`, disks `disk-workorders-tdd-db` and `disk-workorders-uat-db`, the backup container `workorders-uat`, `appi-workorders-tdd` and `appi-workorders-uat`.

### Step 4: Rebuild thought experiment

Suppose `aks-platform-nonprod` must be rebuilt. List what `env-apply` re-creates, what survives, and what people must redo. Then compare with the answer key.

### Step 5: Read the health signals

- The fast-burn SLO alert `slo-fast-burn-<app>-<env>` in Azure Monitor, and its runbook `docs/runbooks/slo-fast-burn.md`.
- Octopus project Insights for `workorders`: deployment frequency, lead time, change-failure rate and time to recovery, per environment.
- The nightly conformance summary on branch `conformance-results` of `<sandbox-app-repo>`: failed capability IDs per area.

## Offline variant: predict every handoff

Work from `terraform/`, `.octopus/platform-infrastructure/`, `argocd/bootstrap/`, `argocd/clusters/nonprod/platform-secrets.yaml` and `contracts/platform-contracts.yaml`.

| # | Question | Prediction | Check in |
|---|---|---|---|
| L1 | Which runbooks exist for `infra-prod`, and which one deliberately does not? | | `.octopus/platform-infrastructure/runbooks/` |
| L2 | Which account and identity does `env-apply` use in `infra-nonprod`, and what can that identity not do? | | `.octopus/platform-infrastructure/variables.ocl`; design §7.0 Identities |
| L3 | Why does no tier layer contain a role assignment, and where are the grants it needs? | | ADR-IR34 decision 3; `terraform/foundation/` |
| L4 | Which files does `terraform/tier/bootstrap.tf` read, and what do they start on the new cluster? | | `argocd/bootstrap/{values,root-app}-nonprod.yaml` |
| L5 | During a nonprod rebuild: what changes about the cluster that forces new federated credentials? Which runbook re-creates those of an app's workload identity? | | ADR-IR34 consequences |
| L6 | After the rebuild, which secrets must a person put back into `<kv-platform-nonprod>`, and which come back on their own? | | `argocd/clusters/nonprod/platform-secrets.yaml`; [../bootstrap.md](../bootstrap.md) P1-07 |
| L7 | What does `env-destroy` leave behind, and why does app data survive it? | | `env-destroy.ocl` header; CAP-AZ-008 |
| L8 | Two apps run `apps-apply` in the same hour. Can one's failure block the other? | | ADR-IR34 decision 12 |
| L9 | An app needs a new secret. Which files change, in which pull request, and who writes the value? | | [../onboarding.md](../onboarding.md); `gitops/apps/workorders/app/base/secrets.yaml` |

<details>
<summary>Answer key</summary>

- **L1.** `env-plan`, `env-apply`, `apps-plan`, `apps-apply`, `rotate-db-passwords`, `env-wake` and `env-sleep`. There is no `env-destroy` for `infra-prod` (CAP-AZ-007).
- **L2.** `azure-platform-lifecycle-nonprod`, an OIDC account for `id-platform-lifecycle-nonprod`: Contributor on the nonprod `-shared`, `-aks`, `-data` and `-apps` groups, AKS RBAC Cluster Admin, Key Vault Secrets Officer and state access. It cannot create role assignments or resource groups, and it has no right in prod.
- **L3.** Only the provisioner holds the constrained RBAC Administrator; delegating it per tier would need an Owner (decision 3). The foundation creates every platform grant at resource-group scope in advance; `terraform/apps/grants` creates app grants.
- **L4.** `argocd/bootstrap/values-nonprod.yaml` for the `argo-cd` Helm release and `argocd/bootstrap/root-app-nonprod.yaml`, whose root syncs `argocd/clusters/nonprod`: the add-ons, the platform namespaces and the ApplicationSet `apps`, which renders every tenant from `apps/*.yaml`.
- **L5.** A new cluster has a new OIDC issuer URL. `env-apply` re-creates the platform workload federated credentials (ESO, Kyverno, backup); `apps-apply` those of each app's workload identity (decision 11). Identities, client IDs and grants stay, so the app manifests stay valid.
- **L6.** People put back what the new Argo CD instance issues: the API token for account `octopus` (`argocd-octopus-gateway-token`). The repo read credential and the gateway registration key are already in the vault and return through ESO; the app vaults are untouched.
- **L7.** The resource groups, the identities and grants (foundation), and the database disks, vaults and backups, which belong to `terraform/apps/tier` and live in `rg-platform-nonprod-data` and `rg-platform-nonprod-apps`. After `env-apply`, static PersistentVolumes bind the same disks again, so the data survives (CAP-AZ-008).
- **L8.** No: each app has its own state `apps-<app>.tfstate`, so neither plan nor failure couples to the other.
- **L9.** One pull request: a `secrets[]` entry in the descriptor (`generate: true`, or `false` with an operator-set value) and the ExternalSecret mapping in the app's own `base/`. After the merge, `apps-apply` in each tier writes the key; a member of `platform-operators` replaces the stand-in when the value is not generated. The descriptor schema rejects `db-*` names and the platform keys.

</details>

## Expected Outcome

- A lifecycle map: create, change, rebuild, destroy, with the actor and identity for each, for the tiers and for one app.
- A list of what survives a cluster rebuild and what must be redone by a person.
- A short report on the platform's delivery health from Insights, the SLO alert and the conformance summary.

## Discussion

1. Why is destroying prod not a runbook, even for an Owner?
2. What would a separate prod subscription (R19) change in the blast radius of `id-platform-lifecycle-nonprod`?
3. Which metric from Step 5 best shows that the new path is safer than the legacy one?
