# Multi-app: onboarding kit, teaching and class-scale operations (pragmatist memo)

> **Note (superseded by the public-repo decision, #47):** the environment repo is now public, so the private-repo and push-ruleset recommendations below no longer apply; see design/platform-design.md, section 6.2 (Public repository). This paper is kept unchanged as history.

Scope: the kit, contracts, checks, docs and labs. Builds on directive §1–§10 and the three other memos.

## 1. Onboarding kit

### 1.1 Descriptor `apps/<app>.yaml`

It declares only what the platform creates or fences. Shape (languages, services, build commands, images, migrators) stays in the app's own files (directive §9), shortening directive §5's list.

```yaml
schema: 1
name: workorders                 # slug; the repo keeps its own name
repos: [clearmeasure-aisf-sample-apps/20260923-001]
owners: {entraGroup: "<entra-group-object-id-app-workorders>"}
lifecycle: {cohort: "2026-09", expires: "2027-06-30"}
deployables:
  - {name: app, octopusProject: workorders}
releases:
  - {codefreshProject: workorders, pipeline: release, octopusProject: workorders}
database: {engine: mssql-2022-express}
```

**Required:**
- `schema`.
- `name`: `^[a-z][a-z0-9]{2,11}$`, reserved words rejected. Every per-app name derives from it.
- `repos`: drives triggers and Git access.
- `owners`: drives the Argo CD role and Octopus team `app-<app>`.
- `lifecycle.cohort` and `lifecycle.expires`: drive tags and retirement.
- `deployables[]` (`name`, `octopusProject`, optional `part` and `imagePaths`): drive namespaces, Applications and annotations.
- `releases[]`: drives the signer identity, the registry token and the feed trigger.

**Optional (default):**
- `environments` (tdd, uat, prod).
- `database` with `engine` and `credentials` (none).
- `azure` services and roles, which create `rg-app-<app>` (none).
- `secrets`, `workloadIdentity`, `hostnames`, `quotas` (lower only), `previews`, `ciProfile` and `alerts` (derived or off).
- `pipelines.location` (`app-repo`).
- `status` (`active`; otherwise `frozen` or `archived`).

`apps/schema.json` (JSON Schema 2020-12, `additionalProperties: false`) rejects wrong types, patterns, enums, reserved words and typos. The validator adds what a schema cannot: uniqueness of names, namespaces, hostnames and projects across descriptors; releases targeting declared Octopus projects; capacity; expiry; blast radius.

### 1.2 Commands

All run offline, credential-free and idempotent. Registration stays with its owners: `terraform/apps` and `octopus/terraform` applies (`for_each` over descriptors), `codefresh/register.sh --app`, Argo CD's `apps` ApplicationSet.
- `scripts/apps/new <app> --starter <s>`: writes a minimal descriptor with TODOs.
- `scripts/apps/scaffold <app> --starter <s>`: copies the starter Codefresh YAML, OCL, `gitops/apps/<app>/` and `PLATFORM.md` once, and records `.scaffold`. `--diff` offers later starter changes.
- `scripts/apps/render [<app>]`: writes platform-owned Codefresh specs and `policies/kyverno/apps/<app>.yaml`.
- `scripts/apps/check [<app>]` (env-checks: `validate-all.sh apps`): schema, cross-app rules, `render` drift, dry renders (tenant chart, kustomize, kubeconform), mocked `terraform test`.
- `scripts/apps/list`: prints the inventory; it is never committed.
- `scripts/apps/retire <app> --freeze|--archive`: sets `status` and prints the teardown steps.

**Blast radius:** `check` fails an onboarding pull request touching anything beyond `apps/<app>.yaml` and `<app>`-scoped paths, making "nothing platform-wide changes" testable; hence no per-app `CODEOWNERS` lines or committed index.

### 1.3 First day

**Instructor** (about 30 minutes per app [UNVERIFIED]):
1. Create the app repo; run `new`, fill in about ten lines, then `scaffold`, `render`, `check`.
2. Open one platform pull request. Security owners review `apps/*.yaml`, since it grants Azure roles and quotas.
3. After merge: apply `terraform/apps` (runbook, approved) and `octopus/terraform`, run `register.sh --app`; Argo CD creates the fences and the database.
4. Smoke test: an app commit becomes a signed tdd release at `https://<app>-tdd.<apps-domain>`.

**Student:**
1. Team membership grants app-repo write, Octopus `app-<app>` (deploy own projects to tdd and uat), Argo CD read and logs, and Codefresh build logs.
2. Read `PLATFORM.md`: URLs, namespaces, pipelines, projects, the four guarantees and what may change.
3. Merge a change and follow it to tdd (lab 18). No platform-repo access is needed.

## 2. Teaching

Starters follow shapes, not languages:
- **Codefresh:** `minimal`, `multi-image`, `helm-chart`, plus language examples.
- **OCL:** `deploy-minimal` (verify-release, wake, image tags, verify), `deploy-with-db` (adds database wait and migration), `db-runbooks` per engine.
- **GitOps:** Kustomize, Helm or raw.
- **Databases:** `mssql-2022-express`, `postgres-17`.

**Labs** use `<app>`, with `workorders` as the worked example:
- 18 Follow a commit: any app (manifest, trigger, release, pin, sync).
- 19 Schema change: apps with a database.
- 20 Promotion and hotfix: the app's own project and lifecycle.
- 21 Drift and rollback: any app.
- 22 Environment lifecycle: a cluster demo, plus onboarding and retiring an app.
- 23 Sleep and wake: cluster-wide, with idle time counted across all apps.
- 24 Own the pipeline (new): change the scaffolded pipeline, then break each guarantee and watch lint, registry and admission refuse.

**Students edit:**
- their app repo: code, Dockerfiles, `.codefresh/`, `.octopus/`;
- `gitops/apps/<app>/`, only through an instructor, who owns that pull request.

**Never:** descriptors, specs, contexts, platform projects, the rest of the platform repo, other apps. Permissions, AppProjects, NetworkPolicy, registry tokens and signer policies enforce this, not lab text.

## 3. Cognitive-load guardrails

- **Docs:** platform docs (README, bootstrap, tool boundaries, cutover, runbooks, labs) are app-neutral; per-app docs are the descriptor and `PLATFORM.md`.
- **Name lint (new):** a platform file outside the app-scoped paths that names an app fails. `workorders` appears only as the labelled example.
- **Checks** verify the handshake and the guarantees, never shape:

| Check | Multi-app rule |
|---|---|
| TB11, TB14, TB18, TB20 | No Octopus key or runbook-run call in any app file; the Space Manager key lives only in platform project variables |
| TB13a, TB19 | No platform account or Azure start right in app projects |
| TB09, C02, C03, C07, C08 | From the descriptor: annotations only on tenant-rendered Applications; images under `apps/<app>/`; pin field per packaging |
| TB17, C12, C22, C23 | Platform projects only. Wake-first: an app process using an in-cluster pool or an Argo CD step first deploys `platform-wake` |
| New cross-app lint | App files reference only their own registry path, namespaces, vaults and `rg-app-<app>` |
| New C24 | Descriptors (`check`) |
| C13, C14 app variables, C16 step rules, C17 | Retired: they check app #1's shape |

- App repos get the same rules through `platform/handshake-lint` (codefresh-engineer §6).
- The contracts keep platform names and the handshake. Their app sections (`images`, `deploymentProcess`, `workorders` variables, pins) move to `apps/workorders.yaml` and app #1's files.

## 4. Rename map

Each row changes the contracts, the §3 checks reading it and every doc naming it; the name lint catches leftovers.

| Today | Becomes |
|---|---|
| `workorders-infrastructure` (project, lifecycle, OCL path) | `platform-infrastructure`; new `platform-wake` |
| Project group `Work Orders` | One per app, `app-<app>` (directive §10; octopus-architect confirms) |
| `workorders-standard`, `-hotfix` | Starter lifecycles `platform-standard`, `platform-hotfix` |
| `infra-nonprod`, `infra-prod` | `infra` |
| Sets `WorkOrders Environment`, `WorkOrders Infrastructure`, `WorkOrders Platform Automation` | Per-app project variables; `Platform Infrastructure`; removed |
| Contexts `workorders-octopus`, `workorders-ci`, `workorders-release` | Removed; `app-workorders-ci`; `app-workorders-registry` |
| `aks-workorders-*`, `rg-workorders-*`, `<kv-workorders-*>`, `apr-sleep-{nonprod,prod}` | `aks-platform-training`; `rg-platform-*`, `rg-app-<app>`; `kv-<app>-<t\|u\|p>-<hash4>`; `apr-sleep-training` |
| `id-env-lifecycle-*`, `id-octopus-deploy-<env>`, `azure-oidc-deploy-<env>` | `id-platform-lifecycle`; `id-<app>-<env>-deploy`; `azure-<app>-<env>` |
| `workorders/<image>`, `argocd-{nonprod,prod}`, AppProjects `workorders-*` | `apps/workorders/<image>`; `argocd-training`; `app-workorders`, `app-workorders-previews` |
| `workorders/` under `gitops/`, `codefresh/`, `.octopus/`, `containers/` | `…/apps/workorders/` |
| `db-restore-pitr`, `rotate-sql-passwords`, step `wake-environment` | `db-restore`; `rotate-db-passwords`; Deploy a Release of `platform-wake` |

Stored user objects keep their names (directive §6).

## 5. Class-scale operations

- **Inventory and cohorts:** descriptors are the source of truth; `lifecycle.cohort` drives tags, optional per-cohort Octopus groups and `retire --cohort`.
- **Frozen** (at course end): triggers off, Octopus projects disabled, replicas 0 through the tenant chart, data kept.
- **Archived:** final database backup and app-repo tag, then delete in order: tenant (`Delete=confirm`) → retained PVs (`platform-retain` otherwise bills forever) → Octopus and Codefresh projects → ACR repositories (lift tag locks) → vaults (soft delete; `hash4` permits reuse) → identities → `rg-app-<app>`. `check` warns 14 days before `expires` and fails 30 days after.
- **Orphans:** a platform runbook reports disks, repositories, vaults and namespaces that no descriptor declares.
- **Cost per app:**
  - `terraform/apps` tags per-app Azure resources `platform-app`, `platform-cohort` and `platform-env`.
  - PVC disks carry the CSI tag `kubernetes.io-created-for-pvc-namespace` [VERIFY].
  - Node cost is split by namespace requests. AKS cost analysis needs the Standard tier [VERIFY]; on the Free tier, OpenCost does it.
  - Codefresh and Octopus usage come from their consoles.
- **Capacity:** `check` sums databases × environments against the pool and 65-vCPU quota.
- **Peaks:** Codefresh concurrency, the Octopus task cap (design Q29) and cold wakes all queue work. Stagger cohort labs.

## 6. Risks and decisions for the chief architect

1. **Slug:** `^[a-z][a-z0-9]{2,11}$` satisfies gitops (no dash) and sre-security (at most 12 characters, for vault names). Repo names stay date-based.
2. **Settle before the schema freezes:**
   - the preview path, `apps-previews/<app>/` (sre-security) or `apps/<app>/preview/` (codefresh-engineer);
   - the ESO identity (gitops risk 2);
   - OCI charts against Octopus pins (gitops risk 3);
   - per-app groups (directive §10) or `Apps` (§6).
3. **Two-step onboarding:** workload-identity client IDs exist only after the `terraform/apps` apply. A second pull request is needed unless the tenant chart can look them up.
4. **Student gitops changes:** instructors carry them (recommended), or students get write, which exposes the whole private repo; then `env-checks` must hold no secret, because student branches run it.
5. **Pipeline location:** app repo for student apps, platform repo for app #1 (ADR-D18), so the worked example differs from the student path; labs show both.
6. **State:** one `apps.tfstate` for dozens of apps couples plans and failures; per-app state keys are safer.
7. **Retirement leaks:** retained PVs, tag locks and soft-deleted vaults; the retire steps and orphan report cover them.
8. **Effort:** labs 18–23 rewritten, lab 24 added [UNVERIFIED].
