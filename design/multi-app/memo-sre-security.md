# Multi-app redesign: Azure layers, data, secrets and cost (sre-security memo)

Scope: `terraform/**`, `policies/**`, `docs/runbooks/**`. Nothing exists in Azure yet, so every change is a rewrite.

## 1. Three Azure layers

| Layer (state) | Applied by | Holds |
|---|---|---|
| `terraform/foundation` (`foundation.tfstate`) | Provisioner, once | `rg-platform-shared`, `rg-platform-apps`, `rg-platform-aks-training`; `vnet-platform`; ACR; `log-platform`; state storage; backup storage account; platform UAMIs and grants; Octopus-issuer federated credentials; the cluster's static outbound IP |
| `terraform/owner` (`owner.tfstate`) | Owner or User Access Administrator | Locks, policy assignments, PIM eligibility (the provisioner cannot create them, E52) |
| `terraform/cluster` (`cluster-training.tfstate`) | `platform-infrastructure` runbooks as `id-platform-lifecycle` | `aks-platform-training`, Argo CD bootstrap, Octopus workers, platform vault, platform workload federation, `apr-sleep-training` |
| `terraform/apps` (`apps.tfstate`) | Same | `for_each` over `apps/*.yaml`: per app-environment UAMIs and federated credentials, optional Key Vault with generated secrets, ACR scope maps and tokens, backup containers, App Insights, alerts, and their grants |

Every per-app resource lives in `rg-platform-apps`. Onboarding therefore needs no new resource group, no subscription-scope right and no platform-wide change.

**Leaves:** Azure SQL (servers, databases, private endpoints, `privatelink.database.windows.net`), `<sql-admins-{class}>`, `SQL DB Contributor`, and the SQL Entra-only policy.

**Owner script update required** (`Grant-ProvisionerRights.ps1`):
- Drop `SQL DB Contributor`.
- Grant `id-platform-lifecycle` a constrained `Role Based Access Control Administrator` on `rg-platform-apps`. Assignable roles: `Key Vault Secrets User`, `Key Vault Secrets Officer`, `Storage Blob Data Contributor`, `Reader`. Assignees: service principals and groups.

Onboarding assigns roles; without this grant only the stored secret could do it (ADR-C10).

## 2. Databases in pods

- **Engine.** SQL Server 2022 Linux StatefulSet with one PVC: E2 in tdd and uat, E4 in prod.
  - StorageClass `platform-retain` (reclaim `Retain`), so pruning a namespace never deletes data.
  - `MSSQL_PID=Express` everywhere: 10 GB per database, 1,410 MB buffer pool, the lesser of one socket or four cores (Microsoft, 2026-08-12). Kyverno `require-mssql-express` rejects other editions.
- **Logins.** SQL logins only, because containers lack Entra authentication.
  - `sa` serves only the `db-init` Sync-hook Job, which creates `<app>_migrator` (DDL, read, write) and `<app>_app` (read, write).
  - `terraform/apps` generates the three passwords (ephemeral, write-only `value_wo`, never in state) into the app vault. ESO syncs them; Octopus reads only the migrator's.
  - Trade-off: this reverses ADR-D9's passwordless app. Pods now hold a rotatable password (`rotate-db-passwords`); the in-cluster certificate is self-signed [VERIFY WI-07].
- **NetworkPolicy.** Default-deny ingress and egress per namespace.
  - Port 1433 accepts only the app's pods, `db-init`, `db-backup` and `octopus-worker-<env>` (migrations).
  - Egress allows DNS, the namespace itself and `0.0.0.0/0` minus the pod and service CIDRs, so no app reaches another app's namespace.
- **Backups.** CronJob `db-backup` in uat and prod: `BACKUP DATABASE … TO DISK` into an `emptyDir`, then `azcopy` as `id-<app>-<env>-backup`. That identity holds `Storage Blob Data Contributor` on container `<app>-<env>` only; the account has no shared keys, soft delete, and 30-day expiry.
  - A sleeping database cannot change, and `startingDeadlineSeconds: 86400` runs a missed backup at the next wake.
  - RPO is 24 hours with no point-in-time restore, a regression from Azure SQL; `db-restore` runs with the app at zero replicas (ADR-IR28).
- **Isolation limits.** Namespaces share the kernel, nodes, control plane, ingress and add-ons. A container escape reaches every app, prod namespaces included.
  - Controls: Pod Security `restricted`, Kyverno, NetworkPolicy, per-app AppProjects with no cluster-scoped kinds, per-app identities, and no Kubernetes RBAC for app teams. Adequate for teaching apps.
  - A separate cluster is warranted for real users or an SLA, regulated data, mutually distrustful tenants, privileged workloads or operators, or prod that must not sleep.
- **Kyverno.** Namespaces carry `tier: app`, `environment: <env>` and `platform/app: <app>`; policies select by label, never by name prefix.
  - Each policy renders twice: Enforce for `environment: prod`, Audit elsewhere.
  - A generic `ValidatingPolicy` admits only `<acr-name>.azurecr.io/apps/<app>/` images plus digest-pinned platform bases.
  - The kit writes `policies/kyverno/apps/<app>.yaml`: an `ImageValidatingPolicy` whose attestor is `https://g.codefresh.io/<cf-account-name>/<app>/release:<CF_ACCOUNT_ID>/<release-pipeline-id>`. Argo CD reads that folder recursively.
- **Quotas.** Each `<app>-<env>` has a ResourceQuota (4 GiB memory, 2 CPU, 3 PVCs, 50 GiB storage, no LoadBalancer or NodePort services) and a LimitRange. Add-ons run at a higher PriorityClass.

## 3. Secrets: one vault per app-environment (recommended)

- Vault `kv-<app>-<t|u|p>-<hash4>` for each app-environment with a database or external secret, with RBAC at vault scope.
- Microsoft recommends "a vault per application per environment" and says assigning roles on individual secrets "is not recommended" (Key Vault RBAC guide, 2026-08-21). A shared vault's blast radius is every app, and one mis-scoped grant exposes all of them.
- Grants: `Key Vault Secrets User` to `id-<app>-<env>-eso` and `id-octopus-deploy-<env>`; `Key Vault Secrets Officer` to `id-platform-lifecycle`.
- Vaults are free. Private endpoints would cost about $7 per vault (about $790 at 36 apps), so app vaults use the firewall instead: default deny, allowing the cluster's static outbound IP and `<octopus-cloud-static-ips>`.
- `keyVault: false` only for stateless apps without secrets.

## 4. Observability

- One `log-platform` workspace in resource-context access mode; app owner groups get `Reader` on their App Insights only.
- `appi-<app>-<env>`, SLO alert `slo-fast-burn-<app>-<env>` and action group `ag-<app>` come from the descriptor, at severity 3 by default.
- Platform alerts (Kyverno, ESO, backup age, node pressure) go to `ag-platform-oncall`.
- A daily ingestion cap (alert at 80 %) bounds cost but, once hit, blinds every app.
- Sleep suppression is now one rule, `apr-sleep-training`, scoped to `rg-platform-aks-training` and `rg-platform-apps`, never muting activity-log alerts. New apps need no change.

## 5. Cost with sleep (monthly, list prices)

Assumptions:
- Nodes: system pool 1 × D2ds_v5; app pool B4ms, about 12 GiB usable each [VERIFY]; ephemeral OS disks.
- Memory: 3 GiB per app-environment (SQL 2 GiB plus the app), plus 4 GiB of add-ons.
- Awake hours: 66, 176 and 264 (the whole working window).

| | 1 app | 12 apps | 36 apps |
|---|---|---|---|
| App nodes | 2 | 10 | 10 with app-level scale-to-zero (≈⅓ of app-environments running) |
| Compute | $35 | $376 | $564 |
| LB + IP, ACR | $42 | $42 | $42 |
| PVCs ($3.60/app) | $4 | $43 | $130 |
| Log Analytics (3 GB + 0.5 GB/app) | $10 | $25 | $58 |
| Private endpoints (platform vault, backups) | $15 | $15 | $15 |
| **Total** | **≈ $106** | **≈ $501** | **≈ $808** |
| Always on | $461 | $1,684 | $1,804 |

- **36 apps without scale-to-zero:** 28 app nodes and 114 vCPUs, which exceeds the 65-vCPU quota. That needs a quota increase and costs about $1,759.
- **Removed from today's bill:** Azure SQL, the prod cluster, and six of the eight private endpoints.

## 6. Migration of `terraform/**`

| Today | Becomes |
|---|---|
| `foundation/*.tf` | Platform names; SQL removed; locks, policies and PIM move to `terraform/owner`; `id-env-lifecycle-{nonprod,prod}` becomes `id-platform-lifecycle`; workload UAMIs move to `terraform/apps` |
| `environment/*.tf` | `terraform/cluster`: one Free-tier cluster; `data-services.tf` keeps only the platform vault; App Insights and alerts move to `terraform/apps` |
| `{nonprod,prod}.tfvars` | `cluster/training.tfvars` plus `apps/*.yaml` |
| — | `terraform/apps` and `terraform/owner`, with mocked `terraform test` plans run by the kit's validator |

No `moved` blocks or state surgery. Runbook renames: `db-restore-pitr` becomes `db-restore`; `rotate-sql-passwords` becomes `rotate-db-passwords`.

## 7. Naming contracts

| Object | Name |
|---|---|
| Resource groups | `rg-platform-shared`, `rg-platform-aks-<cluster>`, `rg-platform-apps` |
| Cluster, network | `aks-platform-<cluster>` (`<cluster>` = `training`), `vnet-platform`, `snet-aks-<cluster>`, `snet-private-endpoints` |
| Shared stores | `log-platform`, `<kv-platform-training>`, `<backup-storage-account>` |
| Platform UAMIs | `id-platform-lifecycle`, `id-octopus-deploy-{tdd,uat,prod}`, `id-octopus-acr-pull`, `id-aks-<cluster>-{controlplane,kubelet}`, `id-eso-platform-<cluster>`, `id-kyverno-<cluster>` |
| Octopus subjects | `space:<space-slug>:environment:<env>` (deploy accounts, keys `space` and `environment`); `space:<space-slug>:project:platform-infrastructure:environment:infra` |
| App UAMIs, subjects | `id-<app>-<env>-{eso,backup}`, optional `id-<app>-<env>-app`; `system:serviceaccount:<app>-<env>:{eso,db-backup,<app-sa>}` |
| App vault, secrets | `kv-<app>-<t\|u\|p>-<hash4>`; `db-sa-password`, `db-migrator-password`, `db-app-password`, plus descriptor keys |
| Registry | `apps/<app>/<image>`, `apps-previews/<app>/<image>`, `platform/<image>`; tokens `cf-<app>-release`, `cf-<app>-preview`, `cf-platform-ci`, `cf-platform-pull` |
| Backups, monitoring | Container `<app>-<env>`; `appi-<app>-<env>`, `slo-fast-burn-<app>-<env>`, `ag-<app>`, `ag-platform-oncall`, `apr-sleep-<cluster>` |

`<app>` is at most 12 characters from `[a-z0-9-]` (for example `20260923-001`), which keeps vault names within 24 characters.

## 8. Risks and decisions

**Risks**
1. The namespace boundary is soft: tdd code shares nodes with prod namespaces.
2. The 20-credential limit per UAMI forces deploy-account subject keys `space` and `environment`, so any project deploying to an environment can use its account. Octopus permissions and reviewed OCL remain the control.
3. The shared `id-octopus-deploy-<env>` reads every app vault of its environment.
4. Backups: RPO 24 hours, no point-in-time restore, and a single-zone disk per database.
5. Any app's job wakes the whole cluster; shared idle time shrinks sleep as apps grow.
6. Prod fail-closed admission and the add-on warm-up now affect every app at once.
7. The stored provisioner keeps its widened rights until retired (R4).

**Decisions for the chief architect**
1. Vaults per app-environment without private endpoints (recommended), or one shared vault.
2. The Owner script update and `terraform/owner`.
3. App-level scale-to-zero before about 15 apps, or a vCPU quota increase.
4. Free-tier cluster, B-series app nodes and ephemeral OS disks.
5. No managed-database component (recommended); add one per app if Express limits bind.
6. Infra environment `infra` and deploy subject keys (with octopus-architect).

## Addendum: per-app Azure scope and keyless platform access

Supersedes §1's Owner-script update, §7's deploy identities and subjects, risks 2, 3, 7 and decision 6. Tiers: Addendum 2.

**1. Scope**
- App-owned Azure services live in `rg-app-<app>-<tier>`; tdd and uat pipelines never reach prod.
- `rg-platform-<tier>-apps` keeps the app's identities, vaults and alerts, so the app cannot re-federate, open or mute them. Role wiring inside `rg-app-<app>-<tier>` comes from the descriptor via `terraform/apps`.
- Conflict with §10 ("created by the per-app Terraform layer"): tier identities cannot create groups, so `terraform/owner` does, from the same descriptor.

**2. Per-app Octopus identity**
- `id-<app>-<env>-deploy`, used via the Octopus account `azure-<app>-<env>`, restricted to `<env>`.
- One federated credential per app project, with subject keys `space`, `project` and `environment`: `space:<space-slug>:project:<project>:environment:<env>`. A foreign project selecting the account presents its own slug, which Entra refuses. Only Octopus Terraform therefore creates, deletes or re-slugs app projects.
- Limit: 20 projects per app-environment identity.
- Grants: `Contributor` on `rg-app-<app>-<tier>`, limited by allowed-type and VM-SKU policies, with a budget alert, plus `Key Vault Secrets Officer` on the app's vault. No AKS role: only Argo CD applies.

**3. Owner script**
- The roles suffice, minus `SQL DB Contributor` and `AcrPull`, plus descriptor-requested data roles; pre-approve the Service Bus, Event Hubs and Storage Queue ones.
- The scope does not. A group grant cannot cover later groups, and subscription scope spans tiers (change: Addendum 2 §2).
- ABAC cannot limit the target scope, so every `terraform/apps` plan gets security review.

**4. Threat review: platform secrets never enter app pipelines**
- **Key locations:** the sensitive variables of `platform-infrastructure`, and the gateway Secrets.
  - Never a library set: any app project editor can include one, so `WorkOrders Platform Automation` must go.
  - Never a Codefresh context: contexts attach by name [VERIFY access control]. Codefresh hands off via a release manifest and feed trigger.
- **Wake:** app processes start with a "Deploy a Release" step for `platform-wake`, set to *Deploy always*.
  - The child task runs the platform's process and variables as `id-platform-wake-<tier>`, which holds `Contributor` on the cluster and `apr-sleep-<tier>` only.
  - It takes no prompted variables and outputs only `Wake.CompletedAt` [VERIFY child identity and variable flow].
  - Teams need only Deployment Creator on it; the worst misuse is an unwanted wake.
  - Today's step calls `env-wake` with the key. Waking in-process, without step 3's REST poll [VERIFY], makes it keyless.
- **Residual risks:**
  1. Worker pools are not project-scoped [VERIFY] and are invisible to Platform Hub policies. App steps can therefore reach reused `hosted-ubuntu` VMs [VERIFY reuse] and capture platform tokens; key-holding runbooks belong in a platform-only space.
  2. Secret-based accounts ignore subjects. Retire `Azure Runtime Provisioner` before onboarding (R4).
  3. A container escape reaches a gateway Secret, whose key opens both tiers. Run add-ons on the system pool (`CriticalAddonsOnly` taint, toleration denied to app namespaces), and give each tier its own gateway service account (ADR-IR16).
  4. The platform Git credential writes all of `gitops/**`. Only annotation scoping confines an app [VERIFY script access].

## Addendum 2: tier segmentation

Supersedes §1's layers, every group name, §4's workspace and rule, §5's cost and decision 6's `infra`. In §7, `<cluster>` becomes `<tier>`: `nonprod` (tdd, uat, previews) or `prod`, each with its own cluster.

**1. Groups, layers, state** (creators are `terraform/` layers)
- `rg-platform-global` (owner): owner and build state.
- `rg-platform-build` (owner, build): registry, `id-platform-build`, `id-octopus-acr-pull`.
- `rg-platform-<tier>-shared` (owner, foundation): VNet, outbound IP, logs, backup and state accounts, `id-platform-lifecycle-<tier>`.
- `rg-platform-<tier>-aks` (owner, foundation, cluster): cluster, platform vault, sleep rule; wake, cluster, ESO and Kyverno identities.
- `rg-platform-<tier>-aks-nodes` (AKS): nodes, load balancer.
- `rg-platform-<tier>-data` (owner, CSI driver): database disks.
- `rg-platform-<tier>-apps` (owner, apps): app identities, vaults, App Insights, alerts.
- `rg-app-<app>-<tier>` (owner, app pipelines): app-owned services.

**Owner layer.** Owner (PIM) creates every group and layer identity. It alone acts at subscription scope, where a tier identity would span tiers. The price is one Owner apply per new app; a subscription per tier avoids it and doubles the vCPU quota (decision).

**Tier layers** run as `id-platform-lifecycle-<tier>`, from `platform-infrastructure` in `infra-<tier>`, with state in the tier's account. `apr-sleep-<tier>` covers the tier's `-aks` and `-apps` groups.

**2. Identities and grants**
- `id-platform-lifecycle-<tier>`: `Contributor` and constrained RBAC Administrator on its tier's groups (only the latter on `rg-app-*`), plus a state data role.
- `id-platform-wake-<tier>`: `Contributor` on the cluster and on `apr-sleep-<tier>`.
- `id-aks-<tier>-controlplane`: `Network Contributor` on its subnet and IP, `Managed Identity Operator` on the kubelet identity, `Contributor` on `-data`.
- `id-aks-<tier>-kubelet` and `id-kyverno-<tier>`: `AcrPull` on the registry.
- `id-platform-build`: `Contributor` and constrained RBAC Administrator (`AcrPull` only) on `rg-platform-build`.

App identities stay in their tier (Addendum 1). Prod's `AcrPull` is the one recorded cross-boundary read; the gateway Secrets' Space Manager key remains cross-tier (Addendum 1, risk 3).

**Owner-script change** (into `terraform/owner`):
1. Drop the subscription-scope grant.
2. On each tier group except `-aks-nodes`, grant `id-platform-lifecycle-<tier>` `Contributor` and the constrained RBAC Administrator; on `rg-app-*`, grant only the latter. Keep today's ABAC condition, minus `SQL DB Contributor` and `AcrPull`, for service principals only.
3. Grant `id-platform-build` the same pair on `rg-platform-build`, with `AcrPull` only.
4. Revoke the provisioner's grants and Graph permissions (R4, R6).

**3. Data groups**
- `terraform/cluster` sets StorageClass `platform-retain`: `disk.csi.azure.com`, `skuName: StandardSSD_LRS`, `resourceGroup: rg-platform-<tier>-data`, `reclaimPolicy: Retain`, `volumeBindingMode: WaitForFirstConsumer`.
- The control-plane identity drives the CSI driver but holds rights only on the node group, so it gets `Contributor` on `-data` [VERIFY a narrower role].
- Disks survive rebuilds and re-attach as static PVs (`volumeHandle`: the disk ID). `rg-platform-prod-data` gets `CanNotDelete`.

**4. One shared build registry (decided)**
- References are identical everywhere; Octopus promotes by moving pins.
- **Access:** Codefresh pushes with per-app tokens `cf-<app>-release` on `apps/<app>/*` (content and metadata read/write, no delete) and platform tokens on `platform/*`. Nothing in prod pushes. The Octopus feed and each cluster's kubelet and Kyverno identities read.
- **Prod safety:**
  - Prod Kyverno admits only images signed by the app's release pipeline, fail-closed.
  - After signing, the pipeline locks (`write-enabled false`) the tag, image, signature and SBOM.
  - Digest pins are recommended for prod: Octopus writes `<version>@sha256:<digest>` from the release manifest if its Argo CD step can [VERIFY], and prod Kyverno then requires digests. Tag pins trust a lock that the app's own token can lift (locking needs `metadata/write`).
  - **Retention:** nightly `acr purge --ago 30d --keep 10` skips locked manifests, never with `--untagged` (referrers may be untagged [VERIFY]). A weekly runbook (`id-platform-build`) unlocks versions past the last 10 per app unless `gitops/**` pins them.
  - Account-wide integrations (ADR-IR19) let any app pipeline push to any app's repository. Signatures, approvals and digest pins contain this.
- **SKU decision: Standard, no private endpoint.** Private endpoints and network rules need Premium (about $50 versus $20, plus endpoints), and Codefresh SaaS and Octopus Cloud use the internet anyway. Controls: Entra identities, scoped tokens, no admin user, no anonymous pull.

**5. Networking without peering**
- `vnet-platform-nonprod` and `vnet-platform-prod`: no peering, non-overlapping ranges (a VPN stays possible), per-tier private DNS.
- No flow crosses tiers: Argo CD, Octopus workers and gateways connect outbound, and clusters pull from the public registry.
- Tier vault and storage firewalls admit only the tier's outbound IP and `<octopus-cloud-static-ips>`, so nonprod pods cannot reach prod secrets even with stolen tokens.
- API servers stay public, with Entra RBAC, local accounts off and authorized ranges `<octopus-cloud-static-ips>` (Q7 [VERIFY]).

**6. Cost with sleep (monthly, list prices)**

§5's assumptions apply per cluster, except that prod is awake 20, 66 and 132 hours (release windows) against nonprod's 66, 176 and 264. Each tier has 3 GB of logs and two private endpoints.

| | 1 app | 12 apps | 36 apps (scale-to-zero) |
|---|---|---|---|
| App nodes, nonprod + prod | 1 + 1 | 7 + 4 | 7 + 4 |
| Compute | $29 | $332 | $529 |
| 2 × (LB + IP), ACR, 4 endpoints | $93 | $93 | $93 |
| PVCs | $4 | $43 | $130 |
| Log Analytics | $18 | $33 | $66 |
| **Total** | **≈ $143** | **≈ $501** | **≈ $818** |
| One cluster (§5) | $106 | $501 | $808 |

- Prod awake as long as nonprod adds $15, $103 and $124.
- Add-ons on a D4ds_v5 system node (Addendum 1, risk 3) add at most $12.
- 36 apps without scale-to-zero: 19 + 10 app nodes, 120 vCPUs with both awake (shared quota 65), about $1,610.
