# Cutover and decommission

Checklists for phases P1 to P5 of design §9 (ADR-IR34). Each phase lists its entry conditions, the work, the exit criteria (all required) with the evidence that proves them, and how to reverse it. Provisioning is [bootstrap.md](bootstrap.md) (P1-01 to P1-13); a new app follows [onboarding.md](onboarding.md) and needs no phase of its own.

The phases concern app #1, `workorders`, the only app with a legacy path. The legacy path lives in `ClearMeasureLabs/bootcamp-palermo-workorders` (GitHub Actions, the legacy Octopus space, Container Apps). The new path builds `clearmeasure-aisf-sample-apps/20260923-001`, a copy of that repository. Changes to the legacy path are made by the user in the legacy origin, never by an agent (user directive).

Rules for every phase:
- **The legacy path stays live and untouched before P5.** The only earlier change to it is the approved single-migration-owner change inside the P4 maintenance window (R13).
- **One source of truth for application code.** App commits land in `20260923-001`; the build that goes to prod is always built there.
- **Every phase before P5 is reversed by stopping the new path.** Nothing in the legacy path depends on the new one.
- **Evidence lives where the action happened:** Octopus deployments, artifacts and interventions; Argo CD history; commits here; Azure activity logs; the conformance results branch of `<sandbox-app-repo>`.
- **A missed criterion extends the phase.** Criteria are not averaged or traded.

## P1 Platform provisioning and conformance

**Work.** [bootstrap.md](bootstrap.md), steps P1-01 to P1-13, in that order.

**Exit criteria.**

| # | Criterion | Evidence |
|---|---|---|
| 1 | Every non-explicit test green on five consecutive nightly runs (CAP-CF-005's live test may be Inconclusive until R33) | `results/<date>-<run-id>/summary.md` on branch `conformance-results` |
| 2 | The destructive suite passed once | Same, from `platform-env/conformance-destructive` |
| 3 | The end-to-end pass delivered a change to prod (CAP-KIT-009) | TRX of `EndToEndTests`; the Octopus prod deployment of the merge commit |
| 4 | Both app clusters Stopped for at least 90 % of the 19:00–07:00 hours | AKS activity log (`managedClusters/start`, `managedClusters/stop`) |
| 5 | Month-to-date spend within 1.2 times the §3.5 sleeping estimate | Budgets `budget-platform-*` |
| 6 | `platform-env/env-checks` active; `codefresh/ci` required on `master` of `20260923-001` | Branch rulesets |
| 7 | 10 consecutive master builds of app #1 pass every gate; a rerun of `release` creates no second release | Codefresh builds; Octopus releases of `workorders` |
| 8 | The build of record takes at most 1.2 times the legacy `build-linux` plus publish | Build durations |
| 9 | Images signed, tag-locked and verifiable with `cosign verify` against the app's release identity | Registry referrers; CAP-CF-006, CAP-CF-007 |

**Reverse.** Stop the new path: freeze `workorders` ([onboarding.md](onboarding.md), Freeze) and let the clusters sleep. The legacy path never depended on it.

## P2 TDD maturity for app #1

**Entry.** P1 exit.

**Work.**
- [ ] `tdd` deploys automatically (lifecycle `platform-standard`).
- [ ] WI-08 (opt-in destructive reset) merged in the app repository.
- [ ] Kyverno in Audit mode in nonprod for the workload baseline; the signer and registry-path rules enforce in prod from P1.
- [ ] Every [VERIFY] item not settled in P1 is proven or has a recorded fallback (design §12; the ledger with the proof of each is [§12.1](../design/platform-design.md#121-verify-ledger-for-p2-2026-09-25)).

**Exit criteria.**

| # | Criterion | Evidence |
|---|---|---|
| 1 | At least 20 consecutive TDD releases, at least 90 % green (legacy baseline 207 of 289, 72 %) | Octopus deployments of `workorders` to `tdd` |
| 2 | Median commit → verified TDD no worse than legacy, cold starts included (ADR-IR33) | Octopus deployment timestamps, with the `wake-environment` time of each; legacy `deploy.yml` run times |
| 3 | Drill: a failed migration keeps the old version serving and fails the deployment | CAP-GIT-010 on the sandbox, then once on `workorders-tdd`: PreSync Job `db-migrate` failed; old ReplicaSet serving |
| 4 | Drill: drift self-heals | CAP-GIT-001; Argo CD history of `workorders-app-tdd` |
| 5 | Drill: redeploying the previous release completes in under 15 minutes from an awake cluster | CAP-OCT-007; Octopus deployment duration, with the wake time shown separately |
| 6 | `platform/tdd` reported on app commits (once the statuses-only GitHub App exists, R16) | Commit statuses on `20260923-001` |
| 7 | 14 days of Kyverno audit without false denies | Policy reports in nonprod |
| 8 | A job that lands just after a sleep succeeds | `env-sleep` and `env-wake` task logs; [runbooks/sleep-and-wake.md](runbooks/sleep-and-wake.md) |

**Reverse.** Freeze `workorders` and apply it ([onboarding.md](onboarding.md), Freeze). Legacy TDD is unaffected: it has its own database and path.

## P3 UAT on AKS and the Worker

**Entry.** P2 exit.

**Work.**
- [ ] UAT deployments with `uat-signoff` (team `UAT Approvers`).
- [ ] Worker enabled in `tdd` and `uat`: `replicas` above 0 in `gitops/apps/workorders/envs/<env>/app/config`, by pull request.
- [ ] SLO fast-burn alert `slo-fast-burn-workorders-uat` live ([runbooks/slo-fast-burn.md](runbooks/slo-fast-burn.md)).
- [ ] UAT data from WI-07 (seed) or a sanitized copy (Q8).
- [ ] WI-12 merged (no `user.name` metric tag).
- [ ] The Octopus handoff moves to pinned freestyle steps (ADR-IR18).

**Exit criteria.**

| # | Criterion | Evidence |
|---|---|---|
| 1 | Two UAT cycles approved in Octopus | `uat-signoff` interventions with responsible users |
| 2 | UAT smoke is blocking | Octopus process and variable history |
| 3 | The Worker runs 14 days in UAT with no growth in error or dead-letter queues | Queue depth queries in `log-platform-nonprod` |
| 4 | Octopus Insights shows lead time | Project Insights |
| 5 | No floating image runs in `workorders/release` | Pipeline YAML; tool-boundary rules TB06 |

**Reverse.** Set the UAT Worker to `replicas: 0` by pull request and stop UAT deployments on the new path. Legacy UAT keeps serving; the new UAT database is separate.

### Worker enablement (prepared 2026-09-25)

The Worker (`src/Worker` of `20260923-001`, NServiceBus endpoint `WorkOrderProcessing`) is already an image of deployable `app` of `workorders`, not a deployable of its own: one Octopus release and one pin commit move `ui-server`, `worker` and `db-migrator` together, so the Worker always runs the build of the UI it calls. Enabling it changes replicas only.

| Part | State | Where |
|---|---|---|
| Descriptor | `deployables[app].images` lists `worker` | `apps/workorders.yaml` |
| Image build | `worker_image` builds, signs and locks `apps/workorders/worker:<VERSION>` from `dotnet publish src/Worker` | `codefresh/apps/workorders/pipelines/release.yml`, `scripts/stage-built.ps1`, `containers/apps/workorders/worker/Dockerfile` |
| Release | Package `apps/workorders/worker` with an explicit version; the image-tag step pins it | `codefresh/apps/workorders/pipelines/release.yml` (`--package`), `.octopus/apps/workorders/workorders/deployment_process.ocl` |
| Desired state | Deployment `worker` (probe-exempt until WI-04), `replicas: 0` in base and every overlay | `gitops/apps/workorders/app/base/worker.yaml`, `envs/<env>/app/config/kustomization.yaml` |
| Database | `workorders_app` holds `db_ddladmin`, so the endpoint's installers create the queue tables in schema `nServiceBus` | `envs/<env>/db/kustomization.yaml` |
| Quota | Requests with the Worker and a surge pod: at most 3.75 of 4 GiB (database 2 GiB, `ui-server` 2 × 512 MiB, Worker 2 × 256 MiB, migrator 256 MiB) | Tenant chart `quota.memoryGiB` |

Branch `p3/worker` of this repository holds the two enablement commits, one per environment, each setting the Worker's `replicas` patch to `1`: tdd first (ADR-D16 allows it from P2), then uat. Each merges as its own pull request; Argo CD scales the Worker on its next sync, with no Octopus release. Prod stays at `0` until product sign-off and WI-04 (probes). Before the uat commit merges:
- the Worker has run in tdd with the `/app/.diagnostics` mount and no start-up error (ADR-IR31, design §12.1);
- a source for the exit evidence exists (criterion 3). The Worker exports NServiceBus traces (source `NServiceBus.Core`), but no queue-depth metric: the meter `NServiceBus.Core.Pipeline.Incoming` is not added in `ChurchBulletin.ServiceDefaults`. Until an app work item adds it, the proxy in `log-platform-nonprod` counts failed message handling of the Worker per day [VERIFY the role name and span mapping on the first tdd run]:

  ```kusto
  AppRequests
  | where TimeGenerated > ago(14d) and AppRoleName == "Worker" and Success == false
  | summarize failed = count() by bin(TimeGenerated, 1d)
  ```

  The exact error-queue depth is `SELECT COUNT(*) FROM [nServiceBus].[error]` in `workorders-uat` [VERIFY the error queue name of `ClearHostedEndpoint`], read by a platform operator (break-glass path C), until the metric exists.


## P4 Prod cutover

**Entry.**
- [ ] P3 exit.
- [ ] WI-01, WI-02, WI-03 and WI-05 merged.
- [ ] `CanNotDelete` on `rg-platform-prod-data` (Owner script `-ApplyLocks`) and on the legacy resource groups.
- [ ] Kyverno Enforce in prod; impersonation and egress hardening decided.
- [ ] A custom domain (R35): the legacy host name cannot move to an sslip.io host.
- [ ] The full cutover rehearsed in UAT, including the database import (a bacpac of at most 10 GB, Q10).
- [ ] R29 decided: prod keeps sleeping only while it serves no real users. Before it does, a pull request sets `Sleep.Enabled` to `false` for `infra-prod`.

**Work.** In a maintenance window, follow the single-migration-owner procedure below, then keep the Worker in prod at `replicas: 0` until product sign-off. Pause sleeping for `infra-prod` for the window (`Sleep.Enabled` `false` by pull request), so the hourly `env-sleep` cannot stop the prod cluster between steps.

**Exit criteria.**

| # | Criterion | Evidence |
|---|---|---|
| 1 | The rehearsal succeeded | UAT rehearsal record with timings |
| 2 | A restore drill (the CAP-AZ-010 procedure, with prod-sized data) finished within the agreed RTO | `db-restore-workorders-uat` run; [runbooks/database-backup-and-restore.md](runbooks/database-backup-and-restore.md) |
| 3 | 14 days of prod SLO within budget; SLO windows count awake time only while prod sleeps (ADR-IR33) | Alert history of `slo-fast-burn-workorders-prod`; `env-wake` and `env-sleep` history |
| 4 | Rollback to the legacy path remains possible until P5 starts | Legacy Container Apps stopped but intact; reverse procedure rehearsed |

### Single-migration-owner procedure

At any moment exactly one delivery path may migrate a given production database. On the new path the migration owner is the PreSync Job `db-migrate` in `workorders-prod` (ADR-IR34 decision 1): it runs the app's migrator image before every rollout, so a sync of the same commit migrates nothing.

| Moment | Legacy prod database | New prod database (SQL Server Express in `workorders-prod`) |
|---|---|---|
| Before the window | The legacy path migrates it | Owned by the new path; holds no production data |
| During the window | Frozen; its migration owner is disabled | Receives the import; `db-migrate` finds the journal complete |
| After the window | Kept intact, read by nobody, until P5 | The only production database |

Steps:
1. Announce the window. Freeze legacy prod deployments and confirm no legacy deployment is running.
2. Disable the legacy prod migration owner through the approved change to `.github/**` or `.octopus/**` of the legacy origin, made by the user (R13). Record the change.
3. Start the write freeze: stop the legacy app from accepting writes.
4. Export the legacy prod database as a bacpac and import it into the SQL Server Express of `workorders-prod` with SqlPackage, from a one-off Job in `platform-backup`, as rehearsed. `db-restore` restores only native backups, and the `platform/db-tools-mssql` image carries no SqlPackage today [VERIFY: add it or use a rehearsed one-off image, Q10].
5. Verify the import: row counts per table and an identical DbUp journal (`SchemaVersions`).
6. In Octopus, deploy to `prod` the release built from the commit legacy prod runs (match the release's commit; the version schemes differ). The process runs `wake-environment`, `prod-go-no-go`, `sod-guard`, `read-deployment-secrets`, `pre-release-backup`, `update-argo-cd-image-tags` (the PreSync Job migrates, a no-op), `verify-version` and `smoke-test`.
7. Move the legacy host name to the prod ingress (`platform-gateway` in `platform-ingress`; certificate from `letsencrypt-http01`); verify from outside.
8. End the write freeze. Leave the legacy app stopped but intact.

**Reverse.**
- **Before step 7:** abort; re-enable the legacy migration owner (revert the step-2 change); lift the freezes. The new prod database is discarded (`db-restore` from the pre-window backup, or a fresh disk).
- **After step 7:** freeze prod deployments in Octopus (a temporary freeze); start a write freeze; export the new prod database as a bacpac and import it into the legacy server; revert the step-2 change; move the host name back; lift the freeze. This works only while every migration applied since cutover is expand-only, so the legacy build runs on the newer schema ([walkthrough 02](walkthroughs/02-schema-change.md)).

## P5 Decommission the legacy path

**Entry.** 30 days after cutover with a change-failure rate no worse than legacy; approvals for each change below (R13).

**Work, in order.** Each step is reversible until a deletion.
- [ ] Disable `deploy.yml` and the legacy publish jobs in the legacy origin (approved change, by the user).
- [ ] Retire the legacy Octopus project and the legacy origin's `.octopus/` (approved change, by the user).
- [ ] After data retention: remove the locks on the legacy resource groups (Owner), then delete the Container Apps and the legacy resource groups. Never `NetworkWatcherRG` or `ai-model`.
- [ ] Delete the `OCTO_API_KEY` and `AZURE_CREDENTIALS` secrets and the legacy `AzureAccount`.
- [ ] Decide the AI Software Factory contract (Q9). The required check is already `codefresh/ci` (ADR-IR26).
- [ ] Apply WI-06 (installers out of the app's runtime rights).
- [ ] Update the app repository's `docs/` (pull request to `20260923-001`) and the docs here.

**Exit criteria.**

| # | Criterion | Evidence |
|---|---|---|
| 1 | No consumer of legacy artifacts remains | Registry pull logs; no workflow references the legacy packages |
| 2 | Legacy secrets are deleted | GitHub repository secrets; legacy Octopus space |
| 3 | The docs are updated | Merged pull requests |

**Reverse.** Before any deletion, re-enable the workflows and the legacy project from Git history. After the Container Apps and legacy resource groups are deleted, no rollback to the legacy path exists; that is why the 30-day gate and the disable-then-delete order come first.
