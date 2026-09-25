# Sleep and wake

Both app clusters sleep by default and wake on the first Codefresh or Octopus job that needs them (user directive,
ADR-IR33 as amended by ADR-IR34). Every rule is per cluster, never per app: idle time counts across all apps of a
tier. The build cluster's `builds` pool scales from zero on its own. The Octopus runbooks do the work; this page is the
human side: how the automation decides, how to force a wake or a sleep, how to pause sleeping, the testability hooks,
what never sleeps, what it costs and what can go wrong.

Contracts: ADR-IR33 (sleep by default, wake on first job; keyless wake for app projects), ADR-IR34 (three clusters,
names of §7.0, decisions 16, 17 and 22, the build runner, testability hooks), ADR-IR32 (the Space Manager key), §3.5
(cost), `terraform/tier/monitoring.tf` (suppression rule).

## How it works

![Dynamic: runbook env-sleep](../../design/diagrams/dyn-env-sleep.png)

*Dynamic, runbook env-sleep in `infra-<tier>`, started hourly by `env-sleep-hourly-<tier>` or by hand. Step Decide sleep applies the rules in order: `Sleep.Enabled`, a queued or running task, `Sleep.Force`, the working window, then idle time; it outputs `Sleep.Decision` and `Sleep.Reason`, and a dry run may simulate the clock with `Sleep.NowOverride`. Step Stop cluster runs only on a sleep decision and changes nothing in a dry run; otherwise it enables `apr-sleep-<tier>`, reads the task list again and stops the cluster without waiting; any exit before the stop is accepted disables the rule again.*

![Dynamic: how a sleeping cluster meets its first job](../../design/diagrams/dyn-wake-on-first-job.png)

*Dynamic, three ways a sleeping cluster meets its first job. The release pipeline's step `wake_nonprod` asks Octopus to run env-wake in `infra-nonprod` and never waits. Step 0 of every app deployment deploys `platform-wake`, whose keyed step runs env-wake in `infra-<tier>` and waits; the deployment continues once the cluster is Running and `apr-sleep-<tier>` is disabled. App runbooks hold no key: they wait up to `Wake.WaitMinutes` for the app to answer, then fail with guidance.*

![Level 3: monitoring and cost objects by Terraform layer](../../design/diagrams/c4-3-observability.png)

*Level 3, monitoring and cost objects, grouped by the Terraform layer that creates them. `terraform/tier` creates `log-platform-<tier>`, `ag-platform-oncall` and `apr-sleep-<tier>` once per tier; `terraform/apps/tier` creates `appi-<app>-<env>` and `slo-fast-burn-<app>-<env>` per app environment; `terraform/foundation` creates the three budgets, each filtered by resource-group name. env-sleep enables `apr-sleep-<tier>` before it stops the cluster and env-wake disables it after the start, so a sleeping tier pages nobody.*

| Piece | Where | What it does |
|---|---|---|
| Runbook `env-wake` | Project `platform-infrastructure`; environments `infra-nonprod`, `infra-prod`; pool `hosted-ubuntu`; account `azure-platform-lifecycle-<tier>` | Idempotent; returns within seconds when the cluster runs. Otherwise waits out Stopping, starts the cluster and waits for Running (up to `Wake.TimeoutMinutes`), disables `apr-sleep-<tier>` and writes `Wake.ClusterStarted`. It then reports the health of the workers of pools `k8s-<env>`, requesting one health check, not awaited, for a worker that is not healthy, reads the status of the Argo CD instance for up to 2 minutes (best effort), and writes `Wake.CompletedAt` (gap: it waits for neither healthy workers nor a connected gateway; tracked) |
| Runbook `env-sleep` | Same project, pool and account; triggers `env-sleep-hourly-nonprod` and `env-sleep-hourly-prod` (minute 0 of every hour, cron `0 0 * * * *`, time zone UTC; the working window uses `Sleep.TimeZone`) | Stops nothing while `Sleep.Enabled` is false. Skips while any task of any project in the tier's environments is Executing, Cancelling or Queued to start within 15 minutes. Otherwise sleeps outside the working window, or after `Sleep.IdleMinutes` without a completed task or `env-wake`: enables `apr-sleep-<tier>`, re-reads the task list, then stops the cluster without waiting (`az aks stop --no-wait`). An exit before Azure accepts the stop disables the rule again (gap: a stop that fails after it was accepted leaves the rule enabled; tracked). Writes `Sleep.Decision` and `Sleep.Reason` and logs every decision |
| Project `platform-wake` | Group `Platform`; one step, `run-env-wake`, on `hosted-ubuntu`; library set `Platform Automation` (`PlatformWake.OctopusApiKey`) | Deployed by step 0 of every app process that touches a cluster (Deploy a Release, condition Always). Maps `tdd` and `uat` to `infra-nonprod` and `prod` to `infra-prod`, runs `env-wake` there through the Octopus REST API, waits, and fails when the wake fails. App projects hold no key and no Azure right |
| App runbooks that need a cluster | Starter OCL wait guard (`db-restore`, app #1's `run-acceptance-tests`) | Wait up to `Wake.WaitMinutes` (30) for the environment and name both ways to wake it; they cannot wake the cluster themselves (Deploy a Release is not offered in runbooks, Q32) |
| `platform-infrastructure` runbooks that need a cluster | `env-plan`, `env-apply`, `env-destroy`, `rotate-db-passwords` | Step `wake-environment` runs `env-wake` through the REST API with the step-scoped key and waits; the Terraform runbooks skip it while no cluster exists. `apps-plan` and `apps-apply` need no cluster (they act on Azure only) |
| Codefresh step `wake_nonprod` | Every app release pipeline that uses the optional helper (app #1's `release`), in parallel with the gates; its failure never fails the build | Starts `env-wake` in `infra-nonprod` without waiting (context `platform-octopus`), so the cluster warms up while CI runs (CAP-CF-009) |
| Build pool `builds` | `aks-platform-build` in `rg-platform-build`; taint `codefresh.io/builds`; 0 to 2 nodes | The first Codefresh job's engine and dind pods scale it up from zero [VERIFY, Q51]; the autoscaler returns it to zero after 10 idle minutes (CAP-CF-003). Only the `system` node (`Standard_B2pls_v2`, the runner agent) runs all the time. No runbook touches it |
| Alert processing rule `apr-sleep-<tier>` | `rg-platform-<tier>-aks`; scope: the tier's `-aks` and `-apps` groups | While enabled, suppresses notifications of metric and log alerts (every `slo-fast-burn-<app>-<env>` among them). Activity-log alerts are never suppressed. Created disabled; Terraform ignores `enabled` afterwards (CAP-AZ-004) |

| Cluster | Carries | Sleeps with it |
|---|---|---|
| `aks-platform-nonprod` (tier `nonprod`, environment `infra-nonprod`) | `tdd`, `uat`, previews | Argo CD `argocd-nonprod`, ESO, the Octopus Argo CD gateway, Kyverno, cert-manager, the gateway `platform-gateway`, workers `octopus-worker-tdd` and `octopus-worker-uat`, every app and its database |
| `aks-platform-prod` (tier `prod`, environment `infra-prod`) | `prod` | The same components for `prod` |
| `aks-platform-build` (`rg-platform-build`) | Codefresh builds | Never stopped: the `builds` pool scales to zero instead, because a stopped cluster cannot wake on a Codefresh job (decision 16) |

Variables (project `platform-infrastructure`, scoped to `infra-nonprod` and `infra-prod`; identical in both):

| Variable | Default | Meaning |
|---|---|---|
| `Sleep.Enabled` | `true` | `false` pauses sleeping for the tier. A real production sets `false` for `infra-prod` |
| `Sleep.TimeZone` | `America/Chicago` | Time zone of the working window |
| `Sleep.WorkDays` | `Mon,Tue,Wed,Thu,Fri` | Days of the working window |
| `Sleep.WorkdayStart`, `Sleep.WorkdayEnd` | `07:00`, `19:00` | Outside this window the next hourly run sleeps an idle cluster |
| `Sleep.IdleMinutes` | `120` | Inside the window, sleep after this long without a completed task or wake |
| `Wake.TimeoutMinutes` | `20` | How long `env-wake` waits for the cluster to run |

## Testability hooks

The conformance suite (`conformance.md`) drives sleep and wake through the same runbooks, never around them:

| Hook | Where | Default | Used by |
|---|---|---|---|
| `Sleep.Force` (prompted) | `env-sleep` | `false` | Force-sleep: skips the window and idle checks, never the running-task check or `Sleep.Enabled`. CAP-OCT-008, CAP-OCT-010, CAP-GIT-011, CAP-AZ-004, CAP-AZ-005 |
| `env-wake` on demand | `platform-infrastructure` | — | Force-wake; the same tests |
| `Sleep.DryRun`, `Sleep.NowOverride` (prompted) | `env-sleep` | `false`; empty | Evaluates the decision at a simulated time without stopping anything (the override is honoured only in a dry run). CAP-OCT-009 |
| `Wake.WaitMinutes` (prompted) | App runbook wait guards | `30` | CAP-OCT-011 sets `1` to prove the guard fails with guidance |
| `CONFORMANCE_STOP_GRACE_MINUTES` | Codefresh `platform-env/conformance-arm` | `15` | Wait after both clusters report Stopped, because Microsoft advises 15 to 30 minutes between a stop and a start |
| `CONFORMANCE_SLEEP_AFTER` | Codefresh conformance pipelines | `true` | `false` keeps the clusters up after a run for debugging; sleep them by hand afterwards |

Observable signals: the task logs and output variables (`Wake.CompletedAt`, `Sleep.Decision`), the AKS power state and
the Activity Log, the state of `apr-sleep-<tier>`, the workers' health in Octopus and the Argo CD instance status.

## Roles

| Role | Who | Does |
|---|---|---|
| Platform engineer | Team `Platform Engineers` (Space Manager); the operator | Runs `env-wake` and `env-sleep` by hand, pauses and resumes sleeping, reviews sleep decisions |
| Deployers | `Release Managers`, `UAT Approvers`, `Prod Approvers` | Nothing extra: step 0 of their deployment deploys `platform-wake`, which wakes the cluster. Their Project Deployer role includes DeploymentCreate, which covers `platform-wake` in their environments; no Deployment Creator grant exists |
| Users of app runbooks | `SRE On-call`, platform engineers | Wake the tier first ([Force-wake](#force-wake)): app runbooks only wait for it |
| `SRE On-call` | Team `SRE On-call` | Runbook Consumer on `platform-infrastructure` in `infra-nonprod` and `infra-prod`: force-wake and force-sleep. `env-apply` and `env-destroy` still stop at approvals that `Platform Engineers` answer |
| Azure operator | Group `platform-operators` (AKS RBAC Cluster Admin on the three cluster groups) or the subscription Owner | Starts or stops a cluster in Azure only when Octopus is unavailable (`break-glass.md`) |
| Security owners | CODEOWNERS | Approve every change to `.octopus/platform-infrastructure/variables.ocl` (`Sleep.*`) and `.octopus/platform-wake/**` |

Under the single-operator model (§13) every role above is the user; the table still says which permission each action
needs.

## Check the state

Octopus shows it without Azure rights: the latest `env-wake` or `env-sleep` task log of the tier states the power state
and the decision, and the workers of `k8s-<env>` show as unavailable while the tier sleeps. With Reader on
`rg-platform-<tier>-aks`:

```bash
az aks show --resource-group rg-platform-<tier>-aks --name aks-platform-<tier> \
  --query "{power: powerState.code, provisioning: provisioningState}" --output table
az monitor alert-processing-rule show --resource-group rg-platform-<tier>-aks \
  --name apr-sleep-<tier> --query "properties.enabled"
az aks nodepool show --resource-group rg-platform-build --cluster-name aks-platform-build \
  --name builds --query "{count: count, min: minCount, max: maxCount}" --output table
```

The second command needs the Azure CLI `alertsmanagement` extension [VERIFY the command group]. Awake means `Running`
with the rule `false`; asleep means `Stopped` with the rule `true`. Any other pair is a half-finished wake or sleep:
run `env-wake` again. The build pool shows `count: 0` when no build ran in the last 10 minutes.

## Force-wake

Use before manual testing or a demo in `uat` or `prod`, before break-glass work in a cluster, before reading Argo CD
or Kyverno reports, before a credential rotation that verifies in a cluster, and before app runbooks such as
`db-restore`, which wait for a sleeping cluster but cannot wake it. Deployments and the Terraform runbooks need no
force-wake.

1. In Octopus, project `platform-infrastructure`, run runbook `env-wake` in `infra-nonprod` (for `tdd`, `uat`) or
   `infra-prod` (for `prod`). A deployer without runbook rights deploys the latest `platform-wake` release to the
   environment instead; it runs the same `env-wake`.
2. Wait for success. The log shows the start (5 to 10 minutes from Stopped), the rule disabled, the health of the
   workers (a worker that is not healthy gets a health check, not awaited) and `Wake.CompletedAt`. Allow a few more minutes for the warm-up described in [After a wake](#after-a-wake).
3. Keep it awake as long as needed. Work in Argo CD, `kubectl` or a browser is not an Octopus task, so the next hourly
   `env-sleep` stops the cluster outside the working window, or inside it after `Sleep.IdleMinutes`. For longer work,
   [pause sleeping](#pause-sleeping) first.
4. If `env-wake` fails, run it once more. When AKS rejects the start soon after a stop, wait 30 minutes and run it
   again. When the region reports no capacity, see [Risks](#risks).

Without Octopus (an Octopus Cloud outage during an incident), a `platform-operators` member performs the same two
actions in the same order and records them in the incident (`break-glass.md`):

```bash
az aks start --resource-group rg-platform-<tier>-aks --name aks-platform-<tier>
az monitor alert-processing-rule update --resource-group rg-platform-<tier>-aks --name apr-sleep-<tier> --enabled false
```

## Force-sleep

1. In Octopus, run runbook `env-sleep` in `infra-nonprod` or `infra-prod` and answer the prompt `Sleep.Force` with
   `true`. The run skips the working window and the idle time, but it still stops nothing while any deployment or
   runbook run is queued or executing in the tier's environments, and nothing while `Sleep.Enabled` is `false`. With
   `Sleep.DryRun` set to `true` it only logs the decision.
2. Only when Octopus is unavailable and cost or containment requires a stop, a `platform-operators` member stops the
   cluster with the same two actions in the same order, and records the reason:

   ```bash
   az monitor alert-processing-rule update --resource-group rg-platform-<tier>-aks --name apr-sleep-<tier> --enabled true
   az aks stop --resource-group rg-platform-<tier>-aks --name aks-platform-<tier>
   ```

3. In a security incident, capture evidence before any stop: stopping deletes standalone pods and node state
   (`break-glass.md`).
4. Verify with [Check the state](#check-the-state): `Stopped`, rule `true`.

The build cluster is never force-slept: stopping `aks-platform-build` would leave Codefresh without a runtime. To stop
build cost immediately, wait for the `builds` pool to return to zero, or cancel the running build.

## Pause sleeping

- **Planned pause** (a demo week, a load test, an upgrade day, incident follow-up): a pull request sets `Sleep.Enabled`
  to `false` for `infra-nonprod` or `infra-prod` in `.octopus/platform-infrastructure/variables.ocl`; security owners
  approve it. After the merge, run `env-wake`. Resume by reverting the pull request; the next hourly `env-sleep`
  applies the normal rules again.
- **Emergency pause** (an incident outside working hours): disable the trigger `env-sleep-hourly-<tier>` of project
  `platform-infrastructure` in Octopus and record it in the incident. Re-enable it when the incident ends; the next
  `octopus/terraform` apply also restores it [VERIFY]. Replace an emergency pause with a planned one if it lasts
  longer than the incident.
- **Conformance debugging:** run the pipeline with `CONFORMANCE_SLEEP_AFTER=false`, then force-sleep by hand.
- Never pause by deleting or editing `apr-sleep-<tier>`, by changing the cluster in Azure, or by running a dummy
  Octopus task to hold the cluster awake.

## What never sleeps

| Resource | Why it keeps running | Cost while the app clusters sleep (§3.5, one app) |
|---|---|---|
| `aks-platform-build`: `system` node `Standard_B2pls_v2`, its OS disk, load balancer and IP | The Codefresh Runner agent must answer the first job | About $55 a month |
| Load balancer and IPs of each app cluster (`pip-platform-<tier>-egress`, `pip-platform-<tier>-ingress`) | Stay allocated while the cluster is stopped | About $22 a month per tier |
| Database disks `disk-<app>-<env>-db` | Data survives sleep and rebuilds | About $3.60 per app a month (E2, E2, E4) |
| Registry (Standard), vaults, workspaces, App Insights, state and backup storage | No compute to stop; billed by storage, operations and ingestion | Registry about $20; the rest a few dollars |
| Alert rules, action groups | Activity-log alerts must fire at any time; SLO alerts see no traffic while asleep | Small |
| Octopus Cloud, Codefresh, GitHub | SaaS subscriptions | Unchanged |

`prod` sleeps only because this is a training platform without real users. A real production sets
`Sleep.Enabled = false` for `infra-prod`: Microsoft does not recommend stopping mission-critical clusters, because a
start can fail when the region lacks capacity.

## Cost effect

Estimates from §3.5 at list prices [UNVERIFIED]:

| Apps | Sleeping | Always on |
|---|---|---|
| 1 | About $220 a month | About $1,010 a month |
| 12 | About $810 a month | About $2,970 a month |
| 36 (E-series apps pools after R34) | About $1,630 a month; about $900 with two thirds frozen | About $4,210 a month |

- Each awake hour of a tier at its minimum size (system and one apps node) costs about $0.54; every extra apps node
  adds $0.271 an hour.
- The assumed awake hours include the working-day use and about 40 nonprod and 11 prod conformance hours a month.
- Budgets `budget-platform-build`, `budget-platform-nonprod` and `budget-platform-prod` filter by resource-group name,
  node groups included (CAP-AZ-014); a breach usually means a cluster did not sleep. The live subscription is a
  sponsorship offer, which Cost Management does not support, so no budget exists there and CAP-AZ-014 reports the gap.
- If the idle clock counts the hourly `env-sleep` runs as activity (S6, Q34), nonprod stays awake through every working
  window and its node cost roughly doubles.

## After a wake

A start restores every object from etcd, then pods come back in no guaranteed order: system pods, Cilium and CoreDNS
first, then Kyverno, ESO, Argo CD, cert-manager, the gateway, the workers, the databases and the apps. The API
server's IP address can change (the FQDN does not), and node counts can sit outside the autoscaler's range for a while.

**Kyverno: admission warm-up.**

- The webhook configurations survive the stop, so the API server calls Kyverno before its pods are ready.
- `prod` fails closed. Until the admission controller is ready, it denies every create or update of a controller
  (Deployment, StatefulSet, DaemonSet, Job, CronJob, Rollout) or bare Pod in app namespaces. Argo CD retries.
- `nonprod` fails open (`Ignore`): writes during the warm-up are admitted unchecked and appear in no report.
- Pods created by controllers are never matched, so ReplicaSets and StatefulSets recreate the apps and databases with
  the images that ran before the stop.
- The Kyverno values exclude `kube-system`, `kube-node-lease` and every cluster-scoped kind from its webhooks, and the
  policies match namespaced kinds only, so a stop is not rejected (ADR-IR34 decision 22; CAP-AZ-005 proves it
  weekly). Fallback, only if a stop is still rejected: `env-sleep` removes Kyverno's webhook configurations just
  before the stop, and Kyverno registers them again at start.
- Before any manual deployment right after a wake, check that `kubectl -n kyverno get pods` shows the admission
  controller Ready.

**ESO: re-sync.** Target Secrets survive in etcd, so pods start with the values synced before the stop.
ExternalSecrets whose refresh interval passed during the sleep refresh when the controller starts [VERIFY]. A vault
change made while the tier slept reaches the Secret shortly after the wake; running pods keep their old values until
a restart through Git. Workload identity keeps working: the OIDC issuer does not change on a stop or start [VERIFY];
it changes only on a rebuild, after which `apps-apply` runs again.

**Databases.** The StatefulSets restart on their retained disks; data written before the stop is there
(CAP-GIT-011). After a start, check that disks attached (`kubectl get pods -n <app>-<env>`, the `db-0` pod Ready);
a disk that fails to attach after a start is a known intermittent AKS issue [VERIFY]: delete the pod once.

**Argo CD.** Argo CD re-reads Git and applies every commit merged while the tier slept, so a pull request merged at
night takes effect at the next wake. Self-heal resumes, and Octopus sees live status again once the gateway reconnects.

**Octopus workers.** The workers reconnect by polling. Their machine policy schedules no health checks, so a worker
keeps the status of its last check through a sleep. `env-wake` requests a check, without waiting, only for a worker
that is not healthy; a step on that pool that starts before the check ends can fail with no healthy worker: rerun it
(gap: `env-wake` does not wait for the workers; tracked).

**Alerts.** `env-wake` disables `apr-sleep-<tier>` right after the start. The fast-burn alerts need at least 50
requests an hour, so a quiet environment cannot page during the warm-up.

## Risks

| Risk | Effect | Mitigation |
|---|---|---|
| The region lacks capacity at start | `env-wake` fails, so the deployment (through `platform-wake`) or the runbook fails | Rerun later. A real production does not sleep |
| An app runbook starts while the tier sleeps | Its guard waits up to `Wake.WaitMinutes`, then fails | Force-wake first; the guard's message names both ways to wake |
| No `platform-wake` release exists | Every app deployment fails at step 0 [VERIFY] | Create a `platform-wake` release after every change to `.octopus/platform-wake` |
| Start requested within 15 to 30 minutes of a stop | The start fails or disturbs the stop | The 120-minute idle threshold makes this rare; the conformance arm waits 15 minutes; rerun `env-wake` after 30 minutes |
| First-job latency: 5 to 10 minutes of AKS start, plus warm-up; plus a build node from zero | The first build and deployment of the day are slower | `wake_nonprod` warms nonprod in parallel with CI; `Wake.TimeoutMinutes` (20) absorbs the rest |
| AKS rejects a stop because of an admission webhook | `env-sleep` fails and the cluster keeps running | Webhook exclusions (decision 22); the fallback above; never delete a webhook by hand to force a stop |
| Managed OS disks bill while stopped (Q39 moot) | Cost only | 64 GiB per node; ephemeral disks are not available on the allowed v6 sizes |
| Pod disruption budgets slow the drain | The stop takes longer | One disruption at a time for the add-ons [VERIFY the stop duration] |
| A failed `env-wake` leaves the suppression rule enabled | Real alerts do not notify while the cluster runs | [Check the state](#check-the-state) after every manual wake; `env-wake` is idempotent |
| A stop fails after Azure accepted it (`env-sleep` does not wait) | `apr-sleep-<tier>` stays enabled on a running cluster, and nothing checks it (gap, tracked) | [Check the state](#check-the-state) after a failed stop; `env-wake` disables the rule |
| A night-time incident with no Octopus task running | `env-sleep` stops the cluster under the responder | Pause sleeping first (`break-glass.md`) |
| A cluster stopped for more than 12 months | Its state cannot be recovered | Rebuild with `env-apply`; the disks survive. Nonprod wakes every working week |
| Upgrades are manual and run only while awake | A long sleep falls behind supported versions | Monthly upgrade day with sleeping paused, while the `builds` pool is at zero (one surge node fits the quota) |
| Changes merged while asleep apply late | A reviewer expects a change that is not live yet | Force-wake to apply it now |
| One API key drives every keyed wake (ADR-IR32) | A leaked key can wake or stop clusters and run `env-destroy` in `infra-nonprod` | Only platform-owned steps receive it: the one step of `platform-wake` and the REST-calling steps of `platform-infrastructure`; app projects never do. It rotates with its other consumers (`credential-rotation.md` §6) |
| The app is unreachable while its tier sleeps | Testers, demos and uptime checks see errors | Force-wake before use; scope uptime checks to awake periods |

## Verification

- Awake: power state `Running`, `apr-sleep-<tier>` disabled, the workers of `k8s-<env>` Healthy, the Argo CD instance
  healthy in Octopus, the Kyverno admission controller Ready, every ExternalSecret `SecretSynced`
  (`kubectl get externalsecrets -A`).
- Asleep: power state `Stopped`, `apr-sleep-<tier>` enabled, and the last `env-sleep` log states the decision.
- Build: the `builds` pool at zero nodes within 20 minutes of the last build (CAP-CF-003).
- Nightly: the conformance suite proves wake on deployment (CAP-OCT-008), the schedule (CAP-OCT-009), force-sleep and
  force-wake (CAP-OCT-010), the alert rule (CAP-AZ-004) and the stop with Kyverno (CAP-AZ-005).

## Audit evidence

- The Octopus task history of `env-wake` and `env-sleep`: every decision with its reason, and the deployment or
  runbook that requested each wake.
- The Azure Activity Log of the cluster and the rule, with the caller (`id-platform-lifecycle-<tier>`):

  ```bash
  az monitor activity-log list --resource-group rg-platform-<tier>-aks --offset 7d \
    --query "[?contains(operationName.value, 'managedClusters/start') || contains(operationName.value, 'managedClusters/stop') || contains(operationName.value, 'actionRules')].{time: eventTimestamp, operation: operationName.value, caller: caller, status: status.value}" \
    --output table
  ```

- The pull requests that changed `Sleep.*`, and the Octopus audit log for trigger changes.
- The monthly cost per resource group (Cost Management), against the three budgets.
