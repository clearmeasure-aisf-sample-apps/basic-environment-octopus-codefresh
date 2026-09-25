# Cutover and decommission

Checklists for phases P1 to P5 of design §9 (ADR-IR34). Each phase lists its entry conditions, the work, the exit criteria (all required) with the evidence that proves them, and how to reverse it. Provisioning is [bootstrap.md](bootstrap.md) (P1-01 to P1-13); a new app follows [onboarding.md](onboarding.md) and needs no phase of its own.

The phases concern app #1, `workorders`, the only app with a legacy path. The legacy path lives in `ClearMeasureLabs/bootcamp-palermo-workorders` (GitHub Actions, the legacy Octopus space, Container Apps). The new path builds `clearmeasure-aisf-sample-apps/20260923-001`, a copy of that repository. Changes to the legacy path are made by the user in the legacy origin, never by an agent (user directive).

Rules for every phase:
- **The legacy path stays live and untouched before P5.** The only earlier change to it is the approved single-migration-owner change inside the P4 maintenance window (R13).
- **One source of truth for application code.** App commits land in `20260923-001`; the build that goes to prod is always built there.
- **Every phase before P5 is reversed by stopping the new path.** Nothing in the legacy path depends on the new one.
- **Evidence lives where the action happened:** Octopus deployments, artifacts and interventions; Argo CD history; commits here; Azure activity logs; the conformance results branch of `<sandbox-app-repo>`. Spend is the exception: the owner reads it in the Azure Sponsorships portal and commits the reading to that branch ([Spend evidence](#spend-evidence-criterion-5)).
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
| 5 | Month-to-date spend within 1.2 times the §3.5 sleeping estimate | Owner's reading of the Azure Sponsorships portal usage, committed as `results/cost/<yyyy-mm>/` on branch `conformance-results` ([Spend evidence](#spend-evidence-criterion-5)). Not budgets: the offer has none (CAP-AZ-014) |
| 6 | `platform-env/env-checks` active; `codefresh/ci` required on `master` of `20260923-001` | Branch rulesets |
| 7 | 10 consecutive master builds of app #1 pass every gate; a rerun of `release` creates no second release | Codefresh builds; Octopus releases of `workorders` |
| 8 | Like for like (owner decision of 2026-09-25): the build of record, excluding the acceptance (Playwright) gate, takes at most 1.2 times the legacy `build-linux` plus publish. The acceptance gate must pass but is timed separately, because the legacy publish jobs never waited for acceptance tests | Build durations, measured as in [runbooks/build-duration.md](runbooks/build-duration.md); the legacy baseline is still an estimate [VERIFY] |
| 9 | Images signed, tag-locked and verifiable with `cosign verify` against the app's release identity | Registry referrers; CAP-CF-006, CAP-CF-007; readings in [runbooks/supply-chain-evidence.md](runbooks/supply-chain-evidence.md) |

### Spend evidence (criterion 5)

Owner decision of 2026-09-25. The subscription is a sponsorship offer (quotaId `Sponsored_2016-01-01`). Cost Management budgets are unavailable on it, `terraform/foundation` skips them, and `sp-platform-conformance` holds no cost read ([bootstrap.md](bootstrap.md), P1-02, CAP-AZ-014). No principal of the platform can read spend. The evidence is the usage page of the Azure Sponsorships portal, <https://www.microsoftazuresponsorships.com/Usage>.

- **Who.** The owner, signed in with the Microsoft account that holds the sponsorship. Agents and the main loop cannot read the portal.
- **When.**
  - At P1 exit: one reading, taken when the other criteria are met.
  - Monthly: on the first working day of each month, for the closed month. The full-month figure also checks the budgets rule of design §3.5 (R18) while no budget exists.
  - The portal shows usage with a delay [VERIFY: how many hours; record the last usage date the page shows].
- **What to capture.**
  - The month-to-date total, the date range it covers and the currency.
  - The per-service breakdown, when the page shows one.
  - The page as an exported CSV (preferred) or a screenshot. Crop or remove the account holder's name, e-mail address and any payment details; the subscription ID may stay (it is already in `gitops/platform/tenant/values.yaml`).
- **Where.** Branch `conformance-results` of `<sandbox-app-repo>`, folder `results/cost/<yyyy-mm>/`, next to the nightly `results/<date>-<run-id>/` folders:
  - `<yyyy-mm-dd>-usage.csv` or `<yyyy-mm-dd>-usage.png`, the capture of that date;
  - `summary.md`, one row per reading: date, usage range, total, subtracted lines, platform total, threshold, pass or fail.
- **Comparison.** The target is the design §3.5 sleeping estimate for one app, ≈$220 a month, so the limit is 1.2 × $220 = $264 for a full month [UNVERIFIED estimate, design §3.5].
  - A month-to-date reading compares with the prorated limit: $264 × d ÷ D, where d is the number of days the usage covers and D the days in the month. Example: 15 of 30 days, limit $132.
  - The portal totals the whole subscription. Subtract, and name in `summary.md`, only lines the estimate excludes (design §3.4 and §3.5, Not included): Azure OpenAI and other AI services (`ai-model`), outbound data transfer (bandwidth), and any legacy-path resources billed to this subscription [VERIFY: whether the legacy Container Apps bill here]. Everything else counts, `NetworkWatcherRG` included.
  - Pass: the platform total is at or under the limit. Fail: criterion 5 is missed and P1 extends. A breach usually means a cluster did not sleep; check criterion 4 and the `env-sleep` history first.
- **Revisit.** On a pay-as-you-go or EA subscription the budgets return (CAP-AZ-014), and budgets `budget-platform-*` become the evidence again.

**Reverse.** Stop the new path: freeze `workorders` ([onboarding.md](onboarding.md), Freeze) and let the clusters sleep. The legacy path never depended on it.

## P2 TDD maturity for app #1

**Entry.** P1 exit.

**Work.**
- [ ] `tdd` deploys automatically (lifecycle `platform-standard`).
- [ ] WI-08 (opt-in destructive reset) merged in the app repository.
- [ ] Kyverno in Audit mode in nonprod for the workload baseline; the signer and registry-path rules enforce in prod from P1, and prod keeps enforcing the workload baseline too (owner decision of 2026-09-25, [P4](#p4-prod-cutover)).
- [ ] Every [VERIFY] item not settled in P1 is proven or has a recorded fallback (design §12; the ledger with the proof of each is [§12.1](../design/platform-design.md#121-verify-ledger-for-p2-2026-09-25)).
- [ ] The live PolicyReport test for criterion 7 added to the conformance suite, only after P1 criterion 1 (five consecutive green nightlies) is met, so it cannot turn the nightly count red (owner decision of 2026-09-25). Until then criterion 7 is read from the nonprod PolicyReports by hand.

**Exit criteria.**

| # | Criterion | Evidence |
|---|---|---|
| 1 | At least 20 consecutive TDD releases, at least 90 % green (legacy baseline 207 of 289, 72 %) | Octopus deployments of `workorders` to `tdd` |
| 2 | Median commit → verified TDD no worse than legacy, cold starts included (ADR-IR33) | Octopus deployment timestamps, with the `wake-environment` time of each; legacy `deploy.yml` run times |
| 3 | Drill: a failed migration keeps the old version serving and fails the deployment | CAP-GIT-010 on the sandbox, then once on `workorders-tdd`: PreSync Job `db-migrate` failed; old ReplicaSet serving |
| 4 | Drill: drift self-heals | CAP-GIT-001; Argo CD history of `workorders-app-tdd` |
| 5 | Drill: redeploying the previous release completes in under 15 minutes from an awake cluster | CAP-OCT-007; Octopus deployment duration, with the wake time shown separately |
| 6 | `platform/tdd` reported on app commits (once the statuses-only GitHub App exists, R16) | Commit statuses on `20260923-001` |
| 7 | 14 days of Kyverno audit without false denies | Policy reports in nonprod. The live PolicyReport test is deferred until P1 criterion 1 (five consecutive green nightlies) is met, so it cannot turn the nightly count red (owner decision of 2026-09-25) |
| 8 | A job that lands just after a sleep succeeds | `env-sleep` and `env-wake` task logs; [runbooks/sleep-and-wake.md](runbooks/sleep-and-wake.md) |

**Reverse.** Freeze `workorders` and apply it ([onboarding.md](onboarding.md), Freeze). Legacy TDD is unaffected: it has its own database and path.

## P3 UAT on AKS and the Worker

**Entry.** P2 exit.

**Work.**
- [ ] UAT deployments with `uat-signoff` (team `UAT Approvers`).
- [ ] Worker enabled in `tdd` and `uat`: `replicas` above 0 in `gitops/apps/workorders/envs/<env>/app/config`, by pull request. tdd goes live ahead of P3 through commit `4a15529` of branch `p3/worker` (owner decision of 2026-09-25); uat (`96a3dfb`) waits for P3 ([Worker enablement](#worker-enablement-prepared-2026-09-25)).
- [ ] SLO fast-burn alert `slo-fast-burn-workorders-uat` live ([runbooks/slo-fast-burn.md](runbooks/slo-fast-burn.md)). Owner decision of 2026-09-25: in nonprod the alerts go live in uat only (tdd stays off), when P3 starts; branch `p3/slo-alerts` (`terraform/apps/tier/nonprod.tfvars`) merges then, not before.
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

Branch `p3/worker` of this repository holds the two enablement commits, one per environment, each setting the Worker's `replicas` patch to `1`: tdd first (ADR-D16 allows it from P2), then uat. Each merges as its own pull request; Argo CD scales the Worker on its next sync, with no Octopus release. Prod stays at `0` until product sign-off and WI-04 (probes).

| Commit | Environment | State (owner decision of 2026-09-25) |
|---|---|---|
| `4a15529` | tdd | Goes live now, ahead of P3; merge to `main` pending |
| `96a3dfb` | uat | Waits for P3 entry and the conditions below |

Before the uat commit merges:
- the Worker has run in tdd with the `/app/.diagnostics` mount and no start-up error (ADR-IR31, design §12.1);
- a source for the exit evidence exists (criterion 3). The Worker exports NServiceBus traces (source `NServiceBus.Core`), but no queue-depth metric: the meter `NServiceBus.Core.Pipeline.Incoming` is not added in `ChurchBulletin.ServiceDefaults`. Until the app work item below adds it, the proxy in `log-platform-nonprod` counts failed message handling of the Worker per day [VERIFY the role name and span mapping on the first tdd run]:

  ```kusto
  AppRequests
  | where TimeGenerated > ago(14d) and AppRoleName == "Worker" and Success == false
  | summarize failed = count() by bin(TimeGenerated, 1d)
  ```

  The exact error-queue depth is `SELECT COUNT(*) FROM [nServiceBus].[error]` in `workorders-uat` [VERIFY the error queue name of `ClearHostedEndpoint`], read by a platform operator (break-glass path C), until the metric exists.

#### App work item: NServiceBus metrics meter

Work item: [20260923-001#4](https://github.com/clearmeasure-aisf-sample-apps/20260923-001/issues/4), "Export NServiceBus queue and processing metrics from the Worker". Owner decision of 2026-09-25: the queue-depth evidence of criterion 3 comes from this app change.

- **Gap.** `src/ChurchBulletin.ServiceDefaults/Extensions.cs:64` registers only the meter `ChurchBulletin.Application`. Line 72 of the same file registers the trace source `NServiceBus.Core`, but no NServiceBus meter.
- **Callers.** The Worker (`src/Worker/WorkOrderEndpoint.cs:47`) and the server (`src/UI/Server/ServerApplication.cs:139`) call `EnableOpenTelemetry()`, so both pick up the change.
- **Change.** Add the meter `NServiceBus.Core.Pipeline.Incoming` next to `ChurchBulletin.Application`. No new NuGet packages.
- **Done when.**
  - A test shows the meter is registered.
  - After a tdd deploy, the fetched, succeeded, failed and processing-time metrics appear in `log-platform-nonprod` for the Worker in `workorders-tdd`.
- **Fallback until then.** The KQL proxy and the SQL error-queue count above.


## P4 Prod cutover

**Entry.**
- [ ] P3 exit.
- [ ] WI-01, WI-02, WI-03 and WI-05 merged.
- [ ] `CanNotDelete` on `rg-platform-prod-data` (Owner script `-ApplyLocks`) and on the legacy resource groups.
- [x] Kyverno Enforce in prod: already met. Prod keeps enforcing the workload baseline ahead of P4, as it does the signer and registry-path rules (owner decision of 2026-09-25); the prod deployment of `workorders` 2.5.722 passed it (CAP-AZ-018).
- [ ] Impersonation and egress hardening decided.
- [ ] The prod host name of R35 in place: the Azure DNS label `workorders-prod` on `pip-platform-prod-ingress` (`workorders-prod.southcentralus.cloudapp.azure.com`), with its certificate issued ([R35: prod host name](#r35-prod-host-name)).
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
7. Switch users to the new prod host (R35, `workorders-prod.southcentralus.cloudapp.azure.com`; `platform-gateway` in `platform-ingress`; certificate from `letsencrypt-http01`); verify from outside. The legacy host is an Azure-owned Container Apps name (`*.southcentralus.azurecontainerapps.io`) and cannot move to another resource: users and bookmarks change URL. A redirect from the legacy host would need the legacy app running and a change in the legacy origin by the user (R13); none is planned.
8. End the write freeze. Leave the legacy app stopped but intact.

**Reverse.**
- **Before step 7:** abort; re-enable the legacy migration owner (revert the step-2 change); lift the freezes. The new prod database is discarded (`db-restore` from the pre-window backup, or a fresh disk).
- **After step 7:** freeze prod deployments in Octopus (a temporary freeze); start a write freeze; export the new prod database as a bacpac and import it into the legacy server; revert the step-2 change; start the legacy app and point users back to the legacy host; lift the freeze. This works only while every migration applied since cutover is expand-only, so the legacy build runs on the newer schema ([walkthrough 02](walkthroughs/02-schema-change.md)).

### R35: prod host name

Decided 2026-09-25 by the owner: an Azure-provided DNS label on the prod ingress IP, no domain registration. The label is `workorders-prod` on `pip-platform-prod-ingress`, host `workorders-prod.southcentralus.cloudapp.azure.com` (owner decision of 2026-09-25). The region matches `location = "southcentralus"` in `terraform/tier/prod.tfvars` (checked 2026-09-25); the label's availability was last read the same day and is first come, first served until set: read again on 2026-09-25 while preparing branch `p4/host-label` (`CheckDnsNameAvailability`: available; no public IP of the subscription carries a label), and to be read once more right before the P4 `env-apply` [VERIFY at P4 item 1].

**Facts.**
- **Azure DNS does not avoid registration.** It hosts zones, but public resolution of a zone needs a delegation (NS records) from the registrar of a domain the owner owns.
- **An Azure DNS label needs no domain.** Setting `domainNameLabel` on a public IP publishes `<label>.<region>.cloudapp.azure.com` as an A record to that IP. The name stays while the Public IP resource stays; a Static Standard IP keeps its address too. `pip-platform-<tier>-ingress` is already Static, Standard and `prevent_destroy` (`terraform/tier/network.tf`). Labels are first come, first served per region; on 2026-09-25 `workorders-prod`, `workorders-tdd`, `workorders-uat` and `sandbox-prod` were available in `southcentralus` (read-only `CheckDnsNameAvailability`). No label is set on any public IP of the subscription today.
- **One label per public IP, exact name only.** A label resolves only its own FQDN, with no subdomains and no wildcard [VERIFY: `dig` a subdomain of the label once set]. Each tier has one ingress IP, so each tier gets at most one label host.
- **TLS stays as is.** cert-manager ClusterIssuer `letsencrypt-http01` (Let's Encrypt production, HTTP-01 through the port-80 listener of `platform-gateway`; `gitops/platform/ingress/base/issuers.yaml`) issues `<namespace>-tls` for every ListenerSet host. HTTP-01 needs only a public A record to the gateway, so it applies to a cloudapp.azure.com host [VERIFY: a first issuance, ideally against the Let's Encrypt staging directory].
- **Rate limits.** Let's Encrypt counts certificates per registered domain, taken from the Public Suffix List. Neither `cloudapp.azure.com` nor `sslip.io` is on it (list version 2026-09-24), so cloudapp.azure.com hosts count against `azure.com`, shared with every Azure customer, and sslip.io hosts against `sslip.io`, not per `<ip-dashed>.sslip.io` as design Q48 assumes [VERIFY: whether Let's Encrypt applies an override to either; renewals of the same name are exempt from the new-certificate limit]. The first issuance is the exposure; if it is refused, the fallback is a custom domain (P6).
- **The legacy host does not move under any option.** It is an Azure-owned Container Apps name, so cutover step 7 switches users to a new URL.

**Options.**

| Option | Registration | Host of app #1 prod | Stable while | TLS | Limits |
|---|---|---|---|---|---|
| sslip.io (today) | None | `workorders-prod.20-225-152-33.sslip.io` | The IP stays | HTTP-01 per host | Third-party resolver; IP-bound name users must type |
| Azure DNS label (chosen) | None | `workorders-prod.southcentralus.cloudapp.azure.com` | The Public IP resource stays | HTTP-01 per host, unchanged | One host per tier IP; shared `azure.com` count [VERIFY] |
| Custom domain with Azure DNS child zones | A domain the owner owns | Any, e.g. `workorders.<domain>` | The domain stays registered | Wildcard DNS-01 per tier zone | Cost of the domain; a DNS role for cert-manager; P6 optional (design §9) |

**Scope.** The label goes on `pip-platform-prod-ingress` only, for `workorders-prod`. `sandbox-prod` and every nonprod host keep sslip.io: they need no stable public name. The label equals the namespace, so the host still starts with the namespace and Kyverno `platform-app-hostnames` needs no change.

**Repository changes for P4 (prepared on branch `p4/host-label`, merged at P4, never before).** The branch holds the seven items; nothing is live until it merges and `env-apply` runs in `infra-prod`.
1. `terraform/tier`: variable `ingress_domain_name_label` (default `null`; validated as the main namespace of an app on the tier, `<app>-prod` on prod and `<app>-tdd` or `<app>-uat` on nonprod), set as `domain_name_label` on `azurerm_public_ip.ingress`; `prod.tfvars` sets `workorders-prod`, `nonprod.tfvars` sets nothing; output `ingress_fqdn`. In azurerm 5.x (the lock resolves 5.7.0) `domain_name_label` is not ForceNew and is written by the resource's update function; only `domain_name_label_scope` forces a replacement, and it stays unset. Checked 2026-09-25 with a read-only plan of the real `pip-platform-prod-ingress` imported into a scratch state: `1 to change, 0 to destroy`, `+ domain_name_label = "workorders-prod"`, `ip_address` unchanged. Offline: `tests/tier.tftest.hcl` (the label and `ingress_fqdn` on prod, none on nonprod, a label of another tier or not a namespace refused). `prevent_destroy` stays, so a plan that ever tried a replacement would fail rather than lose the address.
2. `gitops/platform/ingress`: no `service.beta.kubernetes.io/azure-dns-label-name` annotation in the base or an overlay, so Terraform alone owns the label; `IngressManifestTests` (CAP-GIT-012, offline) keeps it out [VERIFY at P4: the label survives a re-sync of the Envoy Service; the Azure cloud provider writes a label only from that annotation].
3. `gitops/platform/tenant`: `platform.hostOverrides` (namespace to host; `values-prod.yaml`: `workorders-prod` → `workorders-prod.southcentralus.cloudapp.azure.com`), read by `templates/listenersets.yaml` through `tenant.host` in place of `<namespace>.<appsDomain>`. `appsDomain` stays the sslip.io suffix for every other host; `values-nonprod.yaml` has no overrides. `tenant.validateHosts` fails the render unless the descriptor and the tier agree: an override only for a main namespace `<app>-<env>`, only with the same `hosts.<env>` in the descriptor, and every host starting with its namespace.
4. App descriptors: optional `hosts` (per environment, main namespace only) in `apps/schema.json`; `apps/workorders.yaml` sets `hosts.prod`. `Platform.Onboarding check` requires a listed environment, the first DNS label `<app>-<env>` and no sslip.io host; `onboarding render` prints the host. Both the platform values (the tier resource) and the descriptor (the app's claim) name the host, and the chart refuses any disagreement.
5. Octopus: `App.BaseUrl` of project `workorders` gets a value scoped to `prod`, `https://workorders-prod.southcentralus.cloudapp.azure.com` (`.octopus/apps/workorders/workorders/variables.ocl`); tdd and uat keep `https://workorders-#{Octopus.Environment.Name}.#{Platform.AppsDomain}`. `var.apps_domains` of `octopus/terraform` is per tier and would move `sandbox-prod` too, so it stays unset.
6. `policies/kyverno/tests/app-hostnames`: `kyverno test` of `platform-app-hostnames` with the label host admitted in `workorders-prod` and refused from `sandbox-prod`, plus the regional zone, a wildcard and a mixed list refused (CAP-GIT-013, offline counterpart). The policy itself is unchanged.
7. Docs: design decision 21, §9 P4 and R35; `docs/bootstrap.md`; this section.

**What goes live when the branch merges (P4, in the maintenance window before cutover step 7).**
- Argo CD syncs `tenant-workorders` on `aks-platform-prod`: ListenerSet `workorders-prod` changes its host to `workorders-prod.southcentralus.cloudapp.azure.com`, and cert-manager requests a new `workorders-prod-tls` through HTTP-01. Until the label exists (next item) the challenge cannot resolve and the certificate stays pending; the sslip.io host of `workorders-prod` stops being served. `sandbox-prod`, `workorders-tdd` and `workorders-uat` do not change.
- `env-plan`, then `env-apply` in `infra-prod` (runbooks of `main`) sets the label: one in-place update of `pip-platform-prod-ingress`, the address unchanged. Nothing applies it on its own; run both right after the merge to keep the gap without a working prod host short.
- Releases of `workorders` created from `main` after the merge (`--git-ref refs/heads/main`) carry the prod-scoped `App.BaseUrl`, so the prod deployment of cutover step 6 runs `verify-version` and `smoke-test` against the new host; tdd and uat keep their URLs. The Config-as-Code change needs no `octopus/apply.ps1` run.
- Verify: `dig +short workorders-prod.southcentralus.cloudapp.azure.com` returns `20.225.152.33`; `kubectl get certificate -n platform-ingress workorders-prod-tls` is Ready; the live CAP-GIT-012 and CAP-GIT-013 tests pass on prod.
- Reverse: revert the merge commit (the ListenerSet returns to sslip.io and `App.BaseUrl` to the tier default) and apply `prod.tfvars` without the label (again in place). A released label may be taken by anyone in the region.

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
