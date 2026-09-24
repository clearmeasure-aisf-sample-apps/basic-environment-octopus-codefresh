# Multi-app GitOps: memo from the gitops-architect

Scope: `argocd/**`, `gitops/**`; directive §1–§10 and the sleep/wake contract. "Checked" = official docs read 2026-09-24.

## 0. The only mandatory GitOps rules

1. **Fence.** Platform-rendered AppProject `app-<app>`: destinations exactly the app's namespaces; no cluster-scoped kinds; ResourceQuota, LimitRange and NetworkPolicy denied.
2. **One applier.** Argo CD alone writes app namespaces (E27).
3. **Pins.** Octopus changes image tags only, in Applications annotated with the app's own Octopus project slugs; only the platform sets annotations.

Everything else is optional: packaging (Helm, Kustomize, raw), database components, routes.

## 1. Layout, generators, tenancy, pins

```text
apps/<app>.yaml                               descriptor
argocd/clusters/training/                     platform root: namespaces, projects, add-ons, ApplicationSet `apps`
gitops/platform/tenant/                       Helm chart: everything per app that the platform owns
gitops/platform/components/db/<engine>/, db-credentials/{keyvault,generated}/
gitops/platform/ingress/                      Gateway, wildcard certificate
gitops/apps/<app>/                            app-owned, any layout
gitops/apps/<app>/envs/<env>/<deployable>/    one Application each; its pin lives here
```

**Generator.** ApplicationSet `apps`: Git files generator over `apps/*.yaml` (checked), `applicationsSync: create-update` (needs the controller's policy-override flag), `preserveResourcesOnDeletion: true`. It creates `tenant-<app>` (project `platform-tenants`, `FailOnSharedResource=true`), rendering the tenant chart with `$values/apps/<app>.yaml`.

The chart renders these objects in sync waves −2, −1 and 0:

| Object | Notes |
|---|---|
| AppProject `app-<app>`, and `app-<app>-previews` | Rule 1; roles bind the descriptor's Entra groups |
| Namespaces `<app>-<env>`, `<app>-<part>-<env>` | Labels `platform/app`, `environment`, `tier: app`, Pod Security `restricted`; `Prune=confirm,Delete=confirm` |
| ResourceQuota and LimitRange `tenant` | sre-security values; the descriptor can lower them only |
| NetworkPolicies | sre-security's default-deny set, plus allows from the app's own namespaces, `platform-ingress` and `octopus-worker-<env>` |
| ClusterSecretStore `<app>-<env>` | Reads the app vault as `external-secrets/eso-<app>-<env>`; conditions match the namespace labels |
| Workload-identity ServiceAccounts | For apps that use Azure services; client IDs come from `terraform/apps` outputs, copied into the descriptor |
| Applications `<app>-<deployable>-<env>` | Automated sync with self-heal, `PruneLast`, retry; Octopus annotations, plus `image-replace-paths` for Helm |
| ApplicationSet `<app>-previews` | Phase 6: pull-request generator |

**Why a chart.** Packaging, namespaces and Octopus scoping differ per deployable; ApplicationSets cannot create AppProjects. A templated project is safe only while the platform controls every generator source (ApplicationSet security page, checked): here the reviewed descriptor, and the `app-` prefix never yields `platform-*` or `default`.

**Pin path.** Octopus writes in `gitops/apps/<app>/envs/<env>/<deployable>/`, which it finds through the annotations. Argo CD detects the packaging:

| Packaging | Field Octopus writes |
|---|---|
| Kustomize | `images[].newTag` (the §7.6 shape) |
| Helm | Values at `image-replace-paths` (checked). Every referenced values file is rewritten (E1), so pinned paths live only in the env file. The chart comes from Git (E2). |
| Raw | `image:` of known kinds; CRDs are excluded |

## 2. Database components (optional, platform-neutral)

- **`db/mssql-2022-express`**
  - StatefulSet `db` with PVC template `data`: 8 Gi on `platform-retain`, retention `Retain/Retain` (stable in 1.32, checked).
  - Services `db` (1433) and `db-hl`.
  - `MSSQL_PID=Express`, UID 10001, 2 GiB and 100m requested, a 2.5 GiB limit and `MSSQL_MEMORY_LIMIT_MB=2048`.
  - Readiness: `sqlcmd -C -Q "SELECT 1"` from `/opt/mssql-tools18` (2022 CU14 and later, checked).
  - `MSSQL_DB/USER/PASSWORD` create the database and a login at start (checked; image versions and rights [VERIFY]); an idempotent hook Job adds `migrator` and `app` logins.
- **`db/postgres-17`:** the same shape on 5432, with `POSTGRES_*` variables, `pg_isready` and 512 MiB.
- **Credentials component.**
  - `keyvault`: Secrets `db-sa`, `db-migrator` and `db-app` come from store `<app>-<env>`, which `terraform/apps` fills.
  - `generated`: the same Secrets from ESO Password generators, with `CreatedOnce`.
  - Named environments use `keyvault`: Octopus reads the migrator password from the vault, never across Rule 1. Previews use `generated` with hook migrations (ADR-C4).

Opt-in: built with kustomize 5.8.1, valid under `kubeconform -strict`.

```yaml
# gitops/apps/workorders/envs/tdd/db/kustomization.yaml
namespace: workorders-tdd
components:
  - ../../../../../platform/components/db/mssql-2022-express
  - ../../../../../platform/components/db-credentials/keyvault
configMapGenerator:
  - name: db-settings        # read by the components; a replacement sets the store
    literals: [MSSQL_DB=workorders, MSSQL_USER=app, SECRET_STORE=workorders-tdd]
```

**Before the first pin.** Onboarding adds this folder; the tenant chart creates `<app>-db-<env>`, a separate Application, so the database runs before any release and never depends on app health.

**Readiness gate.**
- That Application carries the Octopus annotations and the label `platform/role: database`.
- The optional migration template first runs Octopus's "Wait for Argo CD Applications" with that label filter (checked). This also covers recovery after a wake.
- The migrator then retries its login until `Migration.DbReadyTimeoutSeconds`.
- It is [VERIFY] that the image-tag step skips annotated Applications with no matching image.

## 3. Shared ingress

- **Gateway.** `platform-ingress/platform-gateway`: listener `https` for `*.<apps-domain>`, `http` redirects only; routes only from `tier: app` namespaces. Controller stays Q5; prefer in-cluster [VERIFY].
- **Hostnames.** `<app>-<env>`, `<app>-<part>-<env>` and `<app>-pr-<n>`, each under `.<apps-domain>`. Each name is a single label, so one wildcard certificate serves every app. A Kyverno rule keeps each app on its own names.
- **DNS.** A wildcard A record points to static IP `pip-platform-ingress`, pinned with `service.beta.kubernetes.io/azure-pip-name` and `azure-load-balancer-resource-group`. Outside the node resource group this needs a control-plane grant (checked).
- **TLS.**
  - cert-manager with ClusterIssuer `letsencrypt-dns01`: Azure DNS through workload identity with DNS Zone Contributor (checked). Wildcards require DNS-01 (Let's Encrypt, checked).
  - Certificate `apps-wildcard` produces Secret `apps-wildcard-tls`. Renewal runs while the cluster is awake.

## 4. Sizing

**Assumptions.**
- AKS 1.29+ reserves min(20 MB × maxPods + 50 MB, 25 % of memory) plus 100 Mi (checked).
- The 65-vCPU quota (16 four-vCPU nodes) holds system 2, apps 12, surge 1.
- One app-environment needs ≈2.75 GiB: SQL Express 2 GiB (Microsoft minimum; buffer pool 1,410 MB, checked) plus 0.75 GiB for the work-order services. Three environments need 8.25 GiB.

| Apps pool: 12 nodes, `max_pods` 110 | Allocatable | After ≈6 GiB add-ons | SQL apps, all 3 environments |
|---|---|---|---|
| `Standard_D4ds_v5` (16 GiB) | 13.8 GiB | ≈159 GiB | ≈19 |
| `Standard_E4ds_v5` (32 GiB) | 29.8 GiB | ≈351 GiB | ≈42 |

- **CPU.** Binds at ≈42 apps with ≈0.35 vCPU requested per app-environment.
- **Recommendation.** E-series apps pool, `max_pods` 110; prices and EDSv5 family quota [VERIFY].
- **Disks.** PVCs bill while asleep: ≈$1.80 a month per SQL app.
- **Scale-to-zero.** Not automatic: the cluster already sleeps (ADR-IR33), self-heal owns `replicas`, cold SQL delays requests, and migrations need a running database. Apps park environments by pull request; revisit when the autoscaler hits its maximum.

## 5. Migration

Nothing is provisioned; no live adoption.

| Today | New |
|---|---|
| `gitops/workorders/base/*` | `gitops/apps/workorders/app/base/`. Drop workload identity, SecretStore, `workorders-eso` and NetworkPolicies. Point ExternalSecrets at store `workorders-<env>` and the HTTPRoute at `platform-gateway`. |
| `envs/<env>/kustomization.yaml` | `envs/<env>/app/kustomization.yaml`. Images become `<acr-name>.azurecr.io/apps/workorders/*`. |
| `envs/<env>/config/` | `envs/<env>/app/config/`. The SQL-login connection string (`Server=tcp:db,1433`) moves into the Secret; the hostname becomes `workorders-<env>.<apps-domain>`; client-ID, vault and NetworkPolicy patches go. |
| (new) | `envs/<env>/db/` |
| `components/bluegreen`, `previews/` | Under `gitops/apps/workorders/`; `database.yaml` → components, `generated` |
| `argocd/clusters/{nonprod,prod}/` | `argocd/clusters/training/`: one add-on set and gateway `argocd-training` (tdd, uat, prod); platform namespaces plus `platform-ingress` and `cert-manager`; projects `platform-addons`, `platform-tenants`, `default`. |
| `apps/workorders-*.yaml`, `optional/workorders-previews-appset.yaml` | Deleted; the tenant chart renders both |
| `bootstrap/*-{nonprod,prod}.yaml` | `*-training.yaml`: RBAC `p, octopus, applications, get, app-*/*, allow` (the same for `logs`), plus the policy-override flag |

## 6. Naming contracts

| Item | Contract |
|---|---|
| `<app>` | `^[a-z][a-z0-9]{2,15}$`, with no dash, so a name like `<app>-<env>` splits only one way. Reserved: `argo`, `argocd`, `cert`, `default`, `external`, `kube`, `kyverno`, `octopus`, `platform`, `system`. App #1 is `workorders`. |
| `<part>`, `<deployable>` | `^[a-z][a-z0-9]{1,11}$`; the database deployable is `db` |
| Namespaces | `<app>-<env>`, `<app>-<part>-<env>`, `<app>-pr-<n>`. The validator rejects names another descriptor owns. |
| Argo CD | `apps`, `tenant-<app>`, `<app>-<deployable>-<env>`, `<app>-<deployable>-pr-<n>`; projects `platform-addons`, `platform-tenants`, `app-<app>`, `app-<app>-previews`, `default` |
| Annotations | `argo.octopus.com/project: <app's slug>`, `argo.octopus.com/environment: <env>`, `argo.octopus.com/image-replace-paths` |
| Data | Store `<app>-<env>`; Secrets `db-sa`, `db-migrator`, `db-app`; ConfigMap `db-settings`; `db`, `db-hl`, `data-db-0` |
| Ingress | `platform-gateway`, `apps-wildcard-tls`, `<apps-domain>`. Retire `<{env}-hostname>`, `<gateway-*>` and `<previews-hostname-suffix>`. |

## 7. Could Argo CD or the gateway wake the cluster?

No. Argo CD, the gateway and the `k8s-<env>` workers sleep with the cluster, and a pin-commit webhook comes after migrations. Keep the external Octopus wake: Deploy a Release into `platform-wake` [VERIFY permissions].

## 8. Risks and decisions for the chief architect

1. **`platform-tenants` is admin-equivalent:** CODEOWNERS on `apps/*.yaml` and `gitops/platform/**`, validator, reserved names, Helm `fail` guards.
2. **Conflict with sre-security.** They propose ServiceAccount `<app>-<env>:eso`, which lives in the app namespace where app pods could mount it. This memo keeps it in `external-secrets`, behind a ClusterSecretStore, which scales to several namespaces with one federated credential. Decide.
3. **Conflict with the codefresh-engineer.** Octopus cannot promote OCI chart versions (E2). Helm apps must vendor the chart into Git, or pin image tags only, in Git values beside an OCI chart [VERIFY]. Decide.
4. **Octopus Git credential:** assumed unreadable by scripts [VERIFY]; otherwise any app process could write any pin, and the bot-path audit only detects it afterwards.
5. **Shared cluster.** Prod shares nodes with tdd tests: PriorityClass per environment, set by Kyverno.
6. **Single-replica databases:** drains cause brief outages; disks reattach after stop/start on non-zonal pools (zonal [VERIFY]).
7. **Decide:** the tenant chart versus scaffolded YAML; `keyvault` as the default; E4ds_v5; the Gateway controller and cert-manager; manual-only parking; the `<app>` rule.

## Addendum: two clusters (directive §11)

This replaces the single `training` cluster in §1 and §3–§5; the rest stands.

**Topology.** Two clusters:
- `nonprod`: tdd, uat and previews;
- `prod`: prod.

Each has its own Argo CD (`argocd-nonprod`, `argocd-prod`), gateway registration and root `argocd/clusters/{nonprod,prod}/`, so today's split and bootstrap names stay. Neither instance manages the other cluster; a hub would hold cross-tier credentials.

**Generators per cluster.**
- Each root holds its own ApplicationSet `apps` over the same `apps/*.yaml`, with generator `values: {cluster: <cluster>}` (checked) passed to the tenant chart.
- The chart renders only that cluster's environments. The map is fixed (tdd, uat, previews → nonprod; prod → prod); descriptors cannot remap it.
- `tenant-<app>` and `app-<app>` exist on both, each listing only its own cluster's namespaces. `app-<app>-previews` is nonprod only.

**Namespaces.**
- nonprod: `<app>-{tdd,uat}`, `<app>-<part>-{tdd,uat}`, `<app>-pr-<n>`, and `octopus-worker-{tdd,uat}`.
- prod: `<app>-prod`, `<app>-<part>-prod`, and `octopus-worker-prod`.

**StorageClass per tier.** Each root defines `platform-retain` under the same name, so the database components stay tier-neutral:
- `disk.csi.azure.com`, `skuName: StandardSSD_LRS`;
- `resourceGroup: rg-platform-<tier>-data` (checked: an existing resource group, where the cluster identity needs Contributor);
- `reclaimPolicy: Retain`, `WaitForFirstConsumer`, expansion allowed.

Disks outlive rebuilds. Attaching a retained disk to a rebuilt cluster needs a static-PersistentVolume runbook [VERIFY].

**Images (directive §12).** One registry in `rg-platform-build` serves both clusters, so references are identical in every environment: `<acr-name>.azurecr.io/apps/<app>/<image>`, in the base and in every pin. Octopus promotes by moving the tag only.
- **Tags, not digests.** Prod pins stay tags. Tags are write-locked after signing, and prod Kyverno admits only the app's signed images, so a tag cannot change underneath a pin.
- **Digests.** Kustomize supports `images[].digest`, but whether Octopus's image-tag step can write it is [VERIFY]. If the chief requires digests, the release manifest's digest is the value to pin.
- **Retention.** Every tag named under `gitops/apps/*/envs/**` is the purge job's keep-list, together with its referrers.

**Ingress and DNS.** Each cluster has its own Gateway, static IP, cert-manager and wildcard certificate. One wildcard record cannot point at two IPs, so each tier gets a child zone in its own resource group, delegated from the parent zone in `rg-platform-global`.
- Hosts become `<app>-<env>.<apps-domain-nonprod>` and `<app>-<env>.<apps-domain-prod>`.
- Each tier's cert-manager identity has DNS Zone Contributor on its own zone only.

**Secrets and Azure services.**
- Stores `<app>-<env>` exist only on their tier's cluster, and read `rg-platform-<tier>-apps` vaults with that tier's ESO identities.
- App Azure services live in `rg-app-<app>-<tier>`. GitOps receives only connection data (through the vault) and workload-identity ServiceAccounts. There are no in-cluster Azure operators, so no cluster holds an Azure write credential.

**Sleep and wake per cluster** (ADR-IR33). Prod wakes only for prod deployments and runbooks. After any wake, the §2 gate waits for `<app>-db-<env>` to recover.

**Sizing.** One 65-vCPU quota covers both tiers (same subscription [VERIFY]):
- nonprod: system 1, apps up to 9, surge 1: 44 vCPU;
- prod: system 1, apps up to 3, surge 1: 20 vCPU.

| Cluster (E4ds_v5) | Usable memory | Per SQL app | Apps |
|---|---|---|---|
| nonprod | ≈262 GiB | 5.5 GiB | ≈47 |
| prod | ≈83 GiB | 2.75 GiB | ≈30 |

Prod binds first, and grows only by shrinking nonprod. Extra fixed cost: a second load balancer and IP (≈$22 a month). The prod system node bills only while awake.

**Decisions.**
1. One Argo CD per cluster (recommended) or a hub.
2. Child DNS zones per tier.
3. A 44/20 quota split, or a quota increase.
4. Tag pins in prod, or digests if Octopus can write them.
