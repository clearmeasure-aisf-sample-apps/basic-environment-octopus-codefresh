# Sleep and wake

Both AKS clusters sleep by default and wake on the first Codefresh or Octopus job that needs them
(user directive, ADR-IR33). The Octopus runbooks do the work; this page is the human side: how
the automation decides, how to force a wake or a sleep, how to pause sleeping, what never sleeps,
what it costs and what can go wrong.

Contracts: ADR-IR33 (sleep by default, wake on first job; app projects wake without a key),
ADR-IR32 (the Space Manager API key), ADR-D1 (Standard SKU without node auto-provisioning, so the
clusters can stop), ADR-D15 (alerts), §7.2 (runbooks, variables, project `platform-wake`),
`terraform/environment/monitoring.tf` (suppression rule).

## How it works

| Piece | Where | What it does |
|---|---|---|
| Runbook `env-wake` | Project `workorders-infrastructure`; environments `infra-nonprod`, `infra-prod`; pool `hosted-ubuntu`; account `Azure.LifecycleAccount` | Idempotent; returns within seconds when the cluster already runs. Otherwise starts it and waits for Running (up to `Wake.TimeoutMinutes`), disables `apr-sleep-<cluster>`, waits until the workers of pools `k8s-<env>` report Healthy and, where observable, the Argo CD gateway is connected, then writes `Wake.CompletedAt` |
| Runbook `env-sleep` | Same project, pool and account; scheduled triggers `env-sleep-hourly-nonprod` and `env-sleep-hourly-prod` (every hour, America/Chicago) | Stops nothing while `Sleep.Enabled` is false. Skips while any deployment or runbook run of any project in the cluster's environments is queued or executing. Otherwise sleeps outside the working window, or after `Sleep.IdleMinutes` without a completed task or `env-wake`: enables `apr-sleep-<cluster>`, reads the task list again, then stops the cluster. Prompted `Sleep.Force` (by hand) skips the window and idle checks, never the task check. Logs every decision |
| Project `platform-wake` | Platform-owned; one step, `run-env-wake`, on `hosted-ubuntu`; library set `WorkOrders Platform Automation` | Deployed by the first step of every `workorders` deployment. Maps `tdd`, `uat` to `infra-nonprod` and `prod` to `infra-prod`, runs `env-wake` there through the Octopus REST API with `Platform.OctopusApiKey`, waits, and fails when the wake fails |
| Step `wake-environment` | First step of every process or runbook that needs a cluster | Three forms (ADR-IR33). **`workorders` process:** a Deploy a Release of `platform-wake` (condition Always): project `workorders` holds no key and no Azure right. **`db-backup`, `db-restore-pitr`, `run-acceptance-tests`:** a keyless guard that waits up to 30 minutes until the environment answers and says how to wake it; it cannot wake the cluster itself. **`env-plan`, `env-apply`, `env-destroy`, `rotate-sql-passwords`:** runs `env-wake` through the Octopus REST API with the step-scoped key and waits; the three Terraform runbooks skip it while no cluster exists |
| Codefresh step `wake_nonprod` | `workorders/release`, in parallel with the gates; its failure never fails the build | Starts `env-wake` in `infra-nonprod` without waiting, so the cluster warms up while CI runs |
| Alert processing rule `apr-sleep-<cluster>` | `rg-workorders-aks-<cluster>`; scope: the class's environment resource groups | While enabled, suppresses notifications of metric and log alerts (the SLO alerts among them). Activity-log alerts, including the foundation's security alerts, are never suppressed. Created disabled; Terraform ignores `enabled` afterwards |

| Cluster | Carries | Sleeps with it |
|---|---|---|
| `aks-workorders-nonprod` (class `nonprod`, environment `infra-nonprod`) | `tdd`, `uat` | Argo CD `argocd-nonprod`, ESO, the Octopus Argo CD gateway, Kyverno, workers `octopus-worker-tdd` and `octopus-worker-uat`, the app |
| `aks-workorders-prod` (class `prod`, environment `infra-prod`) | `prod` | The same components for `prod` |

Variables (project `workorders-infrastructure`, scoped to `infra-nonprod` and `infra-prod`):

| Variable | Default | Meaning |
|---|---|---|
| `Sleep.Enabled` | `true` for both | `false` pauses sleeping for the class. A real production sets `false` for `infra-prod` (ADR-IR33) |
| `Sleep.TimeZone` | `America/Chicago` | Time zone of the working window |
| `Sleep.WorkDays` | `Mon,Tue,Wed,Thu,Fri` | Days of the working window |
| `Sleep.WorkdayStart`, `Sleep.WorkdayEnd` | `07:00`, `19:00` | Outside this window the next hourly run sleeps an idle cluster |
| `Sleep.IdleMinutes` | `120` | Inside the window, sleep after this long without a completed task or wake |
| `Wake.TimeoutMinutes` | `20` | How long `env-wake` waits for the cluster to run |

## Roles

| Role | Who | Does |
|---|---|---|
| Platform engineer | Team `Platform Engineers` (Space Manager) | Runs `env-wake` and `env-sleep` by hand, pauses and resumes sleeping, reviews sleep decisions |
| Deployers | `Release Managers`, `UAT Approvers`, `Prod Approvers` | Nothing extra: the first step of their deployment deploys `platform-wake`, which wakes the cluster. They hold Deployment Creator on `platform-wake` in their environments for this (`octopus/terraform/teams.tf`) |
| Users of the `workorders` runbooks | `SRE On-call` (`db-backup`, `db-restore-pitr`), platform engineers (`run-acceptance-tests`) | Wake the cluster first ([Force-wake](#force-wake)): these runbooks only wait for it, because project `workorders` holds no key |
| `SRE On-call` | Team `SRE On-call` | Runbook Consumer on `workorders-infrastructure` in `infra-nonprod` and `infra-prod`: force-wake with `env-wake`, force-sleep with `env-sleep` and `Sleep.Force`. The role also starts the other runbooks of that project; `env-apply` and `env-destroy` still stop at approvals that only `Platform Engineers` answer |
| Azure Owner | PIM Owner or User Access Administrator | Starts or stops a cluster in Azure only when Octopus is unavailable (`break-glass.md`) |
| Security owners | CODEOWNERS | Approve every change to `.octopus/workorders-infrastructure/variables.ocl` (`Sleep.*`, both classes) and to `.octopus/platform-wake/` (S4) |

## Check the state

Octopus shows it without Azure rights: the latest `env-wake` or `env-sleep` task log of the class
states the power state and the decision, and the workers of `k8s-<env>` show as unavailable while
the cluster sleeps. With Reader on `rg-workorders-aks-<cluster>`:

```bash
az aks show --resource-group rg-workorders-aks-<cluster> --name aks-workorders-<cluster> \
  --query "{power: powerState.code, provisioning: provisioningState}" --output table
az monitor alert-processing-rule show --resource-group rg-workorders-aks-<cluster> \
  --name apr-sleep-<cluster> --query "properties.enabled"
```

The second command needs the Azure CLI `alertsmanagement` extension; the command group and the
property path are [VERIFY]. Awake means `Running` with the rule `false`; asleep means `Stopped`
with the rule `true`. Any other pair is a half-finished wake or sleep: run `env-wake` again.

## Force-wake

Use before manual testing or a demo in `uat` or `prod`, before break-glass work in a cluster,
before reading Argo CD or Kyverno reports, before a credential rotation that verifies in a
cluster, and before the `workorders` runbooks `db-backup`, `db-restore-pitr` and
`run-acceptance-tests`, which wait for a sleeping cluster but cannot wake it. Deployments and the
`workorders-infrastructure` runbooks need no force-wake.

1. In Octopus, project `workorders-infrastructure`, run runbook `env-wake` in `infra-nonprod`
   (for `tdd`, `uat`) or `infra-prod` (for `prod`): `SRE On-call` or a platform engineer. A
   deployer without runbook rights deploys the latest `platform-wake` release to the environment
   instead; it runs the same `env-wake`.
2. Wait for success. The log shows the start (5 to 10 minutes from Stopped), the rule disabled,
   the workers Healthy and `Wake.CompletedAt`. Allow a few more minutes for the warm-up described
   in [After a wake](#after-a-wake).
3. Keep it awake as long as needed. Work in Argo CD, `kubectl` or a browser is not an Octopus task,
   so the next hourly `env-sleep` stops the cluster outside the working window, or inside it after
   `Sleep.IdleMinutes`. For longer work, [pause sleeping](#pause-sleeping) first.
4. If `env-wake` fails: run it once more. When AKS rejects the start soon after a stop, wait 30
   minutes (Microsoft asks for 15 to 30 minutes between a stop and a start) and run it again. When
   the region reports no capacity, see [Risks](#risks).

Without Octopus (an Octopus Cloud outage during an incident), the Azure Owner performs the same two
actions in the same order and records them in the incident (`break-glass.md`):

```bash
az aks start --resource-group rg-workorders-aks-<cluster> --name aks-workorders-<cluster>
az monitor alert-processing-rule update --resource-group rg-workorders-aks-<cluster> --name apr-sleep-<cluster> --enabled false
```

## Force-sleep

1. In Octopus, run runbook `env-sleep` in `infra-nonprod` or `infra-prod` (`SRE On-call` or a
   platform engineer) and answer the prompt `Sleep.Force` with `true`. The run skips the working
   window and the idle time, but it still stops nothing while any deployment or runbook run is
   queued or executing in the cluster's environments, and nothing while `Sleep.Enabled` is
   `false`. With `Sleep.Force` left `false`, the run applies the normal hourly decision. The log
   states the decision and, where Octopus provides it, who forced it (S7: whether runbook runs
   populate `Octopus.Deployment.CreatedBy.*` is [VERIFY]; the task history names the user in any
   case).
2. Only when Octopus is unavailable and cost or containment requires a stop, the Azure Owner
   stops the cluster with the same two actions in the same order, and records the reason:

   ```bash
   az monitor alert-processing-rule update --resource-group rg-workorders-aks-<cluster> --name apr-sleep-<cluster> --enabled true
   az aks stop --resource-group rg-workorders-aks-<cluster> --name aks-workorders-<cluster>
   ```

3. In a security incident, capture evidence before any stop: stopping deletes standalone pods and
   node state (`break-glass.md`).
4. Verify with [Check the state](#check-the-state): `Stopped`, rule `true`.

## Pause sleeping

- **Planned pause** (a demo week, a load test, an upgrade day, incident follow-up): a pull request
  sets `Sleep.Enabled` to `false` for `infra-nonprod` or `infra-prod` in
  `.octopus/workorders-infrastructure/variables.ocl`; security owners approve it (`CODEOWNERS`). After
  the merge, run `env-wake`. Resume by reverting the pull request; the next hourly `env-sleep` applies
  the normal rules again.
- **Emergency pause** (an incident outside working hours): a platform engineer disables the trigger
  `env-sleep-hourly-<cluster>` of project `workorders-infrastructure` in Octopus and records it in
  the incident. Re-enable it when the incident ends; the next `octopus/terraform` apply also
  restores it [VERIFY]. Replace the emergency pause with a planned one if it lasts longer than the
  incident.
- Never pause by deleting or editing `apr-sleep-<cluster>`, by changing the cluster in Azure, or
  by running a dummy Octopus task to hold the cluster awake.

## What never sleeps

| Resource | Why it keeps running | Cost while the cluster sleeps |
|---|---|---|
| Azure SQL `tdd` (Basic), `uat` (S0), `prod` (S1) | DTU tiers cannot pause. Serverless auto-pause would cost more at this usage | About $5 to $29 a month each |
| ACR, Key Vaults, Log Analytics, App Insights, Terraform state storage | No compute to stop; billed by storage, operations and ingestion | ACR about $20 a month; the rest small |
| Load balancer and public IP of each cluster | Stay allocated while the cluster is stopped | About $22 a month per cluster |
| Private endpoints for SQL and the vaults (5 for nonprod, 3 for prod) | Billed per hour, running or not (https://azure.microsoft.com/en-us/pricing/details/private-link/) | About $7 a month each [VERIFY price] |
| Alert rules, action groups, the foundation's security alerts | Security alerts must fire at any time; the SLO alert sees no traffic while asleep | Small |
| Codefresh runner cluster | In another subscription, out of reach of these runbooks | Recommendation only: let its build node pools autoscale to zero |
| Octopus Cloud, Codefresh, GitHub | SaaS subscriptions | Unchanged |

`prod` sleeps only because this is a sample platform without real users. A real production sets
`Sleep.Enabled = false` for `infra-prod`: Microsoft does not recommend stopping mission-critical
clusters, because a start can fail when the region lacks capacity.

## Cost effect

Estimates from the design's cost section (§3.4, ADR-IR33), minimum node counts, list prices
[UNVERIFIED amounts; R18]:

| Scope | Always on | With sleep |
|---|---|---|
| Foundation (ACR about $20; DNS zones, state storage, vault operations) | About $25 a month | About $25 a month |
| Private endpoints (8: 5 for nonprod, 3 for prod) | About $58 a month | About $58 a month |
| `nonprod` (2 nodes) | About $515 a month | About $100 a month: about 66 awake hours a month x 2 nodes x $0.271 an hour (about $36) plus the fixed costs above |
| `prod` (4 nodes, Standard tier) | About $1,040 a month | About $140 a month, SQL S1 included; about $210 to $280 if the tier fee or the OS disks bill while stopped |
| **Platform** | **About $1,640 a month** | **About $325 a month** |

- A stopped cluster bills neither the control plane nor the nodes. How OS disks are billed while
  stopped is [VERIFY]; whether the Standard tier's uptime-SLA fee (prod) continues is [VERIFY].
- The awake-hours assumption: about 3 hours per working day, because a cluster wakes on the first
  job, sleeps after 2 idle hours or at 19:00, and stays asleep over weekends. Pauses and demos
  add awake hours at the node rate.
- The private endpoints are listed once, as a fixed line; the class rows exclude them.
- If the idle clock counts the hourly `env-sleep` runs as activity (S6: `env-sleep` skips them by
  task description, [VERIFY] in the P2 drill), nonprod stays awake through every working window:
  about $215 a month instead of about $100.

## After a wake

A start restores every object from etcd, then pods come back in no guaranteed order: system pods,
Cilium and CoreDNS first, then Kyverno, ESO, Argo CD, the gateway, the workers and the app. The
API server's IP address can change (the FQDN does not), and the node count can sit outside the
autoscaler's range for a while.

**Kyverno: admission warm-up.**

- The webhook configurations survive the stop, so the API server calls Kyverno before its pods
  are ready.
- `prod` fails closed (`failurePolicy: Fail`). Until the admission controller is ready, it denies
  every create or update of a Deployment, Job or Rollout in `workorders-*`. Argo CD retries (retry
  limit 5, backoff up to 5 minutes).
- `nonprod` fails open (`Ignore`). Writes during the warm-up are admitted unchecked and appear in
  no report: a gap of a few minutes.
- Pods are not blocked. ReplicaSets recreate them, and the policies match Deployments, Jobs and
  Rollouts, not Pods, so the app returns with the images that ran before the stop.
- The first signature check after a start is slower: Sigstore trust material, Rekor, and an ACR
  token through workload identity. A check that exceeds the 15-second webhook timeout counts as a
  failure and is retried [VERIFY the typical duration in the phase-2 spike].
- The image policies evaluate at admission only (ADR-IR30), so a wake triggers no background
  signature scan. The baseline policies re-scan in the background and refresh the PolicyReports.
- Before any manual deployment right after a wake, check that `kubectl -n kyverno get pods` shows
  the admission controller Ready.

**ESO: re-sync.**

- Target Secrets survive in etcd, so pods start with the values synced before the stop.
- ExternalSecrets whose 1-hour refresh interval passed during the sleep refresh when the
  controller starts [VERIFY]. To refresh at once, use the `force-sync` annotation in
  `credential-rotation.md` §5.
- A Key Vault change made while the cluster slept reaches the Secret shortly after the wake.
  Running pods keep their old environment values until a restart through Git
  (`credential-rotation.md` §7).
- Workload identity keeps working: the cluster's OIDC issuer does not change on a stop or start
  [VERIFY], so the federated credentials still match.

**Argo CD.** Argo CD re-reads Git and applies every commit merged while the cluster slept, so a
configuration pull request merged at night takes effect at the next wake. Self-heal resumes, and
Octopus sees live status again once the gateway reconnects.

**Octopus workers.** The tentacles reconnect by polling; `env-wake` waits until they are Healthy.

**Alerts.** `env-wake` disables `apr-sleep-<cluster>` right after the start. The fast-burn alert
needs at least 50 requests an hour, so a quiet environment cannot page during the warm-up.

## Risks

| Risk | Effect | Mitigation |
|---|---|---|
| The region lacks capacity at start (Microsoft) | `env-wake` fails, so the deployment (through `platform-wake`) or the runbook fails | Rerun later. A real production does not sleep |
| A `workorders` runbook starts while the cluster sleeps | Its first step waits up to 30 minutes, then fails | Force-wake first; the step's warning names both ways to wake |
| No `platform-wake` release exists, or its URL and space placeholders are still in the script | Every `workorders` release or deployment fails at `wake-environment` [VERIFY] | Bootstrap step 3 creates the first release after replacing the placeholders; create a new one after every change to `.octopus/platform-wake` |
| Start requested within 15 to 30 minutes of a stop (Microsoft) | The start fails or disturbs the stop | The 120-minute idle threshold makes this rare; rerun `env-wake` after 30 minutes |
| First-job latency: 5 to 10 minutes of AKS start, plus warm-up | The first deployment of the day is slower | `wake_nonprod` warms `nonprod` in parallel with CI; `Wake.TimeoutMinutes` (20) and the Argo CD verification (900 s) absorb the rest |
| AKS rejects a stop because of an admission webhook with wildcard rules (Microsoft) | `env-sleep` fails and the cluster keeps running | Kyverno's TTL-label webhook has wildcard rules with `failurePolicy: Ignore` (Kyverno 1.19.1 source), one of Microsoft's listed remedies; prove in the phase-2 spike. Never delete a webhook to force a stop |
| Pod disruption budgets slow the drain (Microsoft) | The stop takes longer | Prod Argo CD and Kyverno budgets allow one disruption at a time [VERIFY the stop duration] |
| A failed `env-wake` leaves the suppression rule enabled | Real alerts do not notify while the cluster runs | [Check the state](#check-the-state) after every manual wake; `env-wake` is idempotent, so run it again |
| A night-time incident with no Octopus task running | `env-sleep` stops the cluster under the responder | Pause sleeping first (`break-glass.md`) |
| A cluster stopped for more than 12 months (Microsoft) | Its state cannot be recovered | Rebuild with `env-apply`. Nonprod wakes every working week |
| Upgrades run only while the cluster is awake | A patch or node-image upgrade may start right after a wake; a long sleep falls behind supported versions [VERIFY] | Plan a monthly upgrade day with sleeping paused |
| Changes merged while asleep apply late | A reviewer expects a change that is not live yet | Force-wake to apply it now |
| One API key drives every keyed wake (ADR-IR32) | A leaked key can wake or stop clusters and run `env-destroy` in `infra-nonprod` | Only platform-owned steps receive `Platform.OctopusApiKey`: the one step of `platform-wake` and four steps of `workorders-infrastructure` (S5); project `workorders` never does. It rotates with the key's other consumers (`credential-rotation.md` §6) |
| The app is unreachable while its cluster sleeps | Testers, demos and uptime checks see errors | Force-wake before use; scope any uptime check to awake periods |

## Verification

- Awake: the power state is `Running`, `apr-sleep-<cluster>` is disabled, the workers of
  `k8s-<env>` are Healthy, the Argo CD instance reports healthy in Octopus, the Kyverno admission
  controller is Ready and every ExternalSecret shows `SecretSynced`
  (`kubectl get externalsecrets -A`).
- Asleep: the power state is `Stopped`, `apr-sleep-<cluster>` is enabled, and the last `env-sleep`
  log states the decision.
- Weekly: the `env-sleep` decisions match the working window, and the cost of the cluster
  resource groups follows the estimate (Cost Management, grouped by resource group).

## Audit evidence

- The Octopus task history of `env-wake` and `env-sleep`: every decision with its reason, and the
  deployment or runbook that requested each wake.
- The Azure activity log of the cluster and the rule, with the caller (`id-env-lifecycle-<class>`,
  or the stored provisioner in `infra-nonprod` during phases 1 and 2):

  ```bash
  az monitor activity-log list --resource-group rg-workorders-aks-<cluster> --offset 7d \
    --query "[?contains(operationName.value, 'managedClusters/start') || contains(operationName.value, 'managedClusters/stop') || contains(operationName.value, 'actionRules')].{time: eventTimestamp, operation: operationName.value, caller: caller, status: status.value}" \
    --output table
  ```

- The pull requests that changed `Sleep.*`, and the Octopus audit log for trigger changes.
- The monthly cost report per resource group.
