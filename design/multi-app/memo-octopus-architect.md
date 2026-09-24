# Multi-app Octopus: memo from the octopus-architect

Scope: `.octopus/**`, `octopus/terraform/**`, `octopus/preview/**`; directive §1–§12 and the sibling memos. "Checked" = Octopus docs or pricing pages read 2026-09-24.

## 0. Mandatory guarantees, Octopus side

1. **Wake first.** Every app process that touches a cluster first deploys `platform-wake` with a Deploy a Release step (condition Always). Self-enforcing: without it, the Argo CD step fails against an offline gateway.
2. **Keyless apps.** No platform secret or platform setting reaches an app project or any library variable set.
3. **Pins.** Pins change only through the Argo CD steps, in Applications annotated with the project's own slug.
4. **Azure.** Apps use only their own OIDC accounts.

The rest is app-owned ("scaffold, then own").

## 1. Space model

| Object | Model |
|---|---|
| Space | One (ADR-IR32); see §8, decision 1 |
| Environments | `tdd` and `uat` run on nonprod, `prod` on prod. `infra-nonprod` and `infra-prod` serve platform runbooks only |
| Lifecycles | Shared `platform-standard` (tdd → uat → prod), `platform-hotfix` (uat → prod), `platform-wake` (tdd, uat, prod, any order) and `platform-infrastructure`; a descriptor may request `app-<app>-<name>` |
| Groups | `app-<app>` per app; `Platform` holds `platform-infrastructure` and `platform-wake` |
| App projects | 1..n per app (`<app>`, `<app>-<part>`), fully independent. Terraform (`for_each` over descriptors) creates only the shell: slug, group, lifecycle, Git settings, `release-manifest` trigger |
| Feeds | `acr-apps`, `docker-hub`; the built-in project feed for Deploy a Release |
| Pools | `hosted-ubuntu`, optional `k8s-<app>-<env>` (§3); shared `k8s-<env>` retired |
| Step templates | Optional (`verify-release`, `require-awake-cluster`), version-pinned in app OCL. Platform projects never use templates (§4) |

**Config-as-code location:**
- Default: the app repo, base path `.octopus/<project>/`.
- The platform repo's `.octopus/apps/<app>/<project>/` serves app #1 (ADR-D18) and repos that must stay pristine.

**Git access:**
- Config-as-code uses the Octopus GitHub App connection instead of the org-wide PAT: one per GitHub account per space, with a repository list that onboarding extends (a repository admin adds each repo).
- UI commits act as the signed-in GitHub user, so GitHub permissions and branch rules apply (checked).
- The Argo CD step picks its Git credential by repository restriction (2025.4+, checked). The platform-repo credential stays restricted to `<ENV_REPO_URL>` (R3); app repos get no Octopus write credential. Scripts cannot read Git credentials [VERIFY].

## 2. Keyless everything

**Wake.** `platform-wake` becomes keyless; today's REST-and-key step is interim.
- Its one step runs as `azure-platform-wake-<tier>` over OIDC, with subject `space:<space-slug>:project:platform-wake:environment:<env>`. `id-platform-wake-<tier>` may start, read and toggle the sleep rule, never stop.
- It lifts `apr-sleep-<tier>`, then waits for a new endpoint, `https://wake.<apps-domain-<tier>>/ready` (gitops-architect), which returns 200 once Argo CD, the gateway and ingress are Ready.
- Afterwards only platform runbooks hold the Space Manager key.
- Hardening stays: no Octopus variable expressions in the script; literal account, pool and image. Passed variables "will override existing variables in the child project" (checked).
- Checked: the child deploys to the parent's environment, with the release picked at parent release creation (latest by creation time) and allowed there by its lifecycle. Cancelling the parent leaves the child running.
- [VERIFY] (`verification.md`):
  - the child's identity; deploying teams hold Deployment Creator on `platform-wake` regardless;
  - runbook support; until confirmed, app runbooks start with `require-awake-cluster`, which waits for the ready endpoint and asks the operator to deploy `platform-wake`;
  - task-cap starvation (§6).

**Releases:**
- **Feed.** `acr-apps`: type Azure Container Registry, OIDC subject `space:<space-slug>:feed:acr-apps`, pull-only on the build registry.
- **Trigger.** One external feed trigger `release-manifest` per project. Its only source is package `manifest` (`apps/<app>/manifests/<project>`) of step `verify-release`, on channel `Default` with rule `^$`. That package is also the version donor, so the release number equals `VERSION`.
- **Multi-image.** Artifacts go first; the trigger takes each image's latest version, and `verify-release` fails on a mismatch. Admission stays the signature control, since students can delete that step.
- **Checked:** triggers watch referenced images and charts, read config-as-code from the default branch, run every three minutes, never reuse a package version, need SemVer, and should watch the package pushed last.

**Pins.** Only the tenant chart sets `argo.octopus.com/project[.<source>]` and `argo.octopus.com/environment[.<source>]`, so a deployment updates only Applications carrying its own project slug. Digest pins are [VERIFY].

**App Azure services:**
- Account `azure-<app>-<env>` is restricted to `<env>`, with subject keys space, project and environment. That gives one federated credential per project; runbook runs share the subject (checked).
- `id-<app>-<env>-deploy` is Contributor on `rg-app-<app>-<tier>` only.
- Any project may reference any account; Entra rejects foreign subjects.

## 3. Migrations for in-pod databases

| Option | Isolation | Cost | Verdict |
|---|---|---|---|
| A. Shared `k8s-<env>` pool | None: a shared namespace, a shared work volume, and reach to every database | Lowest | Retire |
| B. Per-app `k8s-<app>-<env>` pools, NetworkPolicy to the app only | By convention only: pools serve every project in a space (checked) | ≈0.3 GiB and a registration per app-environment | Opt-in, for in-cluster steps such as app #1's TDD suite |
| C. The migrator as an Argo CD PreSync hook Job in the app namespace, pinned with the app images | Native: the app's own Secret and NetworkPolicy | None | **Default** |

Option C is engine-neutral.
- It turns ADR-C2's "migrate, then commit" into "commit, then Argo CD migrates before rollout".
- A failed Job fails the sync, so the Healthy verification fails the deployment while old pods keep serving; redeploying the previous release restores the pin.
- Hooks run on every sync, so migrators must be idempotent.
- sre-security can then drop the worker's database-port rule.

## 4. Teams and permissions (built-in roles only)

**Finding (checked).**
- Every built-in role that edits a project (Project Contributor, Project Lead, Project Deployer, Runbook Producer) also grants ProjectEdit, ActionTemplateEdit and LibraryVariableSetEdit.
- Scoping may not confine the last two [VERIFY]; the design assumes it does not.
- Rules that follow:
  - Platform values never go in library sets: ProjectEdit can include any set, and a script can then print its values.
  - Platform projects keep settings in Git-backed project variables and the key as a sensitive project variable, with inline steps only.
  - Terraform reverts drift in app-project shells.

**Teams:**
- `app-<app>` (the descriptor's Entra group):
  - Project Deployer on `app-<app>` in tdd and uat;
  - Project Viewer in prod;
  - Deployment Creator on `platform-wake` in tdd and uat.
- `instructors-<cohort>`:
  - Project Deployer and Release Creator on the cohort's groups in every app environment;
  - Deployment Creator on `platform-wake`.
- `Platform Engineers`: Space Manager.
- `SRE On-call`:
  - Runbook Consumer on `platform-infrastructure` in the infra environments;
  - Project Viewer everywhere.

**Students edit** their own processes, runbooks, channels, variables (sensitive ones write-only) and triggers.

**Students cannot** edit environments, lifecycles, accounts, feeds, pools, Git connections or teams, deploy to prod or the infra environments, or touch other apps' or platform projects.

## 5. Renames and migration of live objects

`moved` blocks plus in-place renames keep IDs and history; empty preview objects are recreated.

- **Keep** the five environments, feed `docker-hub` and the prod freeze, which gains every app project.
- **Rename** lifecycles `workorders-standard`, `-hotfix`, `-infrastructure` → `platform-standard`, `platform-hotfix`, `platform-infrastructure`, and group `Work Orders` → `app-workorders`.
- **Project `workorders`:** keep as app #1, with base path `.octopus/apps/workorders/workorders/`.
- **Project `workorders-infrastructure`:** rename name and slug to `platform-infrastructure`, in group `Platform`. Foundation updates its federated credentials first.
- **Sets `WorkOrders Environment` and `WorkOrders Infrastructure`:** move the values to OCL project variables (app #1 and `platform-infrastructure`), then delete the sets.
- **Teams:** keep `Platform Engineers` and `SRE On-call`, re-scoped; `Developers` → `app-workorders`; `Release Managers` and both approver teams → `instructors-<cohort>`; delete `CI Release Publishers`.
- **Objects not yet applied:**
  - keep `platform-wake`, group `Platform`, the machine policy and the env-sleep triggers; `platform-wake` becomes keyless;
  - drop `WorkOrders Platform Automation`: the key becomes a sensitive `platform-infrastructure` variable [VERIFY for config-as-code];
  - `acr-workorders` → `acr-apps`;
  - `azure-oidc-deploy-<env>` → `azure-<app>-<env>`.
- **`env-wake`, `env-sleep`:** move with the folder. `env-sleep` already counts every project.

## 6. Licensing and capacity

- **Pricing (checked):** per project (Free: 10; Professional: $104 a year each, "best for under 100 projects"; Enterprise: $156), plus a Cloud platform fee set by the task cap. Kubernetes clusters are not machines; workers [VERIFY]. Free and Starter exclude Platform Hub.
- **Projects.** 36 apps × 1.5 + 2 ≈ 56 projects: about $5,800 a year on Professional, plus the fee [VERIFY our tier; whether disabled projects count].
- **Task cap** binds first. Professional defaults to 5 (maximum 20), Enterprise to 20 (checked).
  - Each waking deployment holds two slots, parent and child.
  - If every slot holds a parent waiting for a queued child, deployments starve [VERIFY].
  - Fix: a cap of at least twice the peak concurrent deployments, plus staggered labs (Q29).

## 7. Naming contracts

- `<app>`: `^[a-z][a-z0-9]{2,11}$`, with the pragmatist memo's reserved words.
- Projects: `<app>`, `<app>-<part>`, `platform-infrastructure`, `platform-wake`. Groups: `app-<app>`, `Platform`.
- Config-as-code paths: `.octopus/<project>/` in the app repo; `.octopus/apps/<app>/<project>/` and `.octopus/platform-*/` in the platform repo.
- Steps: `wake-environment` (Deploy a Release of `platform-wake`), `verify-release` (package `manifest`), `require-awake-cluster`.
- Trigger and feed: `release-manifest`; `acr-apps`.
- Accounts: `azure-<app>-<env>`, `azure-platform-lifecycle-<tier>` and `azure-platform-wake-<tier>`, all with subject keys space, project and environment.
- Pools and machine policy: `hosted-ubuntu`, `k8s-<app>-<env>`; `Sleep-tolerant Kubernetes workers`.
- Sleep and wake: `env-wake`, `env-sleep`, `env-sleep-hourly-{nonprod,prod}`, tag `cluster-power/#{Octopus.Environment.Id}`.
- Argo CD: instances `argocd-nonprod` and `argocd-prod`, with the annotations in §2.

## 8. Risks and decisions for the chief architect

1. **One space or two.** Pools are space-wide and dynamic-worker leases non-exclusive (checked): a student step can share a VM with a key-holding platform runbook.
   - One space (recommended): keyless wake, the key only in platform runbooks, rotation (R23), and lint on app OCL that names platform pools.
   - Two spaces: needs System Manager (an ADR-IR32 change); `platform-wake` stays beside the apps.
2. **Role breadth** (§4): accept the rules, or ask the user for one custom role (ADR-IR32 change).
3. **Migrations:** option C or ADR-C2 ordering.
4. **Policies.** Platform Hub policies (Rego) see the steps and their order, but not pools (checked). They need system-team rights; the wake guarantee already enforces itself.
5. **Task cap:** buy 10 or 20 before classes.
6. **Deploy a Release** unknowns (`verification.md`).
7. **Git:** the GitHub App connection; retire the org PAT from config-as-code.
8. **Slug squatting.** A freed slug inherits pins and federated subjects. Retirement deletes the Applications and federated credentials before the project.