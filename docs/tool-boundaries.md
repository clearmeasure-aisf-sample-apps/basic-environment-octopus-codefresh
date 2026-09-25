# Tool boundaries

Every tool of the platform can deploy something. Three deployers is the biggest source of confusion for the people who run it (R1-P §6 D1), so each tool gets one verb and the overlapping features of the others stay off (ADR-D2). The rules hold for every app, whatever its pipelines look like after "scaffold, then own" (ADR-IR34). `scripts/checks/tool-boundaries.sh` enforces them on every push through `platform-env/env-checks`; CAP-KIT-006 runs their C# port from the offline suite (`tests/Platform.Conformance.Offline/Kit/Boundaries`, one test per rule), which also holds TB23. The rule IDs below refer to both.

## One verb per tool

| Tool | Verb | Decides | Never |
|---|---|---|---|
| **Codefresh** | builds | Whether a commit of an app is releasable: the app's own gates, its version, signed and locked images under `apps/<app>/`, build information, and the Octopus release (key in context `platform-octopus`, ADR-IR32, decision 4) | Deploys, approves, syncs Argo CD, touches an app cluster, commits to any repo, holds a cloud identity |
| **Octopus Deploy** | releases, promotes, approves, runs runbooks | When a release may enter an environment (lifecycles, channels, freezes), who approved it, which tags each environment runs (the pin commit), whether the deployment verified; day-2, tier and app-layer runbooks | Applies Kubernetes objects, runs Helm or kubectl against app namespaces, creates releases from feed triggers |
| **Argo CD** | applies | How fast `main` becomes cluster state and that the cluster stays that way (prune, self-heal); every tenant, from `apps/*.yaml` through the ApplicationSet `apps`; migrations as PreSync Jobs | Chooses versions (no Image Updater), holds a calendar (no sync windows), rolls back |
| **GitHub** | enforces merge rules | Which change may merge: each app repository requires `codefresh/ci` and one review; `main` here requires `codefresh/env-checks` and CODEOWNERS review | Runs platform workflows: GitHub Actions stays off in app repositories |

![View: one verb per tool](../design/diagrams/view-responsibility.png)

*The owners of design §4's responsibility matrix. Red dashed lines are the boundary rules below (TB01, TB02, TB04, TB07).*

Two rules follow:
- **Git is the gate between Octopus and Argo CD.** Octopus writes only the pin fields of `gitops/apps/<app>/envs/<env>/<deployable>/`; Argo CD applies whatever `main` holds. Neither calls the other to change state; the gateway only reports health.
- **One writer per fact.** The version comes from the app's Codefresh release pipeline, the release record from Octopus, the running tags from the pin files, and everything else in this repository from reviewed pull requests.

## Where the lanes meet

| Handoff | From → to | Contract (design §7.0) | Checked by |
|---|---|---|---|
| Image publish | Codefresh → registry | M1 images at `apps/<app>/<image>` through the shared token `cf-apps-release`; M2 keyless signature and SBOM; tags locked after signing | `consistency.sh` C25 (M1); CAP-CF-006, CAP-CF-007 |
| Release creation | Codefresh → Octopus | M3 `octopus_release` with explicit `PACKAGES`, no `PACKAGE_VERSION`; key `OCTOPUS_API_KEY` from `platform-octopus`, attached to release pipelines only | `consistency.sh` C25; `tool-boundaries.sh` TB14; CAP-CF-008 |
| Pin commit | Octopus → this repository | The image-tag step (fallback step template `platform-pin-writer`): direct commit to `main` of `newTag`, Helm image values at `argo.octopus.com/image-replace-paths`, or raw `image:` fields | `Platform.Onboarding check` (pin shape); `tool-boundaries.sh --audit-bot-commits`; CAP-OCT-012 |
| Reconciliation | This repository → Argo CD | Application `tenant-<app>` renders `<app>-<deployable>-<env>` and `<app>-db-<env>` with automated prune and self-heal | `consistency.sh` C03, C04, C09 |
| Health report | Argo CD → Octopus | Gateway with the read-only account `octopus` (`applications, get, app-*/*`); annotations rendered only by the tenant chart | `consistency.sh` C02, C06; `tool-boundaries.sh` TB09 |
| Early wake | Codefresh → Octopus | Step `wake_nonprod` of a release pipeline asks for `env-wake` in `infra-nonprod` and never fails the build | `tool-boundaries.sh` TB18; CAP-CF-009 |
| Wake first | Octopus → Azure | Step 0 of every app process that touches a cluster deploys a release of `platform-wake`, whose one step runs `env-wake`; in-cluster app runbooks wait with `Wake.WaitMinutes`; only `env-wake` and `env-sleep` change power state (ADR-IR33) | `consistency.sh` C23; `tool-boundaries.sh` TB17 to TB20 |

## Trust boundary TB2: the build cluster

Codefresh runs every build on one runtime, `<cf-runtime>` = `aks-platform-build/codefresh` (decision 16). The build cluster holds no app workload and no data, its identities hold no role outside `rg-platform-build-aks-nodes`, and pipelines get no cloud identity: registry pushes use the tokens of Codefresh registry integrations. The conformance pipelines, with context `platform-conformance`, are the one recorded exception (single operator, §13).

TB21 checks:
- every spec names `runtimeEnvironment` `<cf-runtime>`;
- `terraform/build` declares no role assignment and no federated credential;
- the runner values, app pipelines and starters carry no Azure login, client secret or workload identity.

## Name lint

The platform is app-neutral (ADR-IR34): no platform file names an app. TB22 reads the app names from `apps/*.yaml` and searches every platform path (`argocd`, `gitops`, `.octopus`, `octopus`, `codefresh`, `containers`, `policies`, `terraform`, `scripts`, `tools`, `fixtures`, `CODEOWNERS`, `.gitleaks.toml`, `.yamllint.yaml`).

Allowed:
- the app's own paths: `apps/<app>.yaml`, `codefresh/apps/<app>/`, `.octopus/apps/<app>/`, `gitops/apps/<app>/`, `containers/apps/<app>/`;
- the conformance fixture `sandbox`, a platform component;
- Markdown, the design, contracts, tests and the catalogue, and test fixtures in a `tests/` folder of a platform root (Kyverno CLI, `terraform test`), which are never deployed;
- Terraform `moved` blocks and lines marked `name-lint: allow` (migration records);
- a labelled example ("for example", "e.g.", "such as").

`Platform.Onboarding check` applies the mirror rule to app files: an app's files never name another app's registry path, namespaces, stores, vaults or resource groups.

## Consoles by role

Cognitive load is counted in consoles and credentials. Each role gets the fewest that let it do its job. Octopus teams scope by environment only, so a new app needs no team change; under the single-operator model (§13) the user holds every role.

| Role | Uses | Access | Never needs |
|---|---|---|---|
| Developer or student | IDE, the app repository, the Codefresh build log, Octopus (read) | Octopus team `Developers` | Argo CD, writes to this repository, the Azure portal, kubectl |
| Release manager | Octopus | Team `Release Managers`: deploys to every environment, creates `Hotfix` releases, overrides `prod-weekend-freeze` with a reason | Codefresh, Argo CD, kubectl |
| UAT or prod approver | Octopus manual interventions | Teams `UAT Approvers`, `Prod Approvers`; `Platform.SoDMode` decides whether the creator may approve | Everything else |
| On-call | Octopus first (what changed, who approved, app runbooks, redeploy previous, `env-wake`, `env-sleep` with `Sleep.Force`); Argo CD read-only; Azure Monitor | Team `SRE On-call`; Argo CD through `platform-operators`, its only group (admin; used for reading) | Codefresh; kubectl writes; Azure rights to start or stop a cluster |
| Platform engineer | All four tools, Azure, pull requests to this repository | Team `Platform Engineers` (Space Manager); group `platform-operators`; `@<org>/platform-owners` | kubectl writes to app namespaces: changes go through Git |
| Security owner | Entra, Key Vault, policy and grant pull requests, descriptors with Azure access | `@<org>/security-owners` | Deploy rights |
| Automation | Release handoff, interventions in test mode, conformance | `AISF-Service-Account` in `CI Release Publishers`, `UAT Approvers` and `Prod Approvers`; answers only with a `conformance:<run-id>` or `e2e:<run-id>` reason while `Platform.InterventionTestMode` is `true` | Console sign-in |

## Features that stay off, and why

| # | Feature | Tool | Why it stays off | Rule |
|---|---|---|---|---|
| 1 | `deploy`, `helm`, `launch-composition` and `approval` steps | Codefresh | A second deployer and a second audit trail; approvals live in Octopus (ADR-D13) | TB01 |
| 2 | `argocd`, `kubectl`, `helm install/upgrade`, `az aks get-credentials` in pipelines | Codefresh | CI holds no cluster credential (M4) | TB02 |
| 3 | GitOps Runtime and Promotions | Codefresh | A second Argo CD console; Promotions are disabled in runtimes after 0.24.0 (E34) | TB03 |
| 4 | Image Updater | Argo CD | A second tag writer would race Octopus and skip approvals | TB04 |
| 5 | Sync windows | Argo CD | The calendar is `prod-weekend-freeze` in Octopus (ADR-D5) | TB05 |
| 6 | `latest` tags in desired state, OCL or pipelines | All | Desired state names exact versions; pins are tags `<VERSION>` or `sha-<sha7>` | TB06 |
| 7 | Kubernetes YAML, Helm, Kustomize or kubectl steps against app namespaces | Octopus | Self-heal would revert them, and Git would stop being the truth | TB07 |
| 8 | Role assignments, locks or policy assignments in `terraform/tier` or `terraform/apps/tier` | Terraform | Every grant is the provisioner's (decision 3); locks come from the Owner script | TB08 |
| 9 | Octopus annotations outside the tenant chart; tenant annotations | Argo CD | The gateway would map add-ons or databases into deployments; there are no tenants (ADR-C8) | TB09 |
| 10 | Trigger sync in the Argo CD step | Octopus | Keeps the gateway account read-only (E7) | TB10 |
| 11 | Feed or built-in release triggers | Octopus | A second release creator; duplicate numbers caused incidents EP20 and EP21. The keyless handoff stays an optional pattern (decision 4) | TB11 |
| 12 | Kubernetes deployment targets on app clusters | Octopus | Shared `k8s-<env>` workers run in-cluster steps; targets would invite direct deploys | TB12 |
| 13 | The stored provisioner account, stored variable sets and stored contexts in any project or pipeline | Octopus, Codefresh | Subscription-wide or org-wide secrets must not reach branch-controlled YAML (ADR-C10, R5) | TB13a, TB13b, TB13c |
| 14 | Octopus API keys other than `OCTOPUS_API_KEY` from `platform-octopus` in release and conformance pipelines | Codefresh | One key, one context, one rotation (ADR-IR32) | TB14 |
| 15 | `mutateDigest` in image verification | Kyverno | Live state would differ from Git and self-heal would fight it (ADR-D11) | TB15 |
| 16 | Commits and pushes | Codefresh | Only Octopus (pins) and people (pull requests) write here; the conformance pipelines push only to `<sandbox-app-repo>` | TB16 |
| 17 | `az aks start`, `az aks stop` or alert-rule toggles outside `env-wake` and `env-sleep` | All | One place decides whether a cluster runs, paired with the alert suppression | TB17 |
| 18 | REST calls that run runbooks outside the platform places | Octopus, Codefresh | They bypass approvals and the audit trail; apps wake keylessly through `platform-wake` | TB18 |
| 19 | Azure rights that can start a cluster in app projects, starters or app grants | Octopus, Terraform | Apps ask `platform-wake`; they never hold the right | TB19 |
| 20 | `Platform.OctopusApiKey` outside `platform-infrastructure`, `PlatformWake.*` outside `platform-wake`, any literal key | Octopus | The Space Manager key controls the space; passed variables override a child's (decision 17) | TB20 |
| 21 | A second runtime, grants on the build cluster, cloud identities for pipelines | Codefresh, Terraform | Trust boundary TB2 | TB21 |
| 22 | App names in platform files | All | The platform is app-neutral; onboarding touches only app-scoped paths | TB22 |
| 23 | Shell scripts (`*.sh`) and Bash script steps (`Octopus.Action.Script.Syntax = "Bash"` in `.octopus/`, `octopus/templates/`) | All | One script language beside .NET: PowerShell 7. Permanent exceptions: `containers/apps/*/*/migrate.sh` (entrypoints of .NET runtime images, which ship no pwsh) and `terraform/tier/scripts/aks-token.sh` (exec credential plugin, started per client, so start-up time matters). Every other script and OCL file is listed as pending conversion (`ScriptLanguageRule.cs`) and leaves the list in the change that converts it | TB23 |
| 24 | Bot commits that change more than pin fields | Octopus machine user | The machine user bypasses review, so every push to `main` audits its commits | AUDIT |
| 25 | Fork events in any trigger | Codefresh | A fork's pull request would run with the platform's contexts | `consistency.sh` C25; CAP-CF-005 |
| 26 | Schedules that wake, apply or destroy | Octopus | Clusters stay asleep until the first job (ADR-IR33); schedules run only `env-sleep` | `consistency.sh` C23 |

## What the checks cannot see

- **Console actions.** A Sync click in Argo CD or a variable edit in the Octopus UI leaves no file behind. RBAC, config as code on protected `main`, Octopus Git drift detection and Argo CD self-heal cover them.
- **Values in the Octopus database.** Sensitive variables and some settings are not in Git (E26); `octopus/terraform` manages the non-sensitive ones.
- **Runtime identity misuse.** Federated subjects are exact (§7.0), each app's ClusterSecretStores admit only its namespaces, and the prod signer policy admits only images signed by the app's own release pipeline. The live suite proves these (CAP-GIT-004, CAP-AZ-001, CAP-AZ-002, CAP-AZ-017).

## Changing a lane

A lane change is a design change. One pull request updates the ADR in `design/platform-design.md`, the names in `contracts/platform-contracts.yaml` and the rule in `scripts/checks/tool-boundaries.sh` and its C# port (`ToolBoundaryRules.cs`); platform owners review it, and security owners too when a credential, a grant or a policy moves.
