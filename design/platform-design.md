# Work Orders Delivery Platform: Adjudicated Design

| Field | Value |
|---|---|
| Role | `chief-architect` (adjudicator) |
| Date | 2026-09-24 (evidence checked 2026-09-23; integration review 2026-09-24, §2.5) |
| Status | Design and implementation sketch after the integration review. Nothing is provisioned; every environment-specific value is a placeholder. |
| Inputs | Shared brief; the ten debate papers in `design/debate/`; the app repo (origin at `46104c1`, the copy at `24da122`, same files for every fact in §2.4); the five implementation reports; official documentation (Evidence register, §2.3). |
| App repo | `clearmeasure-aisf-sample-apps/20260923-001` (public; a fork of `ClearMeasureLabs/bootcamp-palermo-workorders`, copied at `24da122`; default branch `master`). It holds no platform file, and GitHub Actions is disabled there. The origin keeps the live legacy delivery (`.github/**`, `.octopus/**`, root `Dockerfile`), untouched. |
| Environment repo | `clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh` (user directive), private, default branch `main`. It holds every platform file, including the Codefresh pipelines and the Worker and migrator Dockerfiles (ADR-D18). |
| Path conventions | Paths without a prefix are environment-repo paths. `app:` marks app-repo paths. |
| Citation conventions | `R1-OA` = `design/debate/round-1-octopus-architect.md`; `GA` gitops-architect, `CE` codefresh-engineer, `SRE` sre-security, `P` pragmatist; `R2-*` = round 2. `E<n>` = Evidence register entry. `F<n>` = repo fact (§2.4). `[UNVERIFIED]` = not confirmed in official docs. `[VERIFY]` = must be proven in the phase-2 TDD spike before it is relied on. |

## 1. Executive summary

The platform delivers the work-order app with one verb per tool: Codefresh builds and gates merges, Octopus Deploy releases, Argo CD reconciles. The new path targets Azure Kubernetes Service (AKS): one nonprod cluster (TDD, UAT) and one prod cluster, each running upstream Argo CD 3.5. The legacy GitHub Actions → Octopus → Container Apps path runs untouched until cutover criteria pass.

On each master merge, Codefresh mints `2.5.<first-parent height>` and runs the same `build.ps1` gates. It then publishes signed `workorders/*` images to ACR and two NuGet packages to Octopus, and creates the Octopus release over OIDC. Octopus deploys TDD automatically. For every environment it reads secrets from that environment's Key Vault, runs DbUp on an in-cluster Kubernetes worker, and commits two image tags to a Kustomize overlay. It then waits for Argo CD to report Synced and Healthy at that commit and verifies version and health. In TDD only, it runs Playwright, because the suite wipes its target database. UAT and Prod are manual promotions with sign-off, go/no-go, separation of duties, a pre-migration database copy and freezes.

The private environment repo holds every platform file; the app repo holds none. Octopus is the only writer of `images[].newTag`; people change everything else by pull request. Azure infrastructure splits into two layers: a foundation applied by an Owner, which holds every role assignment, and an environment layer that only Octopus runbooks apply.

Key recommendations to the user:
- Back the stored GitHub credential with a machine user or GitHub App.
- Retire the stored Azure provisioner for OIDC identities.
- Require `codefresh/ci` on the app repo; keep GitHub Actions off there.
- Confirm the Octopus license tier.
- Approve the app work items that gate Prod cutover.

Deferred: PR previews, blue-green Rollouts, Platform Hub. Cut: tenants.

### The platform in one paragraph

Codefresh builds and posts the required merge check; Octopus Deploy releases; Argo CD reconciles; GitHub enforces the merge rule. A master merge makes Codefresh mint `2.5.<first-parent height>`, run the `build.ps1` gates, push signed `workorders/ui-server`, `workorders/worker` and `workorders/db-migrator` images to ACR, push the `ChurchBulletin.Database` and `ChurchBulletin.AcceptanceTests` packages to Octopus, and create the release over OIDC. Octopus deploys TDD automatically and UAT and Prod on approval. For each environment it reads secrets from that environment's Key Vault, runs DbUp on that environment's in-cluster Kubernetes worker, commits two `newTag` values to `gitops/workorders/envs/<env>/kustomization.yaml`, waits until Argo CD reports Synced and Healthy at that commit, then verifies version and health; in TDD only it also runs Playwright. Argo CD 3.5 on each AKS cluster auto-syncs, prunes and self-heals from the environment repo. Pods reach SQL and Key Vault through workload identity, and Kyverno admits only images signed by the Codefresh release pipeline. An Owner applies the Azure foundation, which holds every role assignment; Octopus runbooks apply the environment layer. Every platform file lives in the private environment repo; the app repo `20260923-001` only receives settings. The legacy GitHub Actions → Octopus → Container Apps path of the origin repo runs untouched until Prod cutover.

## 2. Decision log

Status values: **Decided** (binding on implementers), **Recommended to user** (needs the user's approval or action), **Deferred** (design fixed or sketched, not built in the current phases).

### 2.1 Contested items C1–C12

#### ADR-C1 Packaging and overlay format — Decided

- **Context.**
  - The Octopus "Update Argo CD Application Image Tags" step is the only writer of environment pins (ADR-D4).
  - What it rewrites depends on the Argo CD source type.
  - UI.Server may later become an Argo Rollout.
- **Options.**
  - (a) A Helm chart in OCI, pinned per environment, with Git values files that Octopus rewrites through `image-replace-paths`.
  - (b) A Kustomize base plus per-environment overlays; Octopus rewrites `images[].newTag`.
  - (c) Plain YAML.
- **Who argued what.**
  - **Helm.** R1-GA §6 ("Packaging" row) and §8. R2-OA §1 conceded to Helm, and R2-OA §3 C1 keeps it.
  - **Kustomize.** R1-OA §6 D3 and R1-P §6 D8. In round 2: R2-GA §1 and §3 C1, R2-CE §1 and §3 C1, R2-SRE §3 C1, and R2-P §3 C1.
  - **The crossover.**
    - The gitops-architect moved from Helm to Kustomize after reading the step's Helm rewrite rule.
    - The octopus-architect moved from Kustomize to Helm, citing the round-1 claim that "Kustomize needs a Rollout transformer".
    - The gitops-architect retracted that claim in R2-GA §2.
- **Decision.**
  - Use Kustomize, in three layers:
    - `gitops/workorders/base/` holds the reviewed structure.
    - `gitops/workorders/envs/<env>/kustomization.yaml` is the pin file. Octopus writes only `images[].newTag` there.
    - `gitops/workorders/envs/<env>/config/` holds the human-owned environment configuration.
  - The app has no Helm chart.
  - Rollouts, when adopted, reuse the same pin (ADR-C3, §7.6).
- **Rationale.**
  1. The write surface is smaller.
     - For Kustomize, Octopus "will only update the `newTag` field(s) found in the Kustomize file. No other files will be edited."
     - For Helm, the step updates "every Helm values file referenced by the Application — both the chart's default `values.yaml` and any files listed in the Application's `spec.source.helm.valueFiles`", and it never writes `valuesObject` (E1).
  2. The Helm-OCI path is undocumented.
     - Octopus "cannot update charts sourced from a Helm repository or OCI feed", and only Git repository sources are supported for updates (E2).
     - Every Helm-annotation example uses a Git-hosted chart (E6).
     - The step also fails if any repository referenced by the Application has no Git credential (E1). How it behaves with an OCI chart source is [UNVERIFIED].
  3. The Rollout premise is false.
     - Kustomize's built-in image field specs include `spec/template/spec/containers[]/image` and carry no kind filter (E10).
     - The Rollouts Kustomize configuration adds name references, labels and replicas, not images (E11; R2-GA §2; R2-SRE §2).
  4. There is no chart release pipeline and no chart-version pin, and `kustomize build` renders locally (R2-P §3 C1).
  5. Plain YAML is rejected because the step's YAML scan excludes CRDs (E1).
- **Consequences.**
  - The base declares no `images:` block; every overlay pins both images.
  - Previews override images with `spec.source.kustomize.images` (R2-P §2).
  - Configuration promotion needs its own rule (ADR-D6).
  - A future shared chart, for example for sample-app templates, is rendered into Kustomize by CI so the one-field pin contract holds (R2-SRE §5). Kustomize `helmCharts` is an [UNVERIFIED] alternative (R2-CE §5).
- **Dissent.**
  - The octopus-architect keeps Helm, with ref-source scoping and `image-replace-paths.chart` (R2-OA §3 C1).
  - Revisit only if a Git-hosted chart becomes necessary, and only after a spike proves that ref-source updates work.

#### ADR-C2 Database migrations — Decided (named environments); previews Deferred with ADR-C4

- **Context.**
  - DbUp (`ChurchBulletin.Database`) is forward-only.
  - The console accepts only SQL authentication or Windows integrated security, and takes the password as a positional argument (F9).
  - Argo CD alone applies desired state (ADR-D2).
- **Options.**
  - (a) An Argo CD Sync or PreSync hook Job.
  - (b) An Octopus step that runs before the pin commit, on an in-cluster Kubernetes worker.
  - (c) A Job that an Octopus step creates in the worker's own namespace.
  - (d) Migrate on app startup.
- **Who argued what.**
  - (a): R1-GA §6 and §8; withdrawn in R2-GA §1.
  - (b): R1-OA §6 D4, R1-CE §2, R1-P §6 D6, R2-CE §3 C2, R2-P §3 C2, R2-SRE §3 C2 (plus a PreSync read-only guard) and R2-GA §3 C2 (per-environment pools).
  - (c): R2-OA §5.1.
  - (d): rejected by all, because startup migrations race across replicas (R1-GA §6).
- **Decision.**
  - For `tdd`, `uat` and `prod`, the `workorders` process runs these steps in order:
    1. `read-deployment-secrets`: an Azure CLI step using the per-environment OIDC account. It reads `workorders-sql-migrator-password`.
    2. `db-copy-pre-release`: prod only.
    3. `migrate-database`: the `ChurchBulletin.Database` package at the release version runs `update`.
    4. `update-argo-cd-image-tags`.
  - All four steps run on the target environment's Kubernetes worker pool (`k8s-<env>`). `migrate-database` runs inside step container `<acr-name>.azurecr.io/platform/ci-dotnet:<ci-image-version>@sha256:<ci-image-digest>`; `read-deployment-secrets` uses the default worker-tools container.
  - A failed migration ends the deployment before any Git commit.
  - After WI-05 adds Entra authentication to DbUp:
    - The password step is deleted.
    - The worker's script-pod service account federates to `id-workorders-<env>-migrator`.
    - If the worker chart cannot label script pods for workload identity [VERIFY], the step switches to mechanism (c).
- **Rationale.**
  - Hooks "are not run" during selective sync (E12). They also re-run on every sync, and a failed hook leaves a pin in Git whose schema was never applied (R2-OA §3 C2; R2-P §2).
  - The Kubernetes worker "is limited to modifying its local namespace" (E27). So the migration has network reach to private endpoints without a write path into Argo-managed namespaces (R2-GA §3 C2).
  - The package path mirrors the proven legacy step (`app:.octopus/deployment_process.ocl`, step `run-db-migrations`).
  - Per-environment pools isolate credentials and network reach. The TDD pool also runs the destructive acceptance suite (ADR-C11).
- **Consequences.**
  - Migrations must follow expand/contract, because rollback redeploys older images over a newer schema.
  - Until WI-05, the migrator password passes through Octopus as a sensitive output variable and appears as a process argument inside the script pod.
  - The PreSync read-only schema guard is Deferred as a phase-6 option (R2-SRE §5; R2-P §5).
- **Dissent.**
  - The octopus-architect prefers the Job mechanism now (R2-OA §5.1).
  - The sre-security role wants the PreSync guard now (R2-SRE §3 C2).

#### ADR-C3 Progressive delivery for UI.Server — Decided (rolling update through cutover); blue-green Deferred; canary rejected

- **Context.**
  - UI.Server runs as one replica.
  - It holds WebSocket connections in an in-process `ConcurrentDictionary` (`RealtimeNotificationHub`; R2-GA §1).
  - It serves version-coupled Blazor WebAssembly assets.
  - No metrics pipeline exists for analysis.
- **Options.**
  - A rolling update.
  - A blue-green Rollout.
  - A canary with SLO analysis.
- **Who argued what.**
  - **Canary:** R1-SRE §1; withdrawn in R2-SRE §1.
  - **Blue-green, with different timing:**
    - R1-GA §6; at cutover per R2-GA §3 C3.
    - Phase 3 per R2-OA §3 C3.
    - Phase 5 per R2-P §3 C3.
    - After a realtime backplane and at least two replicas per R2-SRE §3 C3.
  - **Rolling update first:** R1-P §6 D14 and R2-CE §3 C3.
- **Decision.**
  - **Phases 2–5.**
    - `ui-server` and `worker` are Deployments using `RollingUpdate` with `maxUnavailable: 0` and `maxSurge: 1`.
    - Startup, liveness and readiness probes all use `/alive`.
    - Octopus verifies with "Argo CD Application is healthy" (900 s), then runs `verify-version` and `smoke-test`.
  - **Phase 6 option: prod blue-green for `ui-server`.**
    - Uses the Argo Rollouts add-on and a Rollout with `workloadRef` to the existing Deployment, so the pin contract is unchanged.
    - Sets `autoPromotionEnabled: true`.
    - Runs a Job-provider smoke analysis before promotion.
    - Runs post-promotion analysis in `dryRun` for 14 days.
    - Entry criteria: at least two replicas, and WI-09 (realtime backplane) is done.
  - The Worker is never a Rollout, because it is a competing consumer on its queue.
- **Rationale.**
  - A paused Rollout reports Suspended, never Healthy, so the Octopus verification needs auto-promotion (R1-GA §7.1).
  - The Job provider needs no metrics store (R2-GA §2).
  - A canary would split WebAssembly clients and the notification hub across versions (R2-GA §1; R2-SRE §1).
  - The real release risk is the database, not traffic (R1-P §6 D14).
- **Consequences.**
  - `gitops/workorders/components/bluegreen/` exists but is referenced by no overlay.
  - SLO burn-rate alerts serve as the post-deploy signal from phase 3 (ADR-D15).
- **Dissent.**
  - The gitops-architect wants blue-green at cutover (R2-GA §3 C3).
  - The octopus-architect wants it in phase 3 (R2-OA §5.2).

#### ADR-C4 PR preview environments — Deferred (phase 6); design Decided

- **Context.**
  - Each preview needs a namespace, images and data.
  - Previews must not touch subscription credentials.
- **Options.**
  - (a) The ApplicationSet PR generator only.
  - (b) The PR generator plus Octopus ephemeral environments.
  - (c) Not building previews.
- **Who argued what.**
  - (b): R1-OA §6 D14; withdrawn in R2-OA §1.
  - (a): R1-GA §6, R1-CE §6 D13, R2-GA §3 C4, R2-CE §3 C4, R2-SRE §3 C4 and R2-P §3 C4 (deferred to phase 5).
  - SQLite, UI only: R2-P §3 C4; the default in R2-GA §3 C4.
  - SQL Server container: R2-OA §3 C4 and R2-CE §3 C4.
- **Decision.** Phase 6, opt-in, built as follows:
  - **Generator.** ApplicationSet `workorders-previews` uses the pull-request generator with label `preview`, applied by maintainers.
  - **Scope.**
    - Same-repo branches only.
    - AppProject `workorders-previews`, which allows namespaced kinds only and has no Azure identity.
    - Namespace `workorders-pr-<number>`, with a resource quota.
    - At most three open previews, by maintainer policy.
    - Deleted when the PR closes.
  - **Images.** `workorders-previews/ui-server`, `workorders-previews/worker` and `workorders-previews/db-migrator` (ADR-IR8), tagged `pr-<number>-<head_sha>`, built by the `workorders/preview` pipeline.
  - **Data.**
    - An in-namespace SQL Server container whose password comes from an ESO `Password` generator.
    - A Sync-hook Job at wave `-1` runs `workorders/db-migrator` with `rebuild`, then `seed` (WI-07).
  - **Excluded.** No Octopus ephemeral environment and no release per PR.
- **Rationale.**
  - Octopus ephemeral environments get no lifecycles, freezes, Insights or tenants, and document no Argo CD support (E41; R2-SRE §2).
  - The Codefresh create-release step has no custom-field argument (E18), and no Codefresh Octopus step deprovisions (R2-CE §2 F2).
  - The generator deletes Applications when a PR closes.
  - A SQL Server container is chosen over SQLite for three reasons:
    - The Worker requires the SQL transport (F5).
    - A SQLite preview contains no rows at all (F4).
    - A DbUp-built database contains only the ten role-less employees inserted by `008_AddSomeEmployeeRecords.sql` (F10), so a seed step is needed in either case.
    - The SQL container also exercises the PR's migrations.
- **Consequences.**
  - Each preview costs about one SQL Server pod.
  - WI-07 (seed command) gates phase 6.
  - The PR generator has "only admins may create ApplicationSets" semantics, and the project field is never templated (E14).
- **Dissent.**
  - The pragmatist wants SQLite, UI only (R2-P §3 C4).
  - The gitops-architect wants SQLite by default, with a second label for SQL (R2-GA §3 C4).

#### ADR-C5 GitOps repository topology and location — Decided (single environment repo, per-path writers); repo private and credential narrowed (done 2026-09-24)

- **Context.**
  - The user named the repo `clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh`.
  - The stored Octopus Git credential was restricted to `https://github.com/clearmeasure-aisf-sample-apps/*`; since 2026-09-24 it is restricted to this repo (R3).
- **Options.**
  - Separate config and releases repos.
  - One environment repo.
  - The app repo.
- **Who argued what.**
  - **Two repos:** R1-GA §5; void per R2-GA §2, and kept as a fallback in R2-SRE §3 C5.
  - **One GitOps repo:** R1-OA §5, R1-CE §5, R1-SRE §5 and R1-P §5.
  - **The single directed repo:** all five papers in round 2 (§3 C5 of each).
- **Decision.**
  - The environment repo holds `argocd/`, `gitops/`, `.octopus/`, `octopus/terraform/`, `terraform/`, `policies/`, `codefresh/`, `containers/`, `scripts/`, `contracts/`, `design/` and `docs/` (ADR-D18).
  - Writers per path are listed in §6.1:
    - Octopus writes only the three pin files on `main`, plus config-as-code edits on non-`main` branches.
    - Argo CD reads with its own read-only credential.
    - Codefresh reads and posts commit statuses.
- **Rationale.**
  - The user's directive names the repo.
  - An org-wide credential makes a second repo add no credential separation (R2-GA §2; R2-CE §3 C5).
  - Push rulesets that restrict file paths exist only for private or internal repos, and bypass is granted to roles, teams or GitHub Apps (E31).
- **Consequences.**
  - The repo is private (R2, done), so the push ruleset that restricts file paths applies (§6.2), on top of CODEOWNERS, the bot-path audit in `platform-env/env-checks`, Octopus Git drift detection, and Argo CD reconciling only what Git holds.
  - Argo CD needs a read credential for the private repo (ADR-IR15).
  - The two-repo fallback (a `basic-environment-octopus-codefresh-releases` repo for the pin files) is no longer needed.
- **Dissent.**
  - None remaining. The two-repo split from R1-GA §5 is recorded as the fallback.

#### ADR-C6 CI engine and required merge check — Superseded for the app repo by ADR-IR26 (`codefresh/ci` required from day one)

- **Context.**
  - `build-result` is the single required check (`app:docs/ci-single-gate.md`). It covers eight jobs, including ARM and Windows LocalDB (F12).
  - Fork PRs exist on this public repo.
- **Options.**
  - (a) `build-result` stays required forever.
  - (b) Flip to `codefresh/ci` after evidence.
  - (c) Two required checks.
- **Who argued what.**
  - (a): R2-SRE §3 C6, on fork safety.
  - (b): R2-CE §3 C6 (decoupled from runtime cutover) and R2-P §3 C6 (a single check whose gate asserts a GitHub `platform-matrix` workflow).
  - (c): R2-OA §3 C6 and R2-GA §3 C6.
- **Decision.**
  - **Phases 1–5.**
    - GitHub Actions `build-result` remains the only required check, and `app:.github/workflows/build.yml` stays unchanged.
    - Codefresh `workorders/ci` runs every Linux gate on same-repo branch pushes and posts `codefresh/ci` as an informational status.
    - Codefresh `workorders/release`, triggered by `master`, is the build of record and the only publisher for the new platform.
    - ARM SQLite and Windows LocalDB stay on GitHub Actions.
  - **Recommended flip** (needs user approval and a `.github/**` change). `codefresh/ci` becomes the single required check only when all of these hold:
    - 50 consecutive code-changing same-repo commits show equal verdicts and equal TRX totals.
    - Codefresh p95 duration is at most 1.2× that of GitHub Actions.
    - Fork PRs build through an identity-free `workorders/ci-fork` pipeline gated by a maintainer label.
    - The ARM and Windows jobs move to a GitHub `platform-matrix` workflow, and the `gate` step of `codefresh/ci` asserts that workflow's check run for the same SHA.
- **Rationale.**
  - Fork commits never fire Codefresh push triggers (R2-SRE §3 C6).
  - ARM builds are "only available to Enterprise customers" (E22).
  - Windows builds are in incubation, on Windows Server 1709 (E23).
  - Keeping exactly one required check preserves the single-gate semantics (R2-P §4).
  - The CI flip is independent of runtime readiness (R2-CE §3 C6).
- **Consequences.**
  - Linux gates run twice during the parallel run.
  - No gate is lost (R2-CE §4).
- **Dissent.**
  - The sre-security role keeps `build-result` permanently.
  - The octopus-architect and the gitops-architect want a second required check.
- **Addendum (2026-09-24, ADR-IR26).** The application repo is now the fork `clearmeasure-aisf-sample-apps/20260923-001`, where GitHub Actions is disabled and `build-result` cannot run. For that repo `codefresh/ci` is the single required status from day one; the phase split, the flip criteria and R14 no longer apply to it. The legacy origin keeps `build-result` for the live path.

#### ADR-C7 Version authority and continuity — Decided

- **Context.**
  - Legacy versions are `2.4.<GitHub run number>`.
  - Octopus release-number collisions caused incidents EP20 and EP21 (`app:arch/DeployFailure-2026-08-21.md`).
- **Options.**
  - `2.5.<first-parent height>`.
  - `3.0.<height>`.
  - Octopus `NextPatch`.
  - A Codefresh build ID.
- **Who argued what.**
  - `2.5.<height>`: R1-CE §6 D6, R2-OA §3 C7, R2-GA §3 C7, R2-CE §3 C7 and R2-SRE §3 C7.
  - `3.0`: R1-P §6 D7; withdrawn in R2-P §1.4.
- **Decision.**
  - `codefresh/workorders/scripts/version.sh` produces the version:
    - On `master`: `MAJOR.MINOR.<git rev-list --count --first-parent HEAD>`, with `MAJOR=2` and `MINOR=5` read from `codefresh/workorders/version.env`.
    - On branches: `2.5.<n>-ci.<sha7>`. These versions are never released.
  - The image tag, the Octopus package version and the release number are the same string.
  - `IGNORE_EXISTING: true` makes release creation idempotent.
  - Hotfix-channel releases are numbered `<package-version>-hotfix.<n>` and are created over existing images.
  - Legacy keeps `2.4.<run>` in its own space until it is retired.
- **Rationale.**
  - The version is deterministic per commit and safe on reruns.
  - `2.5.x` sorts above every `2.4.x`.
  - A major bump would signal a breaking change that has not happened (R2-OA §3 C7).
- **Consequences.**
  - The clone must have full depth. The session's checkouts are shallow (F17), so `version.sh` unshallows or fails.
  - A rewritten `master` history could repeat a number. The ACR tag lock makes that push fail loudly (ADR-D11).
- **Dissent.** None.

#### ADR-C8 Octopus tenants per cohort — Decided (cut)

- **Context.** No cohort requirement exists in the brief.
- **Options.** One tenant per cohort, per-student tenants, or no tenants.
- **Who argued what.**
  - For: R1-OA §6 D7.
  - Against: R1-GA §3.2, R1-SRE §3, R1-P §3 and all round-2 papers.
- **Decision.** No tenants. The `argo.octopus.com/tenant` annotation stays available for a future cohort model.
- **Rationale.**
  - Tenants are a licensed add-on.
  - Ephemeral environments cannot use them (E41).
  - They add RBAC surface without isolation for a single customer (R2-SRE §3 C8).
- **Consequences.** Untenanted projects and untenanted OIDC subjects.
- **Dissent.** None.

#### ADR-C9 Octopus Platform Hub — Deferred

- **Context.**
  - Platform Hub policies (Rego) could enforce prod guardrails.
- **Options.**
  - Adopt Platform Hub now.
  - Defer it.
  - Cut it.
- **Who argued what.**
  - Adopt now: R1-OA §6 D13 and R1-SRE §8.
  - Defer or cut: R1-P §3, R2-OA §3 C9, R2-GA §3 C9, R2-CE §3 C9, R2-SRE §3 C9 and R2-P §3 C9.
- **Decision.**
  - Deferred.
  - `policies/octopus/prod-deployment-guardrails.rego` is kept as an inactive policy.
  - Interim controls:
    - Pull-request review of config-as-code.
    - Manual interventions.
    - The separation-of-duties step.
    - Deployment freezes.
    - Kyverno.
- **Rationale.**
  - The pricing page lists Platform Hub under Enterprise (E25).
  - Its permissions "can only be assigned to system teams" (E24).
  - The platform service account is Space Manager only.
- **Consequences.** Revisit Platform Hub when an Enterprise license and system-team rights are confirmed.
- **Dissent.** None. It was deferred, not rejected.

#### ADR-C10 Provisioning credential lifecycle — Recommended to user

- **Context.**
  - By the user's choice, the client-secret principal (Contributor at subscription scope) is stored in two places:
    - Octopus: account `Azure Runtime Provisioner` and variable set `Azure Runtime Provisioning`.
    - Codefresh: context `azure-runtime-provisioner`.
  - Contributor cannot write `Microsoft.Authorization/*` (E36).
- **Options.**
  - Bootstrap, then delete.
  - Octopus holds it long term.
  - Migrate to OIDC by a set date.
- **Who argued what.**
  - Bootstrap, then delete: R1-SRE §6 and R2-SRE §3 C10.
  - Single holder, then OIDC in phase 3: R1-OA §6 D16 and R2-OA §5.3.
  - OIDC at the end of phase 1: R2-CE §3 C10.
  - OIDC at the phase-2 exit: R2-P §3 C10.
  - Scoped to runbooks: R2-GA §3 C10.
- **Decided within the design.**
  - Only `workorders-infrastructure` runbooks in `infra-nonprod` may use `Azure Runtime Provisioner`, through the account variable `Azure.LifecycleAccount`.
  - It is never used in `workorders`, in `infra-prod`, in any Codefresh pipeline, in Argo CD or in any cluster.
  - The foundation creates `id-env-lifecycle-nonprod` and `id-env-lifecycle-prod`, with federated credentials for the Octopus issuer. `octopus/terraform` creates the matching Octopus accounts `azure-oidc-env-lifecycle-nonprod` and `azure-oidc-env-lifecycle-prod`.
- **Recommended to the user.**
  1. Restrict the account (currently unrestricted) to `infra-nonprod`.
  2. Include `Azure Runtime Provisioning` in no project; the built-in Terraform steps use the account directly.
  3. Keep the Codefresh context unattached, then delete it at the phase-2 exit.
  4. Set secret expiry to 90 days or less and alert on its sign-ins.
  5. Have an Owner place `CanNotDelete` locks on the prod and legacy resource groups.
  6. Switch `infra-nonprod` to OIDC at the phase-2 exit.
  7. Use OIDC for `infra-prod` from its first apply.
  8. Then delete the client secret from Entra and from both stores. If other sample apps need it, restrict it to those projects instead.
- **Rationale.**
  - It is a bearer secret whose reach includes data-plane escalation paths (R1-SRE §6).
  - The OIDC replacement needs only role assignments that the Owner already applies in the foundation.
- **Consequences.** Nonprod provisioning can start before OIDC is proven; prod never depends on the secret.
- **Dissent.** None on direction. Timing differs between papers, as listed above.
- **Status (2026-09-24).** Item 1 is applied: the account is restricted to `infra-nonprod` (R4). Items 2 and 3 hold (R5 verified). The account variable of `Azure Runtime Provisioning` is still unscoped; scoping it to `infra-nonprod` is recommended (R4).

#### ADR-C11 Acceptance tests against TDD and the promotion gate — Decided

- **Context.**
  - The acceptance suite wipes its target database.
  - `ServerFixture.OneTimeSetUp` calls `ZDataLoader.LoadData()`, which calls `DatabaseEmptier.DeleteAllData()`.
  - That deletes every row in every `[dbo]` table except `SchemaVersions` and `sysdiagrams`, whether or not `StartLocalServer` is set (F8; R2-OA §2).
- **Options.**
  - Octopus step on the worker.
  - Codefresh waits for the deployment, then tests.
  - Argo CD PostSync hook.
  - Rollouts analysis.
- **Who argued what.**
  - Octopus step: all five in round 2 (§3 C11 of each).
  - Tests in UAT: R1-OA §8; retracted in R2-OA §2.
- **Decision.**
  - **Where and when.**
    - Step `acceptance-tests` runs in `tdd` only.
    - It follows the healthy verification of `update-argo-cd-image-tags`, then `verify-version` and `smoke-test`.
    - It runs `ChurchBulletin.AcceptanceTests` at the release version on `k8s-tdd`, in step container `platform/ci-dotnet`.
  - **Inputs.**
    - `StartLocalServer=false`.
    - `ApplicationBaseUrl=#{App.BaseUrl}`.
    - The TDD connection string for login `workorders_acceptance`.
    - The AI settings.
  - **Results.**
    - TRX files become Octopus artifacts.
    - A failure fails the deployment, and the lifecycle then blocks UAT.
    - `report-commit-status` posts `platform/tdd` to the app commit.
    - Runbook `run-acceptance-tests` exists for on-demand runs in `tdd` only.
  - **Interlocks.**
    1. The step and the runbook are environment-scoped to `tdd`.
    2. The script exits unless `Octopus.Environment.Name` is `tdd` and `Acceptance.AllowDestructiveReset` is `True`. That variable is defined only for `tdd`.
    3. Login `workorders_acceptance`, and its password in Key Vault, exist only for TDD.
    4. TDD data is declared disposable.
    5. WI-08 adds an opt-in guard inside the suite.
- **Rationale.**
  - One orchestrator records the results on the release.
  - The private TDD endpoint is reachable from the in-cluster worker.
  - The tests use the same image and browsers as CI (R2-CE §3 C11).
- **Consequences.**
  - UAT and Prod never run the suite.
  - UAT data needs a seed or copy (Q8).
- **Dissent.** None.

#### ADR-C12 App-side prerequisites — Decided (list and gates); each work item Recommended to user

- **Context.** Several app facts make naive probes and exposure unsafe:
  - `/_healthcheck` aggregates the LLM, database and `NeedsReboot` checks (F2).
  - An anonymous `GET /_demo/setneedsreboot/true` flips `NeedsReboot` in every environment (F3).
  - The Worker has no health endpoint (F5).
  - DbUp cannot use Entra (F9).
- **Options.**
  - All app changes first.
  - Platform mitigations now, with app changes gating later phases.
- **Who argued what.**
  - R2-OA §3 C12, R2-GA §3 C12, R2-CE §3 C12, R2-SRE §3 C12 and R2-P §3 C12. The sre-security role wanted passwordless SQL and gated `/_demo` routes before any AKS environment.
- **Decision.** Platform-side mitigations apply from phase 2, with no `app:src/**` change:
  - Probes use `/alive`.
  - The Gateway redirects `/_demo/*`, `/_diagnostics/*`, `/_healthcheck/detailed` and `/mcp` in uat and prod.
  - NetworkPolicies restrict in-cluster callers.
  - Connection strings start with `Server=`.
  - The Worker image comes from a new Dockerfile.
  - `RemotableBus__ApiUrl` is set for the Worker.
  - App work items WI-01 to WI-13 (§8) each gate a named phase.
- **Rationale.**
  - Probing `/_healthcheck` would let one anonymous request, or an Azure OpenAI outage, remove every pod from service (R2-GA §2; R2-CE §2 F7).
  - TDD holds no user data, so passwordless migration can wait until before prod (R2-P §3 C12).
- **Consequences.**
  - Prod cutover is blocked until WI-01, WI-02, WI-03, WI-05 and WI-08 are merged.
- **Dissent.**
  - The sre-security role wanted these before any AKS environment. That is adopted for prod only.

### 2.2 Consensus decisions that shape implementation

#### ADR-D1 Target runtime — Decided

- **Context.**
  - Argo CD reconciles Kubernetes objects.
  - Azure Container Apps exposes no Kubernetes API.
- **Options.**
  - Stay on Container Apps.
  - AKS for the new path.
  - AKS nonprod with Container Apps prod.
- **Who argued what.**
  - AKS: R1-OA §3, R1-GA §3.1, R1-CE §3, R1-SRE §3 and R1-P §3. All agree.
- **Decision.**
  - Two AKS clusters on the Standard (not Automatic) SKU:
    - `aks-workorders-nonprod`: Free pricing tier, for `tdd` and `uat`.
    - `aks-workorders-prod`: Standard pricing tier (uptime SLA).
  - Both clusters run with:
    - Workload identity and the OIDC issuer.
    - Entra integration with Azure RBAC, and local accounts disabled.
  - The Codefresh runner stays on the existing runner cluster `<aks-cluster-context>`, never on an app cluster.
  - Container Apps stays live until Prod cutover.
- **Rationale.**
  - AKS Automatic uses node auto-provisioning, and clusters with it cannot be stopped (E42).
  - Its controls are enabled explicitly on Standard instead (R2-SRE §1).
- **Consequences.**
  - The platform takes on cluster operations: upgrades and node pools.
- **Dissent.**
  - None. The sre-security role preferred AKS Automatic in round 1 and conceded Standard (R2-SRE §1).

#### ADR-D2 Tool lanes — Decided

- **Context.** Every tool has overlapping "deploy" features.
- **Options.** Let each tool deploy a bit, or give one verb to each tool.
- **Who argued what.** R1-P §1 and §2, adopted by R2-OA §1, R2-GA §1 and R2-SRE §1.
- **Decision.** Each tool has one verb, and the other tools' overlapping features stay off:

  | Tool | Verb | Features that stay off |
  |---|---|---|
  | Codefresh | builds | `deploy`, `approval`, `helm` and `launch-composition` steps; the GitOps Runtime; Promotions |
  | Octopus | releases, promotes, approves, migrates and runs runbooks | Kubernetes YAML and Helm steps against app namespaces |
  | Argo CD | reconciles | Image Updater; sync windows |
  | GitHub | enforces merge rules (branch protection, rulesets) | GitHub Actions in the app repo (ADR-IR26) |

  `scripts/checks/tool-boundaries.sh` enforces these rules.
- **Rationale.** Three deployers are the top confusion risk (R1-P §6 D1).
- **Consequences.** The boundary lint runs in `platform-env/env-checks`.
- **Dissent.** None.

#### ADR-D3 Argo CD distribution and topology — Decided

- **Context.**
  - Codefresh GitOps Promotions are disabled after runtime 0.24.0 (E34).
  - "The GitOps Cloud product is no longer available" (E35).
  - The runtime's existing-Argo CD install mode needs a non-expiring admin token (R2-GA §2; R2-SRE §2).
- **Options.**
  - Upstream Argo CD, one instance per cluster.
  - A hub-and-spoke instance.
  - A Codefresh GitOps Runtime.
- **Who argued what.**
  - Upstream, one per cluster: R1-GA §6, R1-P §6 D3 and R1-SRE §3.
  - Optional Codefresh runtime: R1-CE §6 D10; retracted in R2-CE §2.2.
- **Decision.**
  - Upstream Argo CD 3.5.x on each cluster, from the `argo/argo-cd` chart with app version 3.5.3 (E13).
  - Instances: `argocd-nonprod` and `argocd-prod`.
  - Terraform bootstraps each instance once; after that, Argo CD manages itself through its own add-on Application.
  - No Codefresh GitOps Runtime and no hub.
- **Rationale.**
  - A hub concentrates prod credentials.
  - Octopus already spans instances.
- **Consequences.**
  - Argo CD 3.6.0-rc1 (2026-09-16) is not adopted until it reaches GA.
- **Dissent.** None.

#### ADR-D4 Promotion writer and Git write mode — Decided

- **Context.** Exactly one actor may change what version runs in each environment.
- **Options.**
  - Writers: Octopus's Argo CD step, Argo CD Image Updater, commits from CI, or Codefresh Promotions.
  - Write mode: direct commit or pull request.
- **Who argued what.**
  - All five choose Octopus: R1-OA §6 D2, R1-GA §6, R1-CE §6 D9, R1-SRE §6 and R1-P §2.
  - PR mode for prod was offered as an option (R1-GA §6).
- **Decision.**
  - Octopus step `update-argo-cd-image-tags` ("Update Argo CD Application Image Tags") is the only writer of pins.
  - It commits directly to `main`, with Trigger sync off.
  - It verifies with "Argo CD Application is healthy" and a timeout of 900 s. The default for the separate Wait step is 180 s (E4).
  - Octopus retries the step once.
  - Not used: Image Updater, commits from CI, or Codefresh Promotions.
- **Rationale.**
  - Verification waits for the commit the step itself created (E3; R2-OA §2 F1).
  - The gateway account can stay read-only, because `sync` is needed only for Trigger sync (E7).
  - The single human approval happens in Octopus, so a PR would ask for a second approval of the same decision (R1-GA §6).
- **Consequences.**
  - Without Trigger sync, lead time grows by Argo CD's polling interval (default 120 s plus jitter).
  - What happens when two concurrent deployments commit to the same branch is [VERIFY]. The step retry covers it.
- **Dissent.**
  - The pragmatist and the octopus-architect preferred Trigger sync in round 1 (R1-P §4; R1-OA §4).

#### ADR-D5 Sync policy and rollback — Decided

- **Context.**
  - Argo CD's manual rollback is blocked on applications with auto-sync (E40).
  - Two freeze calendars would disagree.
- **Options.**
  - Auto-sync everywhere, or manual sync in prod.
  - Argo CD sync windows, or Octopus freezes.
- **Who argued what.**
  - Auto-sync everywhere: R1-GA §6, R2-GA §4 and R1-P §6 D9.
  - Manual sync in prod was anticipated for sre-security; R1-SRE never proposed it.
- **Decision.**
  - Every named-environment Application uses `automated: {prune: true, selfHeal: true}` and `PruneLast=true`.
  - Named-environment Applications carry no `resources-finalizer`.
  - No sync windows are used.
  - Octopus deployment freeze `prod-weekend-freeze` covers weekends, matching the weekday releases in `app:docs/release-cadence.md`.
  - Rollback means Octopus "redeploy previous release", which writes the older tags.
  - Break-glass is a reviewed Git commit, surfaced by drift detection (`docs/runbooks/break-glass.md`).
- **Rationale.** Git is the gate, and Octopus holds the calendar.
- **Consequences.** Deleting an Application does not delete its workloads.
- **Dissent.** None.

#### ADR-D6 Configuration promotion across environments — Decided (gap closed by adjudication)

- **Context.**
  - A Kustomize base is shared by `tdd`, `uat` and `prod`.
  - A merged base change reaches every environment at once, outside the Octopus lifecycle.
  - The Helm design pinned chart versions per environment instead; no Kustomize proponent addressed this.
- **Options.**
  - Versioned remote bases.
  - Base directories copied per version.
  - Components promoted per environment.
- **Who argued what.** Not debated. This is a chief-architect decision.
- **Decision.**
  - A change that must roll through the environments is added as a Kustomize component under `gitops/workorders/components/<change>/`.
    - It is enabled first in `envs/tdd/config`, then `uat`, then `prod`, each by pull request.
    - It is folded into `base/` after all three have it.
  - A direct `base/` change that alters runtime behaviour needs the `all-environments` pull-request label and a platform-owner review, enforced by CODEOWNERS.
  - Configuration follows expand/contract: new configuration is added to every environment before the release that needs it reaches that environment.
- **Rationale.**
  - Keeps the one-field pin contract and needs no remote fetches.
- **Consequences.**
  - `docs/walkthroughs/02-schema-change.md` also teaches configuration expand/contract.
- **Dissent.** None recorded. It was not debated.

#### ADR-D7 Octopus space model and config-as-code location — Decided

- **Context.**
  - The platform space `<octopus-space>` is empty apart from built-ins.
  - The live project is in another space; its `.octopus/` directory in the legacy origin (copied unchanged into `20260923-001`) stays untouched.
- **Options.**
  - Config-as-code in the app repo, or in the environment repo.
  - Base path `.octopus/<project>`, or a path outside `.octopus`.
  - One worker pool per cluster, or one per environment.
- **Who argued what.**
  - App repo: R1-OA §5, R1-CE §5 and R1-P §5. All moved to the environment repo in round 2.
  - `.octopus/<project>`: R2-OA §6 and R2-P §6.
  - `octopus/projects/<project>`: R2-GA §6, R2-CE §6 and R2-SRE §6.
  - Per-environment pools: R2-GA §3 C2.
- **Decision.** The model is fixed in §7.2:
  - **Projects:** `workorders` and `workorders-infrastructure`. Their config-as-code base paths are `.octopus/workorders` and `.octopus/workorders-infrastructure` in the environment repo, with default branch `main`, which is protected.
  - **Environments:** `tdd`, `uat`, `prod`, `infra-nonprod` and `infra-prod`.
  - **Lifecycles:** `workorders-standard`, `workorders-hotfix` and `workorders-infrastructure`.
  - **Channels:** `Default` and `Hotfix`.
  - **Kubernetes worker pools:** `k8s-tdd`, `k8s-uat` and `k8s-prod`.
  - Terraform provider `OctopusDeploy/octopusdeploy` 1.20.0 manages everything that config-as-code excludes (E26, E30).
- **Rationale.**
  - The docs keep `.octopus` as the default and recommend a per-project subfolder for multiple projects (E26).
  - A path outside `.octopus` remains [UNVERIFIED] (R1-OA §5; R1-P §9).
  - The environment repo is covered by the stored Git credential.
  - The user directive places Octopus configuration in that repo.
  - Terraform steps can source files from the project's own Git repository (E29).
- **Consequences.**
  - Releases pass `GIT_REF: refs/heads/main` and no `GIT_COMMIT`, because an app SHA does not resolve in the environment repo (R2-OA §6; R2-CE §2 F1).
  - Traceability to the app commit comes from build information and release notes.
- **Dissent.** None.

#### ADR-D8 Identity federation between tools — Decided

- **Context.**
  - Incident EP18: an invalid or rotated Octopus API key caused 41 consecutive red runs over about 13 days (`app:arch/DeployFailure-2026-08-21.md`).
  - Codefresh push tokens embed `scm_user_name` in `sub` (E15).
  - Entra federated credentials need an exact subject match, and flexible credentials accept only GitHub, GitLab and Terraform Cloud (E37).
- **Options.**
  - API keys, or OIDC everywhere.
  - For Codefresh to ACR: Codefresh OIDC to Entra, runner workload identity, or repository-scoped tokens.
- **Who argued what.**
  - OIDC from Codefresh to Octopus: R1-CE §6 D7, R1-OA §6 D10, R1-SRE §6 and R1-P §6 D10.
  - For ACR, the sre-security role proposed runner workload identity (R1-SRE §6), and the pragmatist proposed Codefresh OIDC (R1-P §3, withdrawn in R2-P §2).
  - Repository-scoped tokens: R2-CE §2 F4 and R2-P §2.
- **Decision.**
  - **Codefresh to Octopus.** Superseded by ADR-IR32: the Space Manager API key of the existing user `AISF-Service-Account`, in the secret context `workorders-octopus`, attached to `workorders/release` only. The original design (service account `svc-codefresh-release` with an OIDC identity for `https://oidc.codefresh.io`) is the path back.
  - **Octopus to Azure.**
    - Per-environment Azure OIDC accounts `azure-oidc-deploy-{tdd,uat,prod}`.
    - Environment-lifecycle accounts `azure-oidc-env-lifecycle-{nonprod,prod}`.
    - An ACR feed with OIDC (E21, E30, E38).
  - **Codefresh to ACR.**
    - Repository-scoped ACR tokens held as Codefresh registry integrations; step images use a pull-only token (ADR-IR19).
    - The release token is also in the secret context `workorders-release`, for `supply_chain` (ADR-IR10).
    - Runner workload identity is Deferred [UNVERIFIED inside docker-in-docker].
  - **Signing.** Codefresh signs keyless through Sigstore.
  - **One Octopus API key in one pipeline (ADR-IR32).** `OCTOPUS_API_KEY` from `workorders-octopus` in `workorders/release`, and nowhere else (TB14). The legacy GitHub secret `OCTO_API_KEY` stays with the legacy path until decommission.
- **Rationale.** No stored bearer secret crosses from CI into Octopus or Azure.
- **Consequences.**
  - The foundation layer, applied by the Owner, creates the Octopus-issuer federated credentials once. Nothing federated is created by hand.
- **Dissent.** None.

#### ADR-D9 Secrets and SQL authentication — Decided

- **Context.**
  - Today the SQL connection string is a plain Container App environment variable that `deploy.yml` reads back.
  - The live `.octopus/variables.ocl` binds `az_login_appkey` to the account password (R1-SRE intro).
- **Options.**
  - The Key Vault CSI driver, the External Secrets Operator (ESO), Octopus sensitive variables, or SOPS.
- **Who argued what.**
  - ESO: R1-GA §6, R1-SRE §6, R2-GA §1 and R2-SRE §6.
  - CSI driver: R1-P §2.
- **Decision.**
  - **Key Vaults.** One Key Vault per environment (`<kv-workorders-{env}>`) plus one per cluster for platform secrets (`<kv-workorders-platform-{cluster}>`). All use the RBAC permission model.
  - **ESO stores.**
    - A namespaced `SecretStore` named `key-vault` in each app namespace, authenticated with workload identity.
    - A `ClusterSecretStore` named `platform-keyvault`, limited to the namespaces `argocd` and `octopus-argocd-gateway`.
  - **App SQL access.** Passwordless, with `Authentication=Active Directory Workload Identity` in a connection string that starts with `Server=` (F4).
  - **Interim SQL passwords.** Only two, both until WI-05:
    - `workorders_migrator` in every environment.
    - `workorders_acceptance` in TDD only.
- **Rationale.**
  - The app reads configuration from environment variables.
  - EF Core 10 requires `Microsoft.Data.SqlClient` 6.1.1 or later, which supports workload identity with no new package (E43).
  - The resolved version is [VERIFY], because no lock file exists.
  - Under access policies, a Contributor can grant itself data-plane access (R1-GA §6).
- **Consequences.**
  - Contributor alone cannot write secrets into an RBAC vault, so the foundation grants `Key Vault Secrets Officer` to the lifecycle identities, and PIM-eligible to the group `secret-writers` for people (ADR-IR29).
- **Dissent.** The pragmatist preferred the CSI driver (R1-P §2).

#### ADR-D10 Azure infrastructure layering — Decided

- **Context.** Contributor cannot create role assignments, locks or policy assignments (E36).
- **Options.**
  - One Terraform layer, or a privileged foundation plus a Contributor environment layer.
  - For the lifecycle runner: Octopus runbooks, Codefresh pipelines, or Crossplane.
- **Who argued what.**
  - The layer split: R1-GA §3.4, R1-SRE §3, R1-P §3 and R1-OA §3. All agree.
  - The runner: Octopus runbooks, argued by all five (R1-CE §6 D15).
- **Decision.**
  - **`terraform/foundation/`**
    - Applied by a human Owner or User Access Administrator, using Privileged Identity Management (PIM).
    - Creates resource groups, networks, ACR, Log Analytics, Terraform state and every user-assigned managed identity (UAMI).
    - Creates **every role assignment**, the Octopus-issuer federated credentials, locks, Azure Policy assignments and Entra groups.
  - **`terraform/environment/`**
    - Applied by Octopus runbooks (`env-plan`, `env-apply`, `env-destroy`).
    - Creates AKS clusters, SQL, Key Vaults, App Insights and the workload federated credentials.
    - Performs the one-time Argo CD bootstrap and installs the Octopus Kubernetes workers.
    - Contains zero `azurerm_role_assignment` resources; the boundary lint enforces this.
  - **Guardrails.**
    - `env-destroy` exists only for `infra-nonprod`.
    - It removes resources inside resource groups, never the groups themselves.
- **Rationale.** Pre-created identities keep their role assignments across cluster rebuilds.
- **Consequences.**
  - Rebuilding a cluster changes its OIDC issuer, so the workload federated credentials are re-created (limit: 20 per UAMI).
- **Dissent.** None.

#### ADR-D11 Supply chain and admission — Decided

- **Context.**
  - Legacy ships `:latest` from every branch, with no SBOM, signature or provenance (F12).
- **Options.**
  - Signing: keyless cosign, or Notation with Key Vault.
  - Admission: Kyverno, or Gatekeeper with Ratify.
  - Digests: mutate references to digests, or verify only.
- **Who argued what.**
  - Keyless signing with Kyverno: R1-SRE §6 and R1-CE §6 D11.
  - The mutation conflicts with self-heal: R2-GA §2.
  - Tag lock: R1-P §2.
- **Decision.**
  - **Codefresh `workorders/release`** produces the supply-chain evidence:
    - It signs keyless with `cosign.sign: true`.
    - It attaches an SBOM attestation from Syft.
    - It attaches a provenance attestation, labelled as step-authored and SLSA level 2 at most.
    - It locks the release tags in ACR.
  - **Kyverno `ImageValidatingPolicy`** verifies at the Deployment, Job and Rollout level against the release pipeline's Fulcio identity (E33):
    - It runs in Audit mode in nonprod from phase 2.
    - It runs in Enforce mode in prod before cutover.
  - **Provenance** stays in Audit mode.
  - **No `mutateDigest`.**
- **Rationale.**
  - Kyverno's rewrite would make the live object differ from Git, and self-heal would keep reverting it (R2-GA §2).
  - Octopus writes tags, so ACR tag locks carry immutability.
- **Consequences.**
  - The platform depends on public Sigstore (Fulcio and Rekor).
  - Break-glass is a PIM-gated, time-bound `PolicyException` (`docs/runbooks/break-glass.md`), honoured only in namespace `kyverno` (ADR-IR2).
  - The image policies evaluate at admission only (ADR-IR30).
- **Dissent.** The sre-security role wanted `mutateDigest` (R2-SRE §3 C1), which is rejected.

#### ADR-D12 Probes, health and exposure — Decided

- **Context.** Two app facts (F1–F3):
  - `/alive` runs only the `self` check and is mapped in every environment.
  - `/_healthcheck` runs every check.
- **Options.**
  - Readiness on `/_healthcheck`.
  - Readiness on `/alive`, with a `/ready` endpoint later.
- **Who argued what.**
  - Readiness on `/_healthcheck`: R1-SRE §8, withdrawn in R2-SRE §1.
  - `/alive`: R1-GA §8 and all round-2 papers.
- **Decision.**
  - **Probes.** Startup, liveness and readiness probes use `/alive` on port 8080 until WI-01 delivers `/ready`.
  - **Smoke.** `/_healthcheck` is used only by the Octopus smoke step.
  - **Gateway (uat and prod).** HTTPRoute rules answer `/_demo`, `/_diagnostics`, `/_healthcheck/detailed` and `/mcp` with a `RequestRedirect` to `/`, so those paths never reach the app.
  - **NetworkPolicies in each `workorders-<env>` namespace.**
    - Ingress is denied by default.
    - `ui-server:8080` accepts traffic only from the Gateway's namespace, `octopus-worker-<env>` and the `worker` pods.
    - Egress is not restricted until an FQDN-capable policy engine is adopted (Deferred to the phase-4 hardening).
- **Rationale.** In-cluster callers bypass the Gateway (R2-GA §2).
- **Consequences.** Readiness does not reflect database health until WI-01.
- **Dissent.** The sre-security role wants default-deny egress now (R1-SRE §3). Deferred.

#### ADR-D13 Human gates — Decided

- **Context.** Legacy uses GitHub environment approvals and a `force_skip_tdd` bypass (F13).
- **Options.**
  - GitHub environments.
  - Octopus manual interventions.
  - Octopus Approvals, which are in Public Preview (E39).
  - Platform Hub policies (ADR-C9).
- **Who argued what.**
  - Manual interventions: R1-OA §6 D13, R1-P §6 D5, R2-P §1 and R2-SRE §1.
  - A separation-of-duties guard: R1-SRE §8.
- **Decision.** The `workorders` process has four human or policy gates:
  - `uat-signoff`: a manual intervention in UAT for the `UAT Approvers` team.
  - `prod-go-no-go`: a manual intervention in Prod for the `Prod Approvers` team.
  - `sod-guard`: a script that fails when the approver created the deployment, or when either is the automation user (ADR-IR32).
  - `hotfix-justification`: a manual intervention on the `Hotfix` channel for `Release Managers`.
- **Rationale.** One audit trail. The Hotfix channel is the auditable replacement for `force_skip_tdd` (R1-P §3).
- **Consequences.**
  - Octopus Approvals, with "block approvals by the deployment creator", replaces the script guard when it reaches GA.
  - Since ADR-IR32 the approver teams hold the built-in Project Deployer role, scoped to their environment, so they can also create deployments there and override the prod freeze; `sod-guard` and the freeze-override reason are the controls.
- **Dissent.** None.

#### ADR-D14 Octopus-owned components inside clusters — Decided

- **Context.**
  - Octopus upgrades Kubernetes agents and workers automatically unless `upgrade_locked` is set (E30).
  - If Argo CD owned those Helm releases, it would fight the upgrades.
- **Options.**
  - Argo CD manages the workers and the gateway.
  - Terraform installs both.
  - A split between the two.
- **Who argued what.**
  - Gateway through Terraform: R1-OA §8.
  - Everything in-cluster from Git: R1-GA §3.4.
- **Decision.**
  - **Kubernetes workers.**
    - Installed by `terraform/environment/bootstrap.tf` as `helm_release` resources, one per environment, in namespace `octopus-worker-<env>`.
    - Octopus upgrades them after that. The release ignores version drift.
    - Argo CD does not manage them.
  - **Argo CD gateway.**
    - Installed as an Argo CD add-on Application from the pinned gateway chart.
    - Its secrets come through ESO.
    - Argo CD account `octopus` is read-only.
- **Rationale.** Neither component ends up with two controllers.
- **Consequences.**
  - Registering a worker needs a short-lived bearer token held as sensitive variable `Octopus.WorkerRegistrationToken`, passed as `TF_VAR_octopus_worker_registration_token` (ADR-IR1).
  - The token reaches Helm through a write-only argument, so it stays out of plan and state; it does live in the in-cluster Helm release Secret.
  - The auto-upgrader runs with namespaced roles only (ADR-IR20).
- **Dissent.** None recorded.

#### ADR-D15 Observability and SLOs — Decided

- **Context.**
  - The app exports to Azure Monitor when `ApplicationInsights:ConnectionString` is set, and over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (`app:src/ChurchBulletin.ServiceDefaults/Extensions.cs`).
- **Options.**
  - App Insights in the app only.
  - An in-cluster OTel collector with Prometheus.
  - Azure Managed Prometheus.
- **Who argued what.**
  - Prometheus-based analysis: R1-GA §8 and R2-SRE §1.
  - Burn-rate alerts: R1-SRE §8.
- **Decision.**
  - Each environment has one App Insights resource, and every environment writes to the shared Log Analytics workspace.
  - A fast-burn SLO alert (99.5 % of requests without a 5xx over 28 days) is defined in `terraform/environment/monitoring.tf` as a log alert. Health routes are excluded.
  - The OTel collector and Prometheus are Deferred until Rollouts analysis needs them (phase 6).
- **Rationale.** No new in-cluster component and no app change.
- **Consequences.** WI-12 removes the `user.name` metric tag (F15).
- **Dissent.** None.

#### ADR-D16 Parallel-run data, cutover and the Worker — Decided

- **Context.**
  - NServiceBus queues and DbUp journals would collide on a shared database.
  - The `WorkOrderProcessing` Worker has never been deployed (F5).
- **Options.**
  - For the new path's data: share the legacy databases, or create new ones.
  - For prod data at cutover: copy it, or keep sharing.
- **Who argued what.**
  - Separate databases until cutover: R1-GA §7.1, R1-SRE §7 R4 and R1-P §3.
  - A single migration owner at cutover: R2-P §4.
- **Decision.**
  - **During the parallel run.** The new path owns its own TDD and UAT databases.
  - **At cutover.** Prod data moves by a rehearsed copy into the new prod SQL server during a short write freeze (Q10).
  - **Migrations.** Exactly one migration owner. The legacy prod deployment is disabled through an approved change.
  - **Worker replicas.** Every overlay sets `replicas: 0` for the Worker until it is enabled:
    - In TDD in phase 2.
    - In UAT in phase 3.
    - In prod only after product-owner sign-off, because the AI bot saga goes live then.
- **Rationale.**
  - Events published with no subscriber leave no backlog; the Worker subscribes on start.
- **Consequences.** Cutover needs a maintenance window (§9).
- **Dissent.** None.

#### ADR-D17 Codefresh pipeline trust boundaries — Recommended to user (runtimes); Decided (pipeline rules)

- **Context.**
  - Docker-in-docker build pods are privileged.
  - Branch code (`build.ps1`, the tests, the docs-only classifier) runs inside the gates of every build.
- **Options.**
  - One shared runtime, or separate runtimes for CI and release.
- **Who argued what.**
  - Split runtimes: R1-SRE §3 and R2-CE §1.
  - Contexts unattached: R1-CE §6 D16.
- **Decision.**
  - **Pipeline rules (Decided).**
    - Every `workorders/*` spec loads its YAML from `main` of the environment repo (`specTemplate`), and the scripts come from the same branch, so app-repo branch authors cannot change a pipeline (ADR-D18).
    - `workorders/release` triggers only on `master` of the app repo and runs with concurrency 1.
    - Release credentials exist only in `workorders/release`; `workorders/ci` and `workorders/preview` get no release context.
    - Step images are pulled with the pull-only integration `acr-platform-pull` and pinned by digest (ADR-IR19).
    - The contexts `azure-runtime-provisioner` and `github-aisf-sample-apps-token` are attached to no pipeline.
  - **Runtimes (Recommended to user).** Two runtime environments on the runner cluster, on separate node pools:
    - `<cf-runtime-ci>`: no cloud identity.
    - `<cf-runtime-release>`: tainted.
- **Rationale.** A branch build could otherwise poison the Docker layer cache that release builds reuse.
- **Consequences.** The runner cluster needs one extra node pool.
- **Dissent.** None.

#### ADR-D18 Repository layout: everything in the environment repo — Decided (user-approved 2026-09-24)

- **Context.**
  - User directive: the app repo is `clearmeasure-aisf-sample-apps/20260923-001`, a copy of the origin at `24da122`, and it stays untouched.
  - Every non-Markdown path counts as code for `app:.github/scripts/detect-code-changes.sh` (F16), so platform files in the app repo would trigger full CI runs.
  - Codefresh pipeline specs carry their own `specTemplate` (repo, path, revision, Git context), independent of their triggers (E32).
- **Options.**
  - (a) Codefresh pipelines and container definitions in the app repo, everything else in the environment repo.
  - (b) Everything in the environment repo; the app repo receives settings only.
- **Who argued what.** R1-P §5 (a seed copy moved in phase 2); the codefresh-engineer proposed (b) during implementation; the user approved (b).
- **Decision.** (b):
  - The environment repo holds `codefresh/workorders/**` (pipelines, specs, scripts, `version.env`), `codefresh/images/ci-dotnet/Dockerfile` and `containers/workorders/{worker,db-migrator}/Dockerfile`, next to everything in §6.1.
  - Every `workorders/*` spec loads its YAML from `main` of the environment repo through the stored Git integration `github-aisf-sample-apps`; the triggers of `workorders/ci`, `workorders/release` and `workorders/preview` watch the app repo.
  - Each pipeline clones the app repo at the triggering commit (`main_clone`, full depth) and this repo at `main` (`platform_clone`) for its scripts.
  - The UI image keeps the app repo's root `Dockerfile`, copied into the build context; the file in the app repo never changes.
- **Rationale.**
  - The app repo stays untouched and its CI is not triggered by platform changes.
  - App-repo branch authors cannot change pipeline YAML or scripts; one review surface (CODEOWNERS on `main`) guards the whole platform.
- **Consequences.**
  - The YAML and the scripts are read from `main` at build start; a push to `main` between the two reads can mix two commits in one build [VERIFY how Codefresh resolves the template revision]. The provenance records both commits.
  - `workorders/ci-image` triggers on `main` of this repo (`codefresh/images/**`), and `platform/ci-dotnet` carries this repo as its OCI source.
  - No staging copy exists anywhere: this tree becomes the first commits on `main` of the environment repo.
- **Dissent.** None.

### 2.3 Evidence register

Entries E1–E33 were verified this session on 2026-09-23. E34–E43 were verified by the debaters (the source paper is noted), and their URLs were re-checked for consistency. E44–E49 come from the implementation and the integration review (2026-09-24); the observer is noted.

| # | Fact | Source |
|---|---|---|
| E1 | The image-tag step behaves differently by source type. **Kustomize:** it edits only `newTag`. **Helm:** it rewrites the chart's default `values.yaml` and every file in `valueFiles`, and never `valuesObject`. **Plain YAML:** it scans known kinds only (CRDs are excluded). It picks the Git credential by repository restriction, and fails when a referenced repository has no credential. | https://octopus.com/docs/argo-cd/steps/update-application-image-tags |
| E2 | Octopus "cannot update charts sourced from a Helm repository or OCI feed"; only Git sources are supported for updates. | https://octopus.com/docs/argo-cd/troubleshooting |
| E3 | Commit methods are direct commit and pull request. Verification options are "Argo CD Application is healthy" (2026.1+) and "Pull request merged" (2026.2+). Trigger sync is optional. | https://octopus.com/docs/argo-cd/steps |
| E4 | The Wait for Argo CD Applications step has a default timeout of 180 s and accepts commit hashes of 7–40 hex characters. | https://octopus.com/docs/argo-cd/steps/wait-for-argo-cd-applications |
| E5 | Scoping annotations are `argo.octopus.com/project`, `environment` and `tenant`, with an optional `.<source-name>` suffix; an unnamed single source takes the unscoped form. | https://octopus.com/docs/argo-cd/annotations |
| E6 | The Helm-annotation examples use charts from Git repositories. | https://octopus.com/docs/argo-cd/annotations/helm-annotations |
| E7 | The documented gateway account (`accounts.octopus: apiKey`) is granted `applications get`, `applications sync`, `clusters get` and `logs get`. `sync` serves Trigger sync only (R2-GA §2), so a read-only account is [VERIFY] in the spike. | https://octopus.com/docs/argo-cd/instances/argo-user |
| E8 | The gateway chart values cover registration (`serverApiUrl`, `spaceId`, `environments`, `name`, a token secret) and `argocd.serverGrpcUrl`. | https://octopus.com/docs/argo-cd/instances/helm-chart-values |
| E9 | Octopus 2026.2 still "evolves the Argo CD Preview". 2026.3 adds cross-space gateways, the Wait step for self-hosted instances, and a deployment approval system. GA in 2026.4 is [UNVERIFIED]. | https://octopus.com/downloads/whatsnew/2026.3 |
| E10 | Kustomize's built-in image field specs include `spec/template/spec/containers[]/image`, with no kind filter. | https://github.com/kubernetes-sigs/kustomize/blob/master/api/internal/konfig/builtinpluginconsts/images.go |
| E11 | The Argo Rollouts Kustomize integration: transformer config for Rollout CRDs; OpenAPI patching from Kustomize 4.5.5. | https://argoproj.github.io/argo-rollouts/features/kustomize/ |
| E12 | During selective sync, "Hooks are **not** run" and the sync is not recorded in history. | https://argo-cd.readthedocs.io/en/stable/user-guide/selective_sync/ |
| E13 | Argo CD v3.5.3 is the latest stable release (2026-09-14); v3.6.0-rc1 was published on 2026-09-16. | https://github.com/argoproj/argo-cd/releases |
| E14 | PR generator parameters include `head_sha`, `head_short_sha` and `head_short_sha_7`. Label filtering requires all listed labels. "Only admins may create ApplicationSets." | https://argo-cd.readthedocs.io/en/stable/operator-manual/applicationset/Generators-Pull-Request/ |
| E15 | Codefresh OIDC tokens use issuer `https://oidc.codefresh.io`. A git-push `sub` embeds `scm_repo_url`, `scm_user_name` and `scm_ref`. Tokens expire after 5 minutes. | https://codefresh.io/docs/docs/integrations/oidc-pipelines/ |
| E16 | In `obtain-oidc-id-token` 1.2.3, `AUDIENCE` defaults to `https://g.codefresh.io`, and the step exports `ID_TOKEN`. | https://codefresh.io/steps/step/obtain-oidc-id-token |
| E17 | `octopusdeploy-login` 1.0.0 takes `ID_TOKEN`, `OCTOPUS_URL` and `OCTOPUS_SERVICE_ACCOUNT_ID`, and exports `OCTOPUS_ACCESS_TOKEN`. | https://codefresh.io/steps/step/octopusdeploy-login |
| E18 | `octopusdeploy-create-release` 1.0.1 takes `GIT_REF`, `GIT_COMMIT`, `CHANNEL`, `PACKAGE_VERSION`, `PACKAGES`, `IGNORE_EXISTING`, `RELEASE_NOTES` and `RELEASE_NOTES_FILE` ("release notes to attach, from file"), and has no custom-field argument. Its step definition renders `RELEASE_NOTES` unescaped into generated YAML (codefresh-engineer, from the step's `step.yaml`). | https://codefresh.io/steps/step/octopusdeploy-create-release |
| E19 | Octopus OIDC subjects for other issuers support the wildcards `*` and `?` (added in 2024.1), and a custom audience. | https://octopus.com/docs/octopus-rest-api/openid-connect/other-issuers |
| E20 | Default OIDC subject keys are space, project, tenant and environment, giving `space:<s>:project:<p>:environment:<e>`. Keys can be customised. | https://octopus.com/docs/infrastructure/accounts/openid-connect |
| E21 | Azure OIDC accounts need Octopus 2023.4+. The issuer URL has no trailing slash. They need AzureRM provider 3.22+ and az CLI 2.30+. | https://octopus.com/docs/infrastructure/accounts/azure |
| E22 | Codefresh: "ARM support is only available to Enterprise customers." | https://codefresh.io/docs/docs/installation/runner/arm-support/ |
| E23 | Codefresh Windows builds are in incubation, available on request, on a Windows Server 1709 VM. | https://codefresh.io/docs/docs/incubation/windows/ |
| E24 | Platform Hub permissions (PlatformHubEdit, PlatformHubView) "can only be assigned to system teams". | https://octopus.com/docs/platform-hub |
| E25 | The pricing page lists Platform Hub, ITSM, SIEM audit streaming and Insights with DORA under Enterprise, at a list price of $24,600/year for Octopus Cloud, as shown on 2026-09-23. | https://octopus.com/pricing |
| E26 | The config-as-code directory defaults to `.octopus` and "can be changed"; a per-project subfolder is recommended when several projects share a repo. Sensitive variables, channels, triggers, environments and lifecycles are not stored in Git. | https://octopus.com/docs/projects/version-control/config-as-code-reference |
| E27 | The Kubernetes worker "is limited to modifying its local namespace". Each step can set its own container image. No Docker is available, and inline execution containers are not supported. | https://octopus.com/docs/infrastructure/workers/kubernetes-worker |
| E28 | Kubernetes agent script pods have cluster-wide admin access unless restricted; the chart exposes `scriptPods.serviceAccount.targetNamespaces` and `annotations`. | https://octopus.com/docs/kubernetes/targets/kubernetes-agent/permissions |
| E29 | Terraform steps can source templates from Git, including the project's own repository (2024.1). Variable substitution is applied to `*.tf` files by default. | https://octopus.com/docs/deployments/terraform/working-with-built-in-steps |
| E30 | Provider `OctopusDeploy/octopusdeploy` 1.20.0 (2026-09-21) includes `octopusdeploy_azure_container_registry` with `oidc_authentication` (subject keys `space` and `feed`), `octopusdeploy_azure_openid_connect` with `execution_subject_keys`, `octopusdeploy_service_account_oidc_identity`, `octopusdeploy_kubernetes_agent_worker` with `upgrade_locked`, and `octopusdeploy_deployment_freeze`. | https://registry.terraform.io/providers/OctopusDeploy/octopusdeploy/latest/docs |
| E31 | Push rulesets apply only to private or internal repositories. Bypass can be granted to roles, teams or GitHub Apps. | https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets |
| E32 | Codefresh pipeline specs (`codefresh create pipeline -f`) define `triggers`, `contexts`, `runtimeEnvironment`, `specTemplate` and `concurrency`. Trigger events include `push.heads`, `pullrequest.opened`, `pullrequest.synchronize` and `pullrequest.labeled` [VERIFY exact spellings]. | https://codefresh.io/docs/docs/integrations/codefresh-api/ |
| E33 | The Fulcio SAN template for Codefresh is `{{.platform_url}}/{{.account_name}}/{{.pipeline_name}}:{{.account_id}}/{{.pipeline_id}}`. | https://github.com/sigstore/fulcio/blob/main/config/identity/config.yaml |
| E34 | Codefresh Promotions are disabled in GitOps runtimes after 0.24.0 (R1-GA §6; R1-P R2). | https://codefresh.io/docs/docs/promotions/promotions-overview/ |
| E35 | "The GitOps Cloud product is no longer available"; Codefresh CI continues (R1-GA §6; R1-P R1). | https://octopus.com/codefresh |
| E36 | Contributor NotActions include `Microsoft.Authorization/*/Write` (R1-GA §3.4; R1-P R37). | https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/privileged |
| E37 | Flexible federated identity credentials support only GitHub, GitLab and Terraform Cloud issuers (R2-CE §2; R2-SRE §2). | https://learn.microsoft.com/en-us/entra/workload-id/workload-identities-flexible-federated-identity-credentials |
| E38 | Octopus supports an ACR feed over OIDC from 2025.2 (R2-SRE §2). | https://octopus.com/blog/oidc-external-feeds |
| E39 | Octopus Approvals is in Public Preview (R2-SRE §2; R2-P §2). | https://octopus.com/docs/approvals/octopus-approvals |
| E40 | Argo CD cannot roll back an application that has automated sync enabled (R1-P R17). | https://argo-cd.readthedocs.io/en/stable/user-guide/auto_sync/ |
| E41 | Octopus ephemeral environments get no lifecycles, freezes, Insights or tenants, and no native Argo CD support (R2-SRE §2). | https://octopus.com/docs/projects/ephemeral-environments |
| E42 | Clusters that use node auto-provisioning cannot be stopped (R2-SRE §2). | https://learn.microsoft.com/en-us/azure/aks/start-stop-cluster |
| E43 | EF Core SqlServer 10 depends on `Microsoft.Data.SqlClient` 6.1.1 or later (R2-GA §2). | https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.SqlServer/10.0.0 |
| E44 | Observed 2026-09-24: the Actions API lists no workflow for the fork `clearmeasure-aisf-sample-apps/20260923-001`, although `.github/workflows/` holds three. GitHub keeps a fork's workflows off until someone enables them in the Actions tab [UNVERIFIED in the docs; observed]. | GitHub API, read by the orchestrator |
| E45 | The OCL action-type catalog lists `Octopus.ArgoCDUpdateImageTags`, with no property keys (octopus-architect, 2026-09-24). | https://github.com/OctopusDeploy/github-agent-plugin/blob/main/skills/writing-ocl/references/action-types.md |
| E46 | Output variables are addressed by step name: `Octopus.Action[StepName].Output.VariableName`. | https://octopus.com/docs/projects/variables/output-variables |
| E47 | The live permission set of the session's Octopus user in the platform space (teams `Everyone` and `Space Managers`; read 2026-09-23) names the space permissions `InterruptionView`, `InterruptionSubmit`, `InterruptionViewSubmitResponsible`, `ProjectEdit`, `ReleaseCreate`, `BuiltInFeedPush`, `BuildInformationPush` and others. Its system permissions are `UserView`, `UserRoleView`, `TeamView`, `DeploymentFreezeAdminister` and the SSH known-host permissions, without `UserEdit` or `UserRoleEdit`. | Octopus API `/api/users/{id}/permissions`, read by the orchestrator |
| E48 | A GitHub App authenticates with a JWT signed by its private key (at most 10 minutes), then creates an installation access token (one hour) that can be narrowed to listed repositories and permissions. | https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-json-web-token-jwt-for-a-github-app, https://docs.github.com/en/rest/apps/apps#create-an-installation-access-token-for-an-app |
| E49 | Chart `kubernetes-agent` 3.15.1 binds a cluster-wide `*` ClusterRole to the auto-upgrader service account with default values; `useNamespacedRoles` and `clusterRole.enabled` switch to namespaced Roles (sre-security, `helm template`, 2026-09-24). | `helm template` of the chart |

### 2.4 Repo facts used above (app repo: origin at `46104c1`, copy `20260923-001` at `24da122`)

The cited files are identical in both commits.

| # | Fact | Location |
|---|---|---|
| F1 | `/alive` runs only the `self` check (tag `live`). `MapDefaultEndpoints` maps it in every environment. | `app:src/ChurchBulletin.ServiceDefaults/Extensions.cs` |
| F2 | `/_healthcheck` and `/health` run the LlmGateway, DataAccess, Server, API, Jeffrey, NeedsReboot and ProcessThreadCount checks. | `app:src/UI/Server/UIServiceRegistry.cs` |
| F3 | Anonymous endpoints are mapped in every environment: `GET /_demo/setneedsreboot/{bool}`, `POST /_diagnostics/reset-db-connections`, `GET /_version`, `GET /api/version`, `/_healthcheck/detailed` and `/mcp`. | `app:src/UI/Server/ServerApplication.cs`, `app:src/UI/Api/Controllers/DiagnosticController.cs` |
| F4 | A connection string that starts with `Data Source=` selects SQLite and LearningTransport. The `Testing` environment only calls `EnsureCreated`, and a SQLite preview has no rows. | `app:src/UI/Server/ServerApplication.cs`, `app:src/UI/Server/TestingDatabaseStartupFilter.cs` |
| F5 | The Worker is a generic host running NServiceBus endpoint `WorkOrderProcessing`. It always uses the SQL transport with outbox and installers, and requires `RemotableBus:ApiUrl`. It publishes no `appsettings`, has no HTTP endpoint, and its output is `Worker.dll`. | `app:src/Worker/*` |
| F6 | The root Dockerfile copies `/built/`, which CI stages from the `ChurchBulletin.UI` nupkg. | `app:Dockerfile`, `app:.github/workflows/build.yml` (Publish Release Candidate) |
| F7 | The AppHost defines resources `ui-server` and `worker`, with connection strings `SqlConnectionString` and `AppInsights`. | `app:src/ChurchBulletin.AppHost/AppHost.cs` |
| F8 | The acceptance setup always calls `ZDataLoader.LoadData()`, which triggers `DatabaseEmptier.DeleteAllData()`: every `[dbo]` table is emptied except `SchemaVersions` and `sysdiagrams`, then reseeded. | `app:src/AcceptanceTests/ServerFixture.cs`, `app:src/IntegrationTests/{ZDataLoader,DatabaseEmptier}.cs` |
| F9 | The DbUp console takes positional arguments: server, database, script directory, user, password. It supports SQL authentication or integrated security only. | `app:src/Database/Console/*` |
| F10 | `008_AddSomeEmployeeRecords.sql` inserts 10 employees. No migration inserts roles. `scripts/{Create,Everytime,TestData}` hold only placeholders. | `app:src/Database/scripts/**` |
| F11 | `build.ps1` detects only GitHub Actions and otherwise forces `/tmp/nuget-packages`. It supports `SQL_EXTERNAL`, `SQL_SERVER_HOST` and `SQL_SA_PASSWORD`. | `app:build.ps1` |
| F12 | `build-result` depends on eight jobs, including ARM and Windows. Legacy CI pushes `churchbulletin.ui:latest` from every branch, publishes to Octopus with an API key, and has `security-scan` disabled. | `app:.github/workflows/build.yml` |
| F13 | TDD acceptance tests use the Container App's connection string. Versions are `2.4.<run>`. `force_skip_tdd` exists. | `app:.github/workflows/deploy.yml` |
| F14 | The live process includes a disabled firewall step, DbUp on `hosted-windows`, and `az containerapp update`; `az_login_appkey` is bound to the account password. | `app:.octopus/*.ocl` |
| F15 | `LoginCounter` is tagged with `user.name`. | `app:src/DataAccess/Handlers/TelemetryHandler.cs` |
| F16 | Every path counts as code except `*.md`, `docs/*`, `LICENSE*` and `.github/ISSUE_TEMPLATE/*`, so any YAML, HCL or OCL placed in the app repo would trigger the full CI (one reason for ADR-D18). | `app:.github/scripts/detect-code-changes.sh` |
| F17 | The session's checkouts of the app repo are shallow clones. | `git rev-parse --is-shallow-repository` returns `true` |
| F18 | `20260923-001` is a public fork of the origin, with `build.yml`, `deploy.yml` and `render-diagrams.yml` in `.github/workflows/` and no registered workflow (E44). `build.yml` publishes with secrets the copy does not have (`AZURE_CREDENTIALS`, `OCTO_API_KEY`); `deploy.yml` runs after every successful `Build` on `master`. | `app:.github/workflows/*.yml` |

### 2.5 Integration-review addenda (2026-09-24)

The integration review compared the five implementation packages with this design and with each other. Every queue item is decided below and applied in the files; §2.1 and §2.2 point here where an ADR changed. Queue item Q*n* maps to ADR-IR*n*; ADR-IR26 to ADR-IR31 cover later findings (the fork, R16, N1, N2, N4, N5); ADR-IR32 records a user directive. `docs/consistency-notes.md` maps each item to its evidence.

#### Fixes, names and product facts

| ID | Item | Decision | Applied in |
|---|---|---|---|
| ADR-IR1 | Terraform input names | The runbooks `env-plan`, `env-apply` and `env-destroy` set `TF_VAR_octopus_worker_registration_token` from `Octopus.WorkerRegistrationToken` and `TF_VAR_argocd_repo_read_credential` from `ArgoCD.RepoReadCredential` (JSON through the `JsonEscape` filter [VERIFY]) | `.octopus/workorders-infrastructure/runbooks/env-*.ocl`; check C22 |
| ADR-IR3 | Worker against `require-probes` | Confirmed as built: a match condition skips `app.kubernetes.io/name: worker`; it is removed with WI-04, before the Worker runs in prod | `policies/kyverno/base/workload-baseline.yaml` |
| ADR-IR4 | Owner of `policies/**` | Security owners alone; admission policies are security controls | `CODEOWNERS`, §6.1, §6.2 |
| ADR-IR5 | Provider pins | `azurerm ~> 5.6`, `azuread ~> 3.9`, `helm ~> 3.3`, `kubernetes ~> 3.2`, `random ~> 3.9` (current majors on 2026-09-24) | `terraform/*/versions.tf`, §11.4 |
| ADR-IR6 | New placeholders and the Docker feed | Ratified in §7.1: `<argocd-{cluster}-host>`, `<entra-group-object-id-*>`, `<OCTOPUS_HOST>` (host of `<OCTOPUS_URL>`, for the gateway's gRPC address), `<previews-hostname-suffix>`, `<CF_PREVIEW_PIPELINE_ID>` (the preview attestor now pins the exact Fulcio subject instead of a pattern), `<platform-bots-author-regex>`, `<provisioner-secret-expires-on>`, `<github-status-app-id>`, `<github-status-app-installation-id>`. The Docker feed is a name, not a placeholder: `docker-hub`, created by `octopus/terraform/feeds.tf` | Contracts `placeholders`; feeds, runbooks, Kyverno nonprod overlay |
| ADR-IR7 | UAMI client IDs | One placeholder per identity: `<client-id-of-<uami name>>`, for example `<client-id-of-id-workorders-tdd-app>`; `<uami-client-id>` is retired | `gitops/**`, `argocd/clusters/*/addons/*`, `octopus/terraform/terraform.tfvars.example` |
| ADR-IR8 | Preview migrator | `workorders/preview` also builds `workorders-previews/db-migrator:pr-<n>-<head sha>`, and the ApplicationSet maps `workorders/db-migrator` to it, so a preview runs its own pull request's migrations (amends ADR-C4) | `codefresh/workorders/pipelines/preview.yml`, `argocd/optional/workorders-previews-appset.yaml`, `gitops/workorders/previews/*` |
| ADR-IR9 | `octopus/terraform` state key | `octopus-space.tfstate`, in the shared state container | `octopus/terraform/versions.tf`, §7.10 |
| ADR-IR11 | Home of `StepImage.CiDotnet` | In each project's `variables.ocl` (config-as-code, reviewed with the process that uses it), pinned by digest; check C14 fails when any reference to the toolchain image in `.octopus/**` or the Codefresh pipelines differs | Both `variables.ocl`, the three pipelines, contracts `images.ciToolchain` |
| ADR-IR12 | Contract-file keys | `promptedVariables`, `notationTokens` and `placeholderPatterns` are ratified; the placeholder families are `<*-chart-version>`, `<*-version>`, `<*-digest>`, `<*-sha256>`, `<client-id-of-*>` and `<entra-group-object-id-*>` | `contracts/platform-contracts.yaml` |
| ADR-IR21 | Argo CD step action type | `Octopus.ArgoCDUpdateImageTags` (E45) replaces the placeholder; the phase-2 spike compares it and the property keys with an OCL export [VERIFY] | `deployment_process.ocl`, §7.2 |
| ADR-IR22 | Output variables | Keyed by step name (E46), for example `Octopus.Action[Prod go/no-go].Output.Manual.ResponsibleUser.Id`; `sod-guard` keeps the slug form as a fallback | §7.2, `deployment_process.ocl` |
| ADR-IR23 | Release notes | Passed as `RELEASE_NOTES_FILE`; `RELEASE_NOTES` is forbidden, because the step renders it unescaped into YAML (E18). The first line stays `app-commit: <40-hex sha>`, written by `buildinfo.sh`; check C16 verifies both | §7.7, `release.yml`, contracts `codefresh.handoff` |
| ADR-IR24 | Preview SQL certificate | WI-07 adds an explicit trust option for the preview's self-signed SQL Server; no Service named `mssql-localhost`, which would depend silently on the console's host-name heuristic | §8, `gitops/workorders/previews/database.yaml` |
| ADR-IR25 | `CreateNamespace` with no cluster-scoped kinds | Stays [VERIFY] for the phase-6 spike; fallback: allow kind `Namespace` in AppProject `workorders-previews` only | ApplicationSet comment |
| ADR-IR32 | One Octopus credential | The Space Manager API key of `AISF-Service-Account`; see the ADR below | `octopus/terraform/teams.tf`, `.octopus/workorders/*`, `contracts/`, `scripts/checks/`, `docs/` |
| ADR-IR31 | NServiceBus diagnostics (N5) | An `emptyDir` at `/app/.diagnostics` on `ui-server` and `worker`, next to `/tmp`; whether a failed write is fatal is [VERIFY] in the phase-2 spike | `gitops/workorders/base/{ui-server,worker}.yaml` |

#### ADR-IR2 Kyverno policy exceptions — Decided (amends ADR-D11)

- **Context.** Break-glass relies on a time-bound `PolicyException`, but both Kyverno add-ons left `features.policyExceptions` at the chart default (off), so an exception would do nothing.
- **Decision.** Both add-ons set `features.policyExceptions.enabled: true` and `features.policyExceptions.namespace: kyverno`. Only PIM-activated cluster administrators can write that namespace.
- **Consequences.** An exception in any other namespace is ignored. Whether the flags also govern the CEL `PolicyException` kind (`policies.kyverno.io`) is [VERIFY] in the phase-2 spike; the fallback is a reviewed, temporary Audit patch in the overlay.

#### ADR-IR10 Registry credentials for `supply_chain` — Decided (amends ADR-D8, §7.7)

- **Context.** Syft, `cosign attest`, crane and the tag lock need the release token as a Docker config. A freestyle step cannot use a registry integration, and §7.7 named no context key, so `supply_chain` failed closed and the Octopus handoff never ran (a P1 blocker).
- **Options.** (a) Two keys in `workorders-release`; (b) a separate context; (c) attestations and locks by another identity; (d) runner workload identity (Deferred, ADR-D8).
- **Decision.** (a). `workorders-release` becomes a secret context with `ACR_TOKEN_NAME=cf-workorders-release` and `ACR_TOKEN_PASSWORD`. The step writes a step-local Docker config from them and deletes it on exit.
- **Rationale.** No new token and no new context name; the context is attached to the release pipeline only, on the release runtime.
- **Consequences.**
  - The password lives in the integration and the context; both are rotated together (`docs/runbooks/credential-rotation.md` §3).
  - Every step of `workorders/release`, including the gates that run the merged master's `build.ps1`, can read the context, as it can already read the Octopus values. A Codefresh secret-store context referenced only by `supply_chain` would narrow this [VERIFY]; it is a phase-4 hardening option.

#### ADR-IR13 SQL DB Contributor in UAT — Decided (amends §5.2)

- **Context.** `db-backup` and `db-restore-pitr` run in `uat` as `azure-oidc-deploy-uat`; §5.2 granted SQL DB Contributor to the prod deployment identity only, so every UAT run would fail.
- **Decision.** `id-octopus-deploy-uat` also holds SQL DB Contributor on `rg-workorders-uat` (foundation `deploy_identity_sql_db_contributor_envs = ["uat", "prod"]`). TDD does not.
- **Rationale.** The UAT runbooks rehearse the prod procedures (the PITR drill is a P4 exit criterion); a separate identity would add a credential for the same blast radius.
- **Consequences.** The UAT deployment identity can copy, restore, rename and delete UAT databases; UAT holds no production data.

#### ADR-IR14 Committed environment inputs — Decided (amends ADR-D10)

- **Context.** The runbooks pass `-var-file=#{Environment.Class}.tfvars` from Git. Committing resource IDs was acceptable only in a private repo, and the repo is now private (R2).
- **Decision.** `terraform/environment/{nonprod,prod}.tfvars` are committed by reviewed pull request: resource IDs, names, sizing, IP ranges and alert receivers, never a secret. Secrets reach Terraform only as `TF_VAR_*` from Octopus sensitive variables. If the repo ever turns public, the identifiers move to Octopus variables first.
- **Rationale.** One reviewed source for the layer's inputs; the plan artifact shows every change.
- **Consequences.** The files are created at bootstrap from the `.example` files (`docs/bootstrap.md` step 2); Gitleaks scans them.

#### ADR-IR15 Bootstrap read credential for Argo CD — Decided (amends §5.2)

- **Context.** With a private repo, Argo CD needs a credential before ESO exists. The environment layer seeds Secret `argocd-repo-creds` from the ephemeral, write-only variable `argocd_repo_read_credential`, but nobody was named to hold it.
- **Decision.** A read-only GitHub App (R11), installed on the environment repo only with `Contents: read`. Its JSON credential lives in the Octopus sensitive variable `ArgoCD.RepoReadCredential` (project `workorders-infrastructure`, for cluster builds) and in `argocd-repo-read-credential` in each platform vault, from which ESO keeps the Secret current (`creationPolicy: Orphan`, `mergePolicy: Merge`).
- **Consequences.** Two copies, rotated together; never the stored org-wide PAT.

#### ADR-IR16 Octopus system objects — Superseded by ADR-IR32

- **Context.** Service accounts need `UserEdit` and custom roles need `UserRoleEdit`, both system permissions. Platform engineers are Space Managers, which hold `UserView` and `UserRoleView` but not the edit permissions (E47).
- **Decision.** A System Manager (the user, R23) runs the first `octopus/terraform` apply, which creates `svc-codefresh-release`, `svc-argocd-gateway`, their OIDC identity and the custom roles. Platform engineers keep Space Manager and apply everything else; a change to a system object goes back to the System Manager.
- **Rationale.** Least privilege; system objects change rarely.
- **Consequences.** One more person in bootstrap step 3. Later Space Manager applies read the system objects [VERIFY that the refresh needs no more than the view permissions].

#### ADR-IR17 Approvers and the prod freeze — Superseded by ADR-IR32 (no custom roles)

- **Context.** A responsible team member needs `InterruptionViewSubmitResponsible`. Every built-in role that carries it also carries `ProjectEdit`, which lets its holder override a project deployment freeze.
- **Decision.** Custom role `Work Orders Approver` with `ProjectView`, `ProcessView`, `ReleaseView`, `DeploymentView`, `EnvironmentView`, `LifecycleView`, `TaskView`, `ArtifactView`, `EventView`, `InterruptionView`, `InterruptionSubmit` and `InterruptionViewSubmitResponsible`, for `UAT Approvers` (scoped to `uat`) and `Prod Approvers` (scoped to `prod`). Release Managers create UAT and prod deployments and override the prod freeze with a reason; Space Managers (Platform Engineers) keep that power by role, approvers lose it.
- **Rationale.** Approving and deploying become separate roles by construction; `sod-guard` remains the second control.
- **Consequences.** The names come from the live permission set (E47); whether the set suffices is [VERIFY] in the spike.

#### ADR-IR18 Floating Octopus CLI image — Decided, phased (amends §7.7)

- **Context.** The Octopus marketplace steps run `octopuslabs/octopus-cli` at a floating tag. A replaced image would run with the release's Octopus access token and could push a different `ChurchBulletin.Database` package, which DbUp then executes.
- **Decision.** Accepted while releases reach TDD only (phases 1–2). Before the phase-3 exit, the four Octopus steps become freestyle steps on the digest-pinned toolchain image, which installs the Octopus CLI at a pinned version with a verified checksum. The handoff contract (order, arguments, `RELEASE_NOTES_FILE`, no `GIT_COMMIT`) does not change. Owner: codefresh-engineer; check C16 then checks commands instead of step types.
- **Rationale.** TDD data is disposable; promotions to UAT and prod start in phase 3.
- **Consequences.** A P3 exit criterion (§9) and a P4 precondition (`docs/bootstrap.md` step 11).

#### ADR-IR19 Account-wide registry integrations — Decided; residual risk accepted (amends ADR-D8, ADR-D17)

- **Context.** Codefresh registry integrations are account-wide: any pipeline YAML can name one. `workorders/ci` pulled its step image with `acr-platform-ci`, a push token for `platform/*`.
- **Decision.**
  1. New pull-only integration `acr-platform-pull` (token `cf-platform-pull`, content and metadata read on `platform/*`) for every step image in `ci`, `release` and `preview`; `acr-platform-ci` is named only by `workorders/ci-image`.
  2. The step image is pinned by digest wherever it is used (`StepImage.CiDotnet`, the three pipelines), so a later push to its tag changes nothing.
  3. Every pipeline YAML loads from `main` of the environment repo, so only reviewed YAML names an integration (ADR-D18).
- **Consequences.** Four tokens to rotate. Accepted residual risk: a Codefresh account administrator or a reviewed change could still name a push integration; integration-level access control is [VERIFY].

#### ADR-IR20 Kubernetes agent auto-upgrader roles — Decided (amends ADR-D14)

- **Context.** With default values the `kubernetes-agent` chart binds a cluster-wide `*` ClusterRole to the auto-upgrader (E49).
- **Decision.** `bootstrap.tf` sets `useNamespacedRoles = true` and `clusterRole.enabled = false`: only Roles in `octopus-worker-<env>` (E27).
- **Consequences.** Octopus-driven upgrades under namespaced roles are [VERIFY] in the phase-2 spike; the fallback is `upgrade_locked` with upgrades through `env-apply`.

#### ADR-IR26 Merge gate of the app repo — Decided (addendum to ADR-C6)

- **Context.** `clearmeasure-aisf-sample-apps/20260923-001` is a public fork of the origin. GitHub keeps a fork's workflows off, and the Actions API lists none (E44, F18), so `build-result` cannot run. Enabling Actions would also activate `deploy.yml` and the legacy publish jobs, whose secrets the copy lacks.
- **Options.** (a) Enable Actions with `build.yml` only, keeping `deploy.yml` disabled; (b) require `codefresh/ci` from day one.
- **Decision.** (b). Branch protection on `master` requires a pull request, one review and `codefresh/ci`. GitHub Actions stays disabled in the app repo (R21).
- **Trade-offs accepted.**
  - Pull requests from forks of `20260923-001` never trigger Codefresh (branch code would otherwise run with the CI context). A maintainer reviews such a pull request, then pushes the same commits to a branch of `20260923-001`, and `workorders/ci` posts `codefresh/ci` on that SHA [VERIFY that the status shows on the fork's pull request]. Contributors push branches to the repo itself.
  - ARM SQLite, Windows LocalDB and the ARM acceptance run are not gated for the copy. The platform ships `linux/amd64` images only; the origin keeps those jobs for the live path.
  - The ADR-C6 parity comparison and flip criteria do not apply to the copy; R14 is closed, and the P1 exit criteria change (§9).
- **Consequences.** Contributor guidance in the README: open pull requests against `20260923-001`, base `master`. A fork's pull requests default to the upstream repository, which receives no pull request from this work (user directive).

#### ADR-IR27 Commit statuses from Octopus — Decided (R16 revisited)

- **Context.** The app repo now sits in the same org as the environment repo. The stored Octopus Git credential stays restricted to the environment repo (R3).
- **Decision.** Octopus posts `platform/tdd` through a separate statuses-only GitHub App (`Commit statuses: write`, installed on `20260923-001` only). `report-commit-status` signs a short-lived app JWT with `GitHub.StatusAppPrivateKey` and exchanges it for a one-hour installation token narrowed to that repo and `statuses: write` (E48). `GitHub.StatusEnabled` stays `False` until the App exists (R16).
- **Rationale.** No org-wide or content-writing credential; the Git credential keeps one repo.
- **Consequences.** One private key to rotate yearly. The status informs; it never gates a merge.

#### ADR-IR28 Point-in-time restore without a pool reset — Decided (N1, amends §7.2)

- **Context.** `db-restore-pitr` reset connection pools through `/_diagnostics/reset-db-connections`, which WI-02 removes outside `tdd`. A pooled connection that survives the rename keeps writing to the renamed database, and Octopus cannot restart pods in app namespaces.
- **Decision.** The swap runs with the app stopped: one pull request sets `ui-server` and `worker` to zero replicas, the approver confirms that no pod runs before the swap, and reverting the pull request starts fresh pods. The pool-reset step is removed.
- **Consequences.** A restore is a full outage of the environment, which an incident needing it already is. Whether Azure SQL renames a database with open sessions no longer matters.

#### ADR-IR29 Human secret writers — Decided (N2, N8; amends §5.2, ADR-D9)

- **Context.** Seeding the OpenAI key, the platform tokens and the repo credential could run only through the break-glass group.
- **Decision.** Entra group `secret-writers` (security owner, platform engineers) with PIM-eligible Key Vault Secrets Officer on the cluster and environment resource groups (foundation `secret_writers_group_object_id`). Break-glass stays for incidents. The writer of every secret is recorded in §7.8 and in the contracts.
- **Consequences.** One more group for the Entra administrator (R24).

#### ADR-IR32 One Octopus credential: the Space Manager API key — Decided (user directive; amends ADR-D8, ADR-D13, ADR-IR16, ADR-IR17, §5.2, §7.2, §7.7)

- **Context.** User directive (binding): everything stays in the prototype space, and the only Octopus credential is the API key the user provided, belonging to the existing user `AISF-Service-Account`, a Space Manager of that space only. There is no System Manager, and no new API keys, users, service accounts or custom user roles can be created. The key holds `TeamCreate` and `TeamEdit` but not `UserEdit` or `UserRoleEdit`.
- **Decision.**
  - **Codefresh → Octopus.** Codefresh secret context `workorders-octopus` holds `OCTOPUS_URL`, `OCTOPUS_SPACE_ID` and `OCTOPUS_API_KEY`. It is attached only to `workorders/release`, where a fail-closed `octopus_preflight` step checks it before the push, build-information and release steps. `obtain-oidc-id-token`, `octopusdeploy-login`, `svc-codefresh-release` and the OIDC identity `codefresh-release-master` are removed. `workorders-release` keeps only the ACR token.
  - **Argo CD gateway.** The same key registers the gateway. It is stored in `<kv-workorders-platform-{cluster}>` as `octopus-gateway-registration-token` (ESO → Secret `octopus-gateway-registration`) from phase 1; `svc-argocd-gateway` is removed. Whether the chart accepts an API key in `serverAccessTokenSecretName` is [VERIFY] in the phase-2 spike.
  - **Octopus → Azure** is unchanged: the OIDC accounts need no Octopus user.
  - **Roles.** Built-in roles only: `CI Release Publishers` get Release Creator and Package Publisher and hold `AISF-Service-Account` (read by name through a data source). `UAT Approvers` and `Prod Approvers` get Project Deployer scoped to `uat` and `prod`. Every other team keeps its built-in roles. `octopus/terraform` creates no user, custom role or OIDC identity (check C15), so a Space Manager applies all of it.
  - **Separation of duties.** `sod-guard` stays. It also fails when the prod deployment was created, or the Prod go/no-go answered, as `AISF-Service-Account` (project variable `Platform.AutomationUsername`). The key can therefore create releases and TDD deployments but cannot complete a prod deployment on its own.
- **Risks.**
  1. One bearer key with Space Manager rights sits in Codefresh and, from phase 1, in two cluster vaults. Leaking it grants full control of the space: deployments, variables and runbooks, including `env-destroy` in `infra-nonprod`.
  2. Octopus's audit log attributes Codefresh and gateway actions to the same user.
  3. Approvers can create deployments in their environment and override the prod freeze.
  4. API keys do not expire unless an expiry is set.
- **Mitigations.**
  - The key reaches one pipeline only. `workorders/release` loads its YAML from `main` of the private environment repo and runs on the tainted release runtime (ADR-D17). TB14 fails any other file that names an Octopus key.
  - Vault access is through ESO (`ClusterSecretStore platform-keyvault` limited to two namespaces) and PIM-gated secret writers (ADR-IR29).
  - `sod-guard` and the freeze-override reason are recorded on every prod deployment.
  - The key is rotated every 90 days and on any suspicion, following `docs/runbooks/credential-rotation.md`: Codefresh and both vaults change in one window.
  - The prototype space holds only this platform.
- **Path back.** When a System Manager or a second key becomes available, restore the dedicated accounts in this order. The contracts keep the team names, so no process change is needed.
  1. Service account `svc-codefresh-release` with an OIDC identity and a narrow custom role; drop `OCTOPUS_API_KEY` from `workorders-octopus`.
  2. A separate registration token for the gateway.
  3. The custom approver role without `ProjectEdit` (the former ADR-IR17).
- **Dissent.** The octopus-architect and sre-security positions (ADR-D8, TB5) preferred no stored key; the directive overrides them.

#### ADR-IR30 Image policies evaluate at admission only — Decided (N4, amends ADR-D11)

- **Context.** Only `kyverno-admission-controller` is federated to `id-kyverno-<cluster>` for registry reads. A background scan by another controller would report signature failures that admission passes, polluting the P2 exit criterion.
- **Decision.** `evaluation.background.enabled: false` for `verify-release-signatures` and `audit-release-provenance`. Admission reports still record Audit results.
- **Consequences.** An image admitted before a policy change is re-checked at its next admission; every rollout re-admits.

## 3. Architecture

### 3.1 System context

```mermaid
flowchart LR
    dev(["Developers, AI factory, approvers"])
    subgraph GH["GitHub"]
        appRepo["App repo<br/>clearmeasure-aisf-sample-apps/20260923-001<br/>public fork, no platform files"]
        envRepo["Env repo, private<br/>clearmeasure-aisf-sample-apps/<br/>basic-environment-octopus-codefresh"]
    end
    subgraph CF["Codefresh"]
        cfSaas["Codefresh SaaS<br/>pipelines, triggers, contexts"]
        cfRunner["Hybrid runner<br/>runtimes cf-ci and cf-release<br/>on the runner AKS cluster"]
    end
    subgraph OCT["Octopus Cloud"]
        octo["Space: workorders, workorders-infrastructure<br/>lifecycles, approvals, runbooks, audit"]
    end
    sig["Sigstore<br/>Fulcio + Rekor"]
    subgraph AZ["Azure subscription"]
        entra["Entra ID<br/>UAMIs, federated credentials, groups"]
        acr["ACR<br/>workorders/*, platform/*"]
        kv["Key Vault<br/>per environment + platform"]
        sql["Azure SQL<br/>tdd, uat, prod"]
        mon["Monitoring<br/>App Insights, Log Analytics, alerts"]
        subgraph NP["AKS nonprod"]
            argoNp["Argo CD nonprod"]
            gwNp["Octopus Argo CD gateway"]
            wkNp["Octopus K8s workers<br/>k8s-tdd, k8s-uat"]
            appNp["workorders-tdd<br/>workorders-uat"]
        end
        subgraph PR["AKS prod"]
            argoPr["Argo CD prod"]
            gwPr["Octopus Argo CD gateway"]
            wkPr["Octopus K8s worker<br/>k8s-prod"]
            appPr["workorders-prod"]
        end
    end
    dev -->|"push, PR, review"| appRepo
    dev -->|"approve, deploy, runbooks"| octo
    appRepo -->|"webhook"| cfSaas
    cfRunner -->|"status codefresh/ci, required"| appRepo
    cfSaas --> cfRunner
    cfRunner -->|"push images, repo-scoped token"| acr
    cfRunner -->|"keyless sign, attest"| sig
    cfRunner -->|"OIDC: packages, build info, release"| octo
    envRepo -->|"pipeline YAML and scripts; env-checks webhook"| cfSaas
    octo -->|"pin commit: images newTag"| envRepo
    argoNp -->|"read"| envRepo
    argoPr -->|"read"| envRepo
    argoNp -->|"apply"| appNp
    argoPr -->|"apply"| appPr
    gwNp -->|"outbound gRPC"| octo
    gwPr -->|"outbound gRPC"| octo
    wkNp -->|"polling"| octo
    wkPr -->|"polling"| octo
    wkNp -->|"DbUp, acceptance tests"| sql
    wkPr -->|"DbUp"| sql
    octo -->|"OIDC accounts"| entra
    appNp -->|"workload identity"| sql
    appPr -->|"workload identity"| sql
    appNp -->|"ESO"| kv
    appPr -->|"ESO"| kv
    appNp -->|"telemetry"| mon
    appPr -->|"telemetry"| mon
    NP -->|"kubelet pull"| acr
    PR -->|"kubelet pull"| acr
```

The legacy path runs beside this system until phase 5 (§9), unchanged, from the origin `ClearMeasureLabs/bootcamp-palermo-workorders`: `build.yml` → `deploy.yml` → the legacy Octopus space → Container Apps. In the copy `20260923-001` GitHub Actions is disabled (ADR-IR26).

### 3.2 End-to-end sequence: commit to production

```mermaid
sequenceDiagram
    autonumber
    actor Dev as Developer
    participant GH as App repo
    participant CF as Codefresh
    participant ACR as ACR and Sigstore
    participant OCT as Octopus Deploy
    participant KW as K8s worker of the env
    participant KV as Key Vault and Azure SQL
    participant ENV as Env repo
    participant ARGO as Argo CD
    actor APR as Approvers
    Dev->>GH: push branch, open PR
    GH-->>CF: workorders/ci on the branch
    CF-->>GH: status codefresh/ci, the required check
    Dev->>GH: merge to master
    GH-->>CF: workorders/release on master
    CF->>CF: VERSION = 2.5.first-parent height, build.ps1 gates, Package-Everything
    CF->>ACR: push workorders/ui-server, worker, db-migrator:VERSION, sign, SBOM, lock tags
    CF->>OCT: OIDC login, push packages, build information, create release VERSION
    Note over OCT: Channel Default, lifecycle workorders-standard, TDD deploys automatically
    OCT->>KW: read-deployment-secrets with azure-oidc-deploy-tdd
    KW->>KV: read migrator and acceptance secrets
    OCT->>KW: migrate-database (DbUp update)
    KW->>KV: apply scripts to TDD database
    OCT->>ENV: commit images newTag in gitops/workorders/envs/tdd
    ARGO->>ENV: poll main, render overlay
    ARGO->>ARGO: auto-sync workorders-tdd, rolling update
    ARGO-->>OCT: gateway reports Synced and Healthy at the commit
    OCT->>KW: verify-version, smoke-test, acceptance-tests
    KW-->>OCT: TRX artifacts, pass or fail
    OCT-->>GH: commit status platform/tdd
    APR->>OCT: deploy to UAT
    OCT->>KW: secrets, migrate, pin envs/uat, verify, smoke
    APR->>OCT: UAT sign-off manual intervention
    APR->>OCT: deploy to Prod inside freeze rules
    APR->>OCT: prod go/no-go, separation-of-duties guard
    OCT->>KW: copy prod database, migrate
    OCT->>ENV: commit images newTag in gitops/workorders/envs/prod
    ARGO-->>OCT: prod gateway reports Synced and Healthy
    OCT->>KW: verify-version, smoke-test
    Note over OCT,ARGO: Failure: redeploy previous release, Octopus writes the older tags, Argo CD syncs
```

Handoffs are listed below in order. §7 gives the exact names and arguments.

1. **Branch push.**
   - Codefresh `workorders/ci` runs the Linux gates and posts `codefresh/ci`, the required status of the app repo (ADR-IR26).
   - GitHub Actions stays disabled in the app repo.
2. **Merge to master.**
   - Codefresh `workorders/release` mints `VERSION`.
   - It re-runs the gates, then runs `Package-Everything`.
   - It stages `built/` from the `ChurchBulletin.UI.<VERSION>.nupkg`.
   - It builds three images, signs and attests them, and locks their tags.
3. **Handoff to Octopus.**
   - Codefresh calls, in order: `obtain-oidc-id-token`, `octopusdeploy-login`, `octopusdeploy-push-package`, `octopusdeploy-push-build-information`, `octopusdeploy-create-release` (freestyle steps with a pinned CLI from phase 3, ADR-IR18).
   - Codefresh stops there; it never deploys or waits.
4. **TDD (automatic).** Octopus runs `read-deployment-secrets` → `migrate-database` → `update-argo-cd-image-tags` (healthy verification) → `verify-version` → `smoke-test` → `acceptance-tests` → `report-commit-status`.
5. **UAT (manual).**
   - The same steps without acceptance tests.
   - Then `uat-signoff`.
6. **Prod (manual, freeze-aware).** `prod-go-no-go` → `sod-guard` → `read-deployment-secrets` → `db-copy-pre-release` → `migrate-database` → `update-argo-cd-image-tags` → `verify-version` → `smoke-test`.
7. **Failure and rollback.**
   - A failed step fails the deployment.
   - Recovery is Octopus "redeploy previous release". Migration is a no-op, because the schema is forward-only; the older tags are committed; Argo CD syncs.

### 3.3 Logical environment topology

```mermaid
flowchart TB
    subgraph OSPACE["Octopus space placeholder octopus-space"]
        subgraph PWO["Project workorders"]
            chDef["Channel Default<br/>lifecycle workorders-standard<br/>tdd auto, uat manual, prod manual"]
            chHot["Channel Hotfix<br/>lifecycle workorders-hotfix<br/>uat, prod"]
        end
        subgraph PINF["Project workorders-infrastructure (runbooks)"]
            lcInf["Lifecycle workorders-infrastructure<br/>infra-nonprod, infra-prod"]
        end
        envTdd["Env tdd<br/>pool k8s-tdd"]
        envUat["Env uat<br/>pool k8s-uat"]
        envProd["Env prod<br/>pool k8s-prod"]
        envInp["Env infra-nonprod<br/>pool Hosted Ubuntu"]
        envIpr["Env infra-prod<br/>pool Hosted Ubuntu"]
    end
    subgraph CNP["Cluster aks-nonprod placeholder"]
        subgraph ANP["argocd: instance argocd-nonprod"]
            apTdd["Application workorders-tdd<br/>project workorders-nonprod"]
            apUat["Application workorders-uat<br/>project workorders-nonprod"]
            apSet["ApplicationSet workorders-previews<br/>phase 6, project workorders-previews"]
        end
        nsTdd["ns workorders-tdd"]
        nsUat["ns workorders-uat"]
        nsPr["ns workorders-pr-N"]
        nsWt["ns octopus-worker-tdd"]
        nsWu["ns octopus-worker-uat"]
        nsAddNp["ns external-secrets, kyverno,<br/>octopus-argocd-gateway"]
    end
    subgraph CPR["Cluster aks-prod placeholder"]
        subgraph APR["argocd: instance argocd-prod"]
            apProd["Application workorders-prod<br/>project workorders-prod"]
        end
        nsProd["ns workorders-prod"]
        nsWp["ns octopus-worker-prod"]
        nsAddPr["ns external-secrets, kyverno,<br/>octopus-argocd-gateway"]
    end
    chDef --> envTdd
    chDef --> envUat
    chDef --> envProd
    chHot --> envUat
    chHot --> envProd
    lcInf --> envInp
    lcInf --> envIpr
    envTdd -.->|"annotation environment tdd"| apTdd
    envUat -.->|"annotation environment uat"| apUat
    envProd -.->|"annotation environment prod"| apProd
    apTdd --> nsTdd
    apUat --> nsUat
    apSet --> nsPr
    apProd --> nsProd
    envTdd -.->|"worker pool"| nsWt
    envUat -.->|"worker pool"| nsWu
    envProd -.->|"worker pool"| nsWp
    envInp -.->|"Terraform environment layer"| CNP
    envIpr -.->|"Terraform environment layer"| CPR
```

The Codefresh runner cluster `<aks-cluster-context>` is outside this topology. It hosts runtimes `<cf-runtime-ci>` and `<cf-runtime-release>` and never runs workloads.

## 4. Responsibility matrix

Legend:
- **O** marks the owner. Every row has exactly one.
- **c** means the tool contributes.
- — means the tool must not act.

The "Azure platform" column covers Terraform-managed Azure resources and the in-cluster platform add-ons: ESO, Kyverno and the Gateway.

| Capability | GitHub | Codefresh | Octopus Deploy | Argo CD | Azure platform |
|---|---|---|---|---|---|
| Source control, review and merge approval | **O** | — | — | — | — |
| Required merge check of the app repo (`codefresh/ci`, ADR-IR26) | c (branch protection enforces it) | **O** | — | — | — |
| Legacy CI of the origin (ARM, Windows LocalDB; until P5) | **O** (GitHub Actions in the origin) | — | — | — | — |
| Build of record, version minting, image and package publication | — | **O** | c (built-in feed receives packages) | — | c (ACR stores images) |
| SBOM, signature and provenance | — | **O** | — | — | c (Kyverno verifies) |
| Environment-repo validation CI | — | **O** `platform-env/env-checks` | — | — | — |
| Registry and tag immutability | — | c (locks tags at publish) | — | — | **O** (ACR) |
| Release record (snapshot, build info, notes) | — | c (creates the release) | **O** | — | — |
| Environment promotion (lifecycles, channels, freezes) | — | — | **O** | — | — |
| Human approvals and separation of duties | c (PR review of configuration) | — | **O** | — | — |
| Environment pin write (`images[].newTag`) | — | — | **O** | — | — |
| Desired-state authoring (manifests, config, AppProjects) | **O** (env-repo PRs, CODEOWNERS) | — | — | — | — |
| Kubernetes reconciliation and drift correction | — | — | c (Live Object Status, drift view) | **O** | — |
| Workload rollout strategy | — | — | c (verifies health) | **O** | — |
| Admission policy | — | — | — | c (delivers policies) | **O** (Kyverno) |
| Schema migration, named environments | — | c (packages DbUp) | **O** | — | — |
| Schema and seed, previews (phase 6) | — | c (migrator image) | — | **O** | — |
| Post-deploy verification (version, smoke, TDD acceptance) | — | — | **O** | c (health) | — |
| Rollback | — | — | **O** (redeploy previous release) | c (syncs) | — |
| Day-2 runbooks (backup, PITR, rotation) | — | — | **O** | — | — |
| Environment-layer IaC execution | — | c (credential-free checks) | **O** (runbooks) | — | c (Terraform code) |
| Privileged foundation (role assignments, locks, policy) | — | — | — | — | **O** (human Owner) |
| Runtime secrets (Key Vault → ESO → Secret) | — | — | — | c (applies `ExternalSecret`) | **O** |
| Workload identity to Azure | — | — | — | — | **O** |
| PR preview environments (phase 6) | c (label) | c (preview images) | — | **O** | — |
| Telemetry and SLO alerts | — | — | — | — | **O** (App Insights, Log Analytics) |
| Deployment audit trail and DORA metrics | — | c (build history) | **O** | c (sync history) | — |

## 5. Identities, secrets and trust boundaries

### 5.1 Trust boundaries

| # | Crossing | Credential | Controls |
|---|---|---|---|
| TB1 | Public app repo `20260923-001` → Codefresh SaaS | Stored Git integration `github-aisf-sample-apps` (read, trigger, commit status) | Push triggers fire only for same-repo branches; fork events are off. Pipeline YAML and scripts come from `main` of the environment repo (ADR-D18). Branch code never receives release contexts. |
| TB2 | Codefresh SaaS → runner cluster | Runner registration (existing) | Two runtimes: `<cf-runtime-ci>` with no identity, and `<cf-runtime-release>` on a tainted pool. |
| TB3 | Runner → ACR | Repository-scoped ACR tokens (registry integrations; the release token also in a secret context) | Push is scoped to `workorders/*`, `workorders-previews/*` or `platform/*`; step images are pulled with a pull-only token and pinned by digest (ADR-IR19). Tokens expire in 90 days or less. Tags are locked. |
| TB4 | Runner → Sigstore | Codefresh OIDC (audience `sigstore`) | The signer identity is the Fulcio SAN of `workorders/release`. |
| TB5 | Codefresh → Octopus | Space Manager API key of `AISF-Service-Account` in secret context `workorders-octopus` (ADR-IR32) | Attached to `workorders/release` only (YAML from `main`, release runtime); TB14 bans the key elsewhere. The key could deploy anywhere in the space; `sod-guard` refuses prod deployments created or approved as this user. Rotated every 90 days. |
| TB6 | Octopus → env repo | Stored Git credential `GitHub clearmeasure-aisf-sample-apps`, restricted to this repo (R3) | Direct pushes to `main` are limited to the pin files; config-as-code edits go to branches. Also: ruleset bypass by team, push ruleset on `.octopus/**`, bot-path audit, drift detection. |
| TB7 | Env repo → Argo CD | Read-only GitHub App or read-only token (new) | Never the stored PAT. |
| TB8 | Argo CD → cluster API | Argo CD controller | AppProject destination and kind allow-lists. Impersonation is Deferred to phase-4 hardening (beta). |
| TB9 | Pods → Azure | AKS workload identity (UAMI per workload per environment) | Exact-subject federated credentials. No secrets in pods except ESO-synced app keys. |
| TB10 | Gateway → Octopus | Outbound gRPC; registration token (ESO) | Argo CD account `octopus` is read-only (`applications get`, `logs get`, `clusters get`). |
| TB11 | Octopus workers → Octopus / SQL / Key Vault | Worker polling certificate; Octopus Azure OIDC accounts | One pool per environment. A worker can modify only its own namespace. |
| TB12 | Octopus → Azure | OIDC accounts `azure-oidc-deploy-<env>` and `azure-oidc-env-lifecycle-<class>` | Each account is scoped to its environment(s). Subjects are exact. |
| TB13 | Runbooks → subscription | Stored `Azure Runtime Provisioner` (interim, `infra-nonprod` only) → `id-env-lifecycle-*` | Plan, then a manual intervention, then apply. Owner locks. No prod destroy. |
| TB14 | Octopus → app repo (commit statuses) | Statuses-only GitHub App (ADR-IR27) | One-hour installation token narrowed to `20260923-001` and `statuses: write`; the private key is an Octopus sensitive variable. |

### 5.2 Identity inventory

| Identity | Kind | Used by | Permissions | Credential location | Recommended end state |
|---|---|---|---|---|---|
| Azure Owner or User Access Administrator | Human (PIM) | `terraform/foundation` apply; locks | Owner on the subscription (just-in-time) | Entra MFA | PIM with approval |
| Entra administrator | Human | Entra groups, the Argo CD SSO app registration, admin consent | Groups and Application Administrator | Entra MFA | PIM |
| Platform engineer | Human | `octopus/terraform` applies; sensitive Octopus variables | Space Manager in `<octopus-space>` | Octopus SSO | Unchanged |
| Secret writers | Entra group `secret-writers` (security owner, platform engineers) | Seeding and rotating Key Vault secrets (ADR-IR29) | PIM-eligible Key Vault Secrets Officer on the cluster and environment resource groups | Entra MFA | Unchanged |
| **`Azure Runtime Provisioner`** (stored by the user) | Service principal with a client secret | `workorders-infrastructure` runbooks in `infra-nonprod`, phases 1–2 | Contributor at subscription scope. The foundation adds AKS RBAC Cluster Admin on `rg-workorders-aks-nonprod`, Key Vault Secrets Officer on the nonprod environment resource groups, Storage Blob Data Contributor on the Terraform state container, and SQL admin group membership. | Octopus account `Azure Runtime Provisioner` plus variable set `Azure Runtime Provisioning`; Codefresh context `azure-runtime-provisioner` | Replaced by `id-env-lifecycle-*`; secret deleted everywhere (ADR-C10) |
| **GitHub fine-grained PAT, org `clearmeasure-aisf-sample-apps`** (stored by the user) | Token | Octopus config-as-code and pin commits; Codefresh triggers, clones and statuses for both repos | Contents read/write on the org's repositories; the Octopus Git credential that holds it is restricted to the environment repo (R3) | Octopus Git credential `GitHub clearmeasure-aisf-sample-apps`; Octopus variable set `GitHub AISF Sample Apps`; Codefresh Git integration `github-aisf-sample-apps`; Codefresh context `github-aisf-sample-apps-token` | GitHub App or machine user limited to this repo; 90-day expiry. The variable set and the context are used by nothing. |
| **`AISF-Service-Account`** (existing user, stored key; ADR-IR32) | Octopus user with an API key | Codefresh `workorders/release` (packages, build information, releases); Argo CD gateway registration; the phase 0 preview apply | Space Manager of the prototype space only; member of `CI Release Publishers` (Release Creator, Package Publisher) to document the narrow need | Codefresh secret context `workorders-octopus` (`OCTOPUS_API_KEY`); `octopus-gateway-registration-token` in `<kv-workorders-platform-<cluster>>` from phase 1 | Dedicated service accounts with OIDC and a separate gateway token (ADR-IR32 path back); until then, rotation every 90 days |
| `azure-oidc-deploy-{tdd,uat,prod}` → UAMI `id-octopus-deploy-{env}` | Octopus Azure OIDC account | `workorders` deployment steps and runbooks | Key Vault Secrets User and Reader on `rg-workorders-{env}`. UAT and prod also get SQL DB Contributor on `rg-workorders-{env}` (pre-release copy, backup, PITR; ADR-IR13). | None (federated credential with subject `space:<space-slug>:project:workorders:environment:<env>`) | Unchanged |
| `azure-oidc-env-lifecycle-{nonprod,prod}` → UAMI `id-env-lifecycle-{class}` | Octopus Azure OIDC account | `workorders-infrastructure` runbooks | Contributor on `rg-workorders-aks-{class}` and on that class's environment resource groups; AKS RBAC Cluster Admin on the cluster resource group; Key Vault Secrets Officer on the environment resource groups; Storage Blob Data Contributor on the state container; member of `<sql-admins-{class}>` | None (federated credential with subject `space:<space-slug>:project:workorders-infrastructure:environment:infra-{class}`) | Sole provisioning identity |
| ACR feed identity, UAMI `id-octopus-acr-pull` | Octopus feed OIDC | Feed `acr-workorders` | AcrPull on ACR | None (subject `space:<space-slug>:feed:acr-workorders` [VERIFY format]) | Unchanged |
| ACR tokens `cf-workorders-release`, `cf-workorders-preview`, `cf-platform-ci` | Repository-scoped tokens | Codefresh registry integrations `acr-workorders-release`, `acr-workorders-preview`, `acr-platform-ci` | Content read and write, plus metadata write (tag lock [VERIFY]), on `workorders/*`, `workorders-previews/*` and `platform/*` respectively | Codefresh registry integrations (encrypted); `cf-workorders-release` also in the secret context `workorders-release` (ADR-IR10) | Runner workload identity, if proven |
| ACR token `cf-platform-pull` | Repository-scoped token, pull only | Codefresh registry integration `acr-platform-pull` (step images of ci, release, preview; ADR-IR19) | Content and metadata read on `platform/*` | Codefresh registry integration | Unchanged |
| Codefresh keyless signer | OIDC | `workorders/release`, `workorders/ci-image`, `workorders/preview` | Obtains Fulcio certificates | None | Unchanged |
| Codefresh context `workorders-ci` | Encrypted context | `workorders/ci`, `workorders/release` | `CI_SQL_SA_PASSWORD` (a throwaway for a service container) and a CI-only, low-budget OpenAI key | Codefresh | Unchanged |
| Codefresh context `workorders-release` | Encrypted context (secret) | `workorders/release` only | `ACR_REGISTRY`; `ACR_TOKEN_NAME`, `ACR_TOKEN_PASSWORD` for `supply_chain` (ADR-IR10) | Codefresh | A secret-store context read by one step [VERIFY] |
| Codefresh context `workorders-octopus` | Encrypted context (secret, ADR-IR32) | `workorders/release` only | `OCTOPUS_URL`, `OCTOPUS_SPACE_ID`, `OCTOPUS_API_KEY` (the `AISF-Service-Account` key) | Codefresh | Removed when the OIDC service account returns |
| AKS control-plane identity `id-aks-{class}-controlplane` | UAMI | AKS | Network Contributor on the cluster subnet; Managed Identity Operator on the kubelet identity | None | Unchanged |
| AKS kubelet identity `id-aks-{class}-kubelet` | UAMI | Node image pulls | AcrPull | None | Unchanged |
| `id-workorders-{env}-app` | UAMI (workload identity) | Service accounts `ui-server` and `worker` in `workorders-{env}` | Contained database user: `db_datareader`, `db_datawriter`; `CREATE TABLE` and `ALTER ON SCHEMA::nServiceBus` (interim, until WI-06) | None | Loses the DDL grants after WI-06 |
| `id-workorders-{env}-eso` | UAMI (workload identity) | Service account `workorders-eso` | Key Vault Secrets User on `rg-workorders-{env}` | None | Unchanged |
| `id-workorders-{env}-migrator` | UAMI (created in phase 4) | Worker script pods after WI-05 | Contained user in `db_ddladmin`, `db_datareader`, `db_datawriter` | None | Replaces `workorders_migrator` |
| `id-eso-platform-{cluster}` | UAMI (workload identity) | ESO controller (`ClusterSecretStore platform-keyvault`) | Key Vault Secrets User on `<kv-workorders-platform-{cluster}>` | None | Unchanged |
| `id-kyverno-{cluster}` | UAMI (workload identity) | Kyverno admission controller | AcrPull, to read signatures and attestations | None | Unchanged |
| Argo CD repo reader | GitHub App (contents: read) on the environment repo only (new, R11) | Argo CD | Read the environment repo | `<kv-workorders-platform-<cluster>>` `argocd-repo-read-credential` → ESO; bootstrap copy in the Octopus sensitive variable `ArgoCD.RepoReadCredential`, passed as `TF_VAR_argocd_repo_read_credential` (ADR-IR15) | GitHub App |
| Argo CD account `octopus` | Argo CD API token | Gateway | `applications get`, `logs get` on `workorders-*/*`; `clusters get` | `argocd-octopus-gateway-token` in the platform vault | Rotate every 90 days |
| Argo CD SSO | Entra app registration `<argocd-sso-app>` | People signing in to Argo CD | Group claims | Workload-identity federation preferred; fallback `argocd-sso-client-secret` in the platform vault | Federation only |
| Octopus worker registration | Bearer token | `env-apply` (one time per install) | Registers a worker in `k8s-<env>` | Octopus sensitive variable `Octopus.WorkerRegistrationToken` (`workorders-infrastructure`), passed as `TF_VAR_octopus_worker_registration_token`; write-only, never in plan or state | Short-lived; regenerate per install |
| SQL `workorders_migrator` | Contained database user, password (interim) | `migrate-database` | `db_ddladmin`, `db_datareader`, `db_datawriter` | Key Vault `workorders-sql-migrator-password` | Removed after WI-05 |
| SQL `workorders_acceptance` (TDD only) | Contained database user, password (interim) | `acceptance-tests` | `db_datareader`, `db_datawriter`, `VIEW DEFINITION` | Key Vault `workorders-sql-acceptance-password` (TDD vault only) | Workload identity after WI-05 |
| Azure OpenAI key | API key | ui-server, worker, acceptance tests | Model calls | Key Vault `workorders-ai-openai-apikey` → ESO | Keyless (`Azure.Identity` is a new package; needs approval) |
| API validation key | Shared key | ui-server | API-key middleware | Key Vault `workorders-api-validation-key` | Unchanged |
| GitHub status writer for `platform/tdd` | Statuses-only GitHub App (`Commit statuses: write`), installed on `20260923-001` only (R16, ADR-IR27) | `report-commit-status` | Commit statuses on `clearmeasure-aisf-sample-apps/20260923-001` | Octopus sensitive variable `GitHub.StatusAppPrivateKey`; app and installation IDs in `.octopus/workorders/variables.ocl` | Unchanged |
| Legacy: `OCTO_API_KEY`, `AZURE_CREDENTIALS`, legacy `AzureAccount` | Keys and secrets | The legacy path | Unchanged | GitHub secrets; legacy Octopus space | Deleted at decommission (phase 5) |

### 5.3 Rules for the stored credentials (the user's choice, respected)

| Stored object | Design use | Recommendation |
|---|---|---|
| Octopus account `Azure Runtime Provisioner` | Account variable `Azure.LifecycleAccount`, scoped to `infra-nonprod`, used by the `workorders-infrastructure` Terraform steps in phases 1–2 | Restricted to `infra-nonprod` (R4, applied 2026-09-24). Retire it at the phase-2 exit (ADR-C10). |
| Octopus variable set `Azure Runtime Provisioning` | Not included in any project | Scope its account variable to `infra-nonprod` (open, R4). Include the set only if a script needs the raw `AZURE_*` values, and then only in `workorders-infrastructure`. |
| Codefresh context `azure-runtime-provisioner` | Attached to no pipeline | Delete it after phase 2. |
| Octopus Git credential `GitHub clearmeasure-aisf-sample-apps` | Config-as-code for both projects; pin commits | Restricted to `https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh`, with and without `.git` (R3, applied 2026-09-24). Still open: back it with a machine user in team `platform-bots`, or a GitHub App, with a 90-day expiry. |
| Octopus variable set `GitHub AISF Sample Apps` | Not included in any project | Keep it for other sample apps, or delete it. |
| Codefresh Git integration `github-aisf-sample-apps` | Triggers, clones, `specTemplate` loads and commit statuses for every pipeline, in the app repo and the environment repo (directive F) | Keep it. Its token's rights on the app repo (webhooks, statuses) are [VERIFY]. |
| Codefresh context `github-aisf-sample-apps-token` | Attached to no pipeline | Delete it after phase 2. |

### 5.4 The role-assignment gap

Contributor cannot create role assignments, locks or policy assignments (E36). Every grant in §5.2 is therefore created in `terraform/foundation`, at resource-group scope where possible, so that resources created later inherit it. Federated credentials on UAMIs are ARM writes that Contributor can make (R1-P R39), so:
- The environment layer creates the workload federated credentials after each cluster exists.
- The foundation creates the Octopus-issuer federated credentials in advance.

A grant that must reach a resource created later (Key Vault, SQL server, AKS) is assigned at the resource-group scope in advance. Assignments go directly to managed identities, not through groups, because group membership for managed identities lags (R1-SRE §3). The one exception is the SQL Entra admin group.

## 6. Repository layouts

### 6.1 Environment repo: full tree and writers

Writers:
- **H**: people, through a pull request with CODEOWNERS review, merged to `main`.
- **O-pin**: Octopus, using the stored Git credential with a direct commit to `main`. It may change only `images[].newTag`.
- **O-branch**: Octopus UI edits of config-as-code, committed to non-`main` branches only and merged by H.

Readers:
- **R-argo**: Argo CD.
- **R-cf**: Codefresh: `env-checks`, and the `workorders/*` pipelines, which load their YAML and scripts from `main`.
- **R-oct**: Octopus.
- **R-tf**: the environment Terraform.

```text
basic-environment-octopus-codefresh/                       private; default branch main
├── README.md                                              H
├── CODEOWNERS                                             H (platform owners)
├── .gitleaks.toml                                         H (security owners)
├── .yamllint.yaml                                         H
├── contracts/platform-contracts.yaml                      H      single source of names for checks
├── design/                                                H
│   ├── platform-design.md                                 this document
│   └── debate/round-{1,2}-<role>.md                       debate record (existing)
├── docs/                                                  H
│   ├── bootstrap.md  tool-boundaries.md
│   ├── cutover-and-decommission.md  consistency-notes.md
│   ├── walkthroughs/0{1..5}-*.md
│   └── runbooks/{break-glass,rollback-and-forward-fix,database-restore-pitr,credential-rotation,slo-fast-burn}.md
├── scripts/checks/{tool-boundaries,consistency,validate-all}.sh   H      R-cf
├── codefresh/                                             H      R-cf
│   ├── pipelines/env-checks.yml
│   ├── specs/platform-env-checks.yml
│   ├── workorders/README.md, version.env
│   ├── workorders/pipelines/{ci,release,preview,ci-image}.yml
│   ├── workorders/specs/workorders-{ci,release,preview,ci-image}.yml
│   ├── workorders/scripts/{version,changed-paths,stage-built,buildinfo,gate,supply-chain}.sh
│   └── images/ci-dotnet/Dockerfile                        toolchain image platform/ci-dotnet
├── containers/workorders/{worker,db-migrator}/Dockerfile  H      R-cf   Worker and migrator images
├── .octopus/                                              H, O-branch     R-oct
│   ├── workorders/{schema_version,deployment_settings,deployment_process,variables}.ocl
│   ├── workorders/runbooks/{db-backup,db-restore-pitr,run-acceptance-tests}.ocl
│   ├── workorders-infrastructure/{schema_version,deployment_settings,deployment_process,variables}.ocl
│   └── workorders-infrastructure/runbooks/{env-plan,env-apply,env-destroy,rotate-sql-passwords,provisioner-credential-check}.ocl
├── octopus/terraform/*.tf, terraform.tfvars.example       H      applied by a Space Manager (ADR-IR32)
├── argocd/                                                H      R-argo, R-tf (bootstrap/)
│   ├── bootstrap/{values,root-app}-{nonprod,prod}.yaml
│   ├── clusters/nonprod/{namespaces,projects,platform-secrets}.yaml
│   ├── clusters/nonprod/addons/{argocd,external-secrets,octopus-argocd-gateway,kyverno}.yaml
│   ├── clusters/nonprod/apps/workorders-{tdd,uat}.yaml
│   ├── clusters/prod/{namespaces,projects,platform-secrets}.yaml
│   ├── clusters/prod/addons/{argocd,external-secrets,octopus-argocd-gateway,kyverno}.yaml
│   ├── clusters/prod/apps/workorders-prod.yaml
│   └── optional/{argo-rollouts,workorders-previews-appset}.yaml      phase 6, not under any root path
├── gitops/workorders/                                                  R-argo
│   ├── base/{kustomization,ui-server,worker,secrets,network}.yaml     H
│   ├── components/bluegreen/{kustomization,rollout}.yaml              H (phase 6)
│   ├── previews/{kustomization,database}.yaml                         H (phase 6)
│   └── envs/{tdd,uat,prod}/
│       ├── kustomization.yaml                                          O-pin for images[].newTag; H for anything else
│       └── config/kustomization.yaml                                   H
├── policies/                                              H (security owners)   R-argo
│   ├── kyverno/base/{kustomization,verify-release-signatures,workload-baseline}.yaml
│   ├── kyverno/overlays/{nonprod,prod}/kustomization.yaml
│   └── octopus/prod-deployment-guardrails.rego            inactive (ADR-C9)
└── terraform/                                             H
    ├── foundation/*.tf, foundation.tfvars.example         applied by a human Owner
    └── environment/*.tf, {nonprod,prod}.tfvars(.example)  R-oct: applied by workorders-infrastructure runbooks; .tfvars committed (ADR-IR14)
```

No identity other than H and O writes to the repo. Argo CD and Codefresh never write to it; Codefresh posts commit statuses only.

The application repo `clearmeasure-aisf-sample-apps/20260923-001` holds no file of this tree (§6.3).

### 6.2 Enforcing the write matrix

| Control | Applies when | Detail |
|---|---|---|
| Branch ruleset on `main` | Always | Requires a pull request, CODEOWNERS approval and the `codefresh/env-checks` status. The bypass list holds only team `platform-bots`, which contains the Octopus credential's machine user. |
| Push ruleset "restrict file paths" | Always: the repo is private (R2, E31) | Blocks `.octopus/**` edits on `main` by every actor except merged pull requests. The exact per-actor semantics are [VERIFY]. |
| Bot-path audit | Every push to `main` | `scripts/checks/tool-boundaries.sh --audit-bot-commits` fails and alerts when a commit by `platform-bots` changes anything other than the `newTag` lines of `gitops/workorders/envs/*/kustomization.yaml`. |
| CODEOWNERS | Always | `gitops/workorders/base/**` and `argocd/**` require platform owners. `policies/**`, `terraform/foundation/**` and `.gitleaks.toml` require security owners (ADR-IR4). Every other path defaults to platform owners. |
| App-repo branch protection | Always | `master` of `20260923-001` requires a pull request, one review and the status `codefresh/ci` (ADR-IR26). GitHub Actions stays disabled there (R21). |
| Drift detection | Always | Octopus Git drift detection and Argo CD self-heal surface out-of-band changes. |

### 6.3 The application repo

`clearmeasure-aisf-sample-apps/20260923-001` receives no file and no change to an existing file (ADR-D18). The platform reads it (Codefresh clones at the triggering commit) and writes only commit statuses (`codefresh/*` from Codefresh, `platform/tdd` from Octopus once R16 is done). What the user sets on it:
- Branch protection on `master`: pull request, one review, required status `codefresh/ci` (ADR-IR26).
- GitHub Actions stays disabled: the repo is a fork, and its copied workflows would otherwise run the legacy publish and deploy jobs (R21).
- The installation of the statuses-only GitHub App (R16, ADR-IR27).

Contributors open pull requests against `20260923-001`, base `master`. A fork's pull requests default to the upstream repository; the README says how to pick the base, and no pull request goes to `ClearMeasureLabs/bootcamp-palermo-workorders`.

## 7. Interface contracts

Every implementer uses these names exactly. `contracts/platform-contracts.yaml` repeats them in machine-readable form.

### 7.1 Placeholders and naming

| Placeholder | Meaning |
|---|---|
| `<AZURE_TENANT_ID>`, `<AZURE_SUBSCRIPTION_ID>`, `<azure-region>` | Azure tenant, subscription and region |
| `<acr-name>` | ACR name. The login server is `<acr-name>.azurecr.io`. |
| `<kv-workorders-tdd>`, `<kv-workorders-uat>`, `<kv-workorders-prod>` | Per-environment Key Vaults (names are globally unique) |
| `<kv-workorders-platform-nonprod>`, `<kv-workorders-platform-prod>` | Per-cluster platform Key Vaults |
| `<sql-workorders-{env}>`, `<sqldb-workorders-{env}>` | Per-environment SQL logical server and database |
| `<tfstate-storage-account>` | Terraform state storage account |
| `<tdd-hostname>`, `<uat-hostname>`, `<prod-hostname>` | Public hostnames |
| `<gateway-name>`, `<gateway-namespace>`, `<gateway-class>` | Gateway API parent reference (Q5) |
| `<OCTOPUS_URL>`, `<octopus-space>`, `<octopus-space-slug>`, `<octopus-space-id>` | Octopus Cloud URL and platform space |
| `<OCTOPUS_HOST>` | Host name of `<OCTOPUS_URL>`, for the gateway's gRPC address |
| `<CF_ACCOUNT_ID>`, `<cf-account-name>`, `<CF_RELEASE_PIPELINE_ID>`, `<CF_PREVIEW_PIPELINE_ID>` | Codefresh account, release pipeline and preview pipeline identifiers (Fulcio subjects) |
| `<cf-runtime-ci>`, `<cf-runtime-release>` | Codefresh runtimes |
| `<aks-cluster-context>` | The existing Codefresh runner cluster |
| `<ENV_REPO_URL>` | `https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh.git` |
| `<ci-image-version>`, `<ci-image-digest>` | Tag and digest of `platform/ci-dotnet`, pinned together in `StepImage.CiDotnet` and the Codefresh pipelines (ADR-IR11, ADR-IR19) |
| `<*-chart-version>`, `<*-version>`, `<*-digest>`, `<*-sha256>` | Pinned versions and digests: Helm charts (Argo CD app 3.5.3, ESO, Kyverno, the Octopus gateway, the Kubernetes agent, Argo Rollouts app 1.10.0), tool images, base images |
| `<client-id-of-<uami name>>` | Client ID of a user-assigned identity from the foundation outputs, for example `<client-id-of-id-workorders-tdd-app>` (ADR-IR7) |
| `<entra-group-object-id-<group>>` | Object ID of an Entra group, for example `<entra-group-object-id-prod-approvers>` |
| `<argocd-{cluster}-host>` | Host name of the Argo CD instance (`argocd-nonprod`, `argocd-prod`) |
| `<previews-hostname-suffix>` | DNS suffix of preview hosts `pr-<number>.<previews-hostname-suffix>` (phase 6) |
| `<platform-bots-author-regex>` | Author identity of the Octopus Git credential, for the bot-path audit (`PLATFORM_BOT_AUTHORS`) |
| `<provisioner-secret-expires-on>` | Expiry date of the stored provisioner secret (`Provisioner.SecretExpiresOn`) |
| `<github-status-app-id>`, `<github-status-app-installation-id>` | IDs of the statuses-only GitHub App (ADR-IR27) |

The contracts file also carries `notationTokens` (descriptive `<env>`-style tokens, never substituted), `placeholderPatterns` (the families above) and `promptedVariables` (ADR-IR12).

Resource groups: `rg-workorders-shared`, `rg-workorders-aks-nonprod`, `rg-workorders-aks-prod`, `rg-workorders-tdd`, `rg-workorders-uat`, `rg-workorders-prod`. Clusters: `aks-workorders-nonprod`, `aks-workorders-prod`. Log Analytics: `log-workorders`. App Insights: `appi-workorders-{env}`.

### 7.2 Octopus

**Space and projects**

| Object | Value |
|---|---|
| Space | `<octopus-space>`, slug `<octopus-space-slug>` |
| Project group | `Work Orders` |
| Project `workorders` | Slug `workorders`. Lifecycle `workorders-standard`. Untenanted. Version controlled with the Git credential `GitHub clearmeasure-aisf-sample-apps`, URL `<ENV_REPO_URL>`, base path `.octopus/workorders`, default branch `main`, protected branches `main`. Includes library variable set `WorkOrders Environment`. Connectivity: `allow_deployments_to_no_targets = true`. |
| Project `workorders-infrastructure` | Runbooks only. Lifecycle `workorders-infrastructure`. Base path `.octopus/workorders-infrastructure`, with the same repo and credential. Includes library variable set `WorkOrders Infrastructure`. |

**Environments, lifecycles and channels**

| Object | Value |
|---|---|
| Environments (in order) | `tdd`, `uat`, `prod`, `infra-nonprod`, `infra-prod` |
| Lifecycle `workorders-standard` | Phases `TDD` (`tdd`; automatic from phase 2, controlled by Terraform variable `tdd_auto_deploy`), `UAT` (`uat`, manual), `Prod` (`prod`, manual) |
| Lifecycle `workorders-hotfix` | Phases `UAT` (`uat`) and `Prod` (`prod`) |
| Lifecycle `workorders-infrastructure` | Phases `Infra Nonprod` (`infra-nonprod`, optional) and `Infra Prod` (`infra-prod`, optional) |
| Channel `Default` | Default channel. Lifecycle `workorders-standard`. Git reference rule `refs/heads/main`. Every package's version must have no pre-release tag (`tag = "^$"`). Releases are created only by Codefresh with the `AISF-Service-Account` key (ADR-IR32). |
| Channel `Hotfix` | Lifecycle `workorders-hotfix`. Git reference rule `refs/heads/main`. Release numbers look like `<package-version>-hotfix.<n>` and are created by `Release Managers`. |
| Deployment freeze | `prod-weekend-freeze`: a project deployment freeze on `prod` for `workorders`, recurring weekly from Saturday 00:00 to Monday 00:00 (`octopus/terraform/freezes.tf`; the window length per occurrence is [VERIFY]). `Release Managers` may override it and must give a reason; approvers cannot (ADR-IR17). |

**Deployment process steps** (project `workorders`, in order)

| # | Slug | Name | Type | Environments or channel | Pool, container, packages |
|---|---|---|---|---|---|
| 1 | `hotfix-justification` | Hotfix justification | Manual intervention (`Octopus.Manual`), team `Release Managers` | Channel `Hotfix` | — |
| 2 | `prod-go-no-go` | Prod go/no-go | Manual intervention, team `Prod Approvers` | `prod` | — |
| 3 | `sod-guard` | Separation-of-duties guard | Script (Bash). Fails when `Octopus.Action[Prod go/no-go].Output.Manual.ResponsibleUser.Id` equals `Octopus.Deployment.CreatedBy.Id`, or when the creator or the approver is `#{Platform.AutomationUsername}` (ADR-IR32) (output variables are keyed by step name, ADR-IR22) | `prod` | `#{WorkerPool}` |
| 4 | `read-deployment-secrets` | Read deployment secrets | Azure CLI script (Bash) with account `#{Azure.DeployAccount}`. Writes sensitive outputs `MigratorPassword`; in `tdd` also `AcceptancePassword` and `OpenAIKey`. | `tdd`, `uat`, `prod` | `#{WorkerPool}`, default worker-tools container |
| 5 | `db-copy-pre-release` | Copy prod database | Azure CLI script: `az sql db copy` to `#{Sql.Database}-pre-<release, with dots replaced>`, tagged `expires-on` = now + 14 days | `prod` | `#{WorkerPool}` |
| 6 | `migrate-database` | Migrate database (DbUp update) | Script (Bash). Runs `dotnet ClearMeasure.Bootcamp.Database.dll update #{Sql.ServerFqdn} #{Sql.Database} <scripts> #{Sql.MigratorUser} <MigratorPassword>`. One automatic retry; timeout `#{Migration.TimeoutSeconds}`. | `tdd`, `uat`, `prod` | `#{WorkerPool}`; container `#{StepImage.CiDotnet}`; package `ChurchBulletin.Database` (built-in feed, extracted) |
| 7 | `update-argo-cd-image-tags` | Update Argo CD image tags | "Update Argo CD Application Image Tags" (action type `Octopus.ArgoCDUpdateImageTags`, ADR-IR21; confirmed against an OCL export in the spike). Direct commit, Trigger sync off, verification "Argo CD Application is healthy", timeout `#{Argo.VerificationTimeoutSeconds}`, one automatic retry. | `tdd`, `uat`, `prod` | Packages `workorders/ui-server` and `workorders/worker` from feed `acr-workorders` (not acquired) |
| 8 | `verify-version` | Verify version | Script. The `version` field of `#{App.BaseUrl}/_version` must start with the ui-server package version. | `tdd`, `uat`, `prod` | `#{WorkerPool}` |
| 9 | `smoke-test` | Smoke test | Script. `#{App.BaseUrl}/_healthcheck` must return `Healthy`; `Degraded` fails when `Smoke.FailOnDegraded` is `True`. Retries for 150 s. | `tdd`, `uat`, `prod` | `#{WorkerPool}` |
| 10 | `acceptance-tests` | Acceptance tests (TDD only) | Script (PowerShell) with the interlocks from ADR-C11. `dotnet test` on the package DLL. TRX files uploaded with `New-OctopusArtifact`. Timeout 30 min. | `tdd` | `k8s-tdd`; container `#{StepImage.CiDotnet}`; package `ChurchBulletin.AcceptanceTests` |
| 11 | `uat-signoff` | UAT sign-off | Manual intervention, team `UAT Approvers` | `uat` | — |
| 12 | `report-commit-status` | Report platform/tdd status | Script. Run condition: always. Skipped unless `GitHub.StatusEnabled` is `True`. Reads the app SHA from the `app-commit:` line of the release notes and posts to `#{GitHub.AppRepository}` with a one-hour installation token of the statuses-only GitHub App (ADR-IR27). | `tdd` | `#{WorkerPool}` |

**Runbooks**

| Project | Runbook | Environments | Core steps |
|---|---|---|---|
| `workorders` | `db-backup` | `uat`, `prod` | `az sql db copy` or export to storage, using `#{Azure.DeployAccount}` (SQL DB Contributor in both, ADR-IR13) |
| `workorders` | `db-restore-pitr` | `uat`, `prod` | Prompted `RestorePointInTime` → restore to a new database → manual intervention (the app runs zero replicas) → swap names. No pool reset (ADR-IR28). |
| `workorders` | `run-acceptance-tests` | `tdd` | Same as step 10 |
| `workorders-infrastructure` | `env-plan` | `infra-nonprod`, `infra-prod` | "Plan to apply a Terraform template". Source: project Git repo, directory `terraform/environment`. Account `#{Azure.LifecycleAccount}`. Variable substitution in `.tf` files off (E29). Inputs: `#{Environment.Class}.tfvars` from Git (ADR-IR14); `TF_VAR_octopus_worker_registration_token` and `TF_VAR_argocd_repo_read_credential` from sensitive variables (ADR-IR1, ADR-IR15). Container `octopusdeploy/worker-tools:<worker-tools-version>` from feed `docker-hub`. The plan is saved as an artifact. |
| `workorders-infrastructure` | `env-apply` | `infra-nonprod`, `infra-prod` | Plan → manual intervention (always) → "Apply a Terraform template" → `configure-db-principals-<env>` on `k8s-<env>` (T-SQL, run as the lifecycle identity) |
| `workorders-infrastructure` | `env-destroy` | `infra-nonprod` only | Manual intervention → "Destroy Terraform resources" (removes resources inside resource groups, never the groups) |
| `workorders-infrastructure` | `rotate-sql-passwords` | `infra-nonprod`, `infra-prod` | New password → `ALTER USER` → Key Vault → verify; monthly trigger |
| `workorders-infrastructure` | `provisioner-credential-check` | `infra-nonprod` | Daily. Warns 14 days before `Provisioner.SecretExpiresOn`; runs `az login` smoke. |

**Variables**

| Name | Where | Type | Scope → value |
|---|---|---|---|
| `WorkerPool` | `.octopus/workorders/variables.ocl` | WorkerPool | `tdd`→`k8s-tdd`, `uat`→`k8s-uat`, `prod`→`k8s-prod` |
| `Azure.DeployAccount` | same | AzureAccount | `tdd`→`azure-oidc-deploy-tdd`, `uat`→`azure-oidc-deploy-uat`, `prod`→`azure-oidc-deploy-prod` |
| `StepImage.CiDotnet` | same, and `.octopus/workorders-infrastructure/variables.ocl` (ADR-IR11) | String | `<acr-name>.azurecr.io/platform/ci-dotnet:<ci-image-version>@sha256:<ci-image-digest>`, identical in both |
| `Smoke.FailOnDegraded` | same | String | `tdd`→`True`; `uat`,`prod`→`False` until #9016 closes |
| `Argo.VerificationTimeoutSeconds` / `Migration.TimeoutSeconds` | same | String | `900` / `900` |
| `Acceptance.AllowDestructiveReset` | same | String | `tdd`→`True` (no other scope) |
| `Platform.AutomationUsername` | same | String | `AISF-Service-Account` (ADR-IR32; read by `sod-guard`) |
| `GitHub.StatusEnabled`, `GitHub.StatusContext`, `GitHub.AppRepository` | same | String | `False` until R16; `platform/tdd`; `clearmeasure-aisf-sample-apps/20260923-001` |
| `GitHub.StatusAppId`, `GitHub.StatusAppInstallationId` | same | String | `<github-status-app-id>`, `<github-status-app-installation-id>` |
| `GitHub.StatusAppPrivateKey` | Octopus database | Sensitive | Private key of the statuses-only GitHub App (R16, ADR-IR27) |
| `App.BaseUrl` | Library set `WorkOrders Environment` (Terraform) | String | `https://<{env}-hostname>` |
| `App.InternalUrl` | same | String | `http://ui-server.workorders-{env}.svc.cluster.local:8080` |
| `Azure.ResourceGroup`, `KeyVault.Name` | same | String | `rg-workorders-{env}`, `<kv-workorders-{env}>` |
| `Sql.ServerName`, `Sql.ServerFqdn`, `Sql.Database` | same | String | `<sql-workorders-{env}>`, `<sql-workorders-{env}>.database.windows.net`, `<sqldb-workorders-{env}>` |
| `Sql.MigratorUser`, `Sql.AcceptanceUser` | same | String | `workorders_migrator`; `workorders_acceptance` (`tdd` only) |
| `AI.OpenAIUrl`, `AI.OpenAIModel` | same | String | `<azure-openai-endpoint>`, `<model-deployment-name>` |
| `Environment.Class` | Library set `WorkOrders Infrastructure` (Terraform) | String | `infra-nonprod`→`nonprod`, `infra-prod`→`prod` |
| `Terraform.StateResourceGroup`, `Terraform.StateStorageAccount`, `Terraform.StateContainer`, `Terraform.StateKey` | same | String | `rg-workorders-shared`, `<tfstate-storage-account>`, `tfstate`, `environment-{class}.tfstate` |
| `Azure.LifecycleAccount` | `.octopus/workorders-infrastructure/variables.ocl` | AzureAccount | `infra-nonprod`→`azure-runtime-provisioner` (phases 1–2), then `azure-oidc-env-lifecycle-nonprod`; `infra-prod`→`azure-oidc-env-lifecycle-prod` |
| `Provisioner.SecretExpiresOn` | same | String (ISO date) | `<provisioner-secret-expires-on>`, entered by a person |
| `Octopus.WorkerRegistrationToken` | Octopus database | Sensitive | Short-lived; entered before each `env-apply` that installs workers; passed as `TF_VAR_octopus_worker_registration_token` |
| `ArgoCD.RepoReadCredential` | Octopus database (`workorders-infrastructure`) | Sensitive | JSON credential of the read-only GitHub App (R11); passed as `TF_VAR_argocd_repo_read_credential` (ADR-IR15) |

Output variables are addressed by step name: `Octopus.Action[<step name>].Output.<variable>` (E46, ADR-IR22).

**Infrastructure and people**

| Object | Value |
|---|---|
| Worker pools | Static Kubernetes worker pools `k8s-tdd`, `k8s-uat`, `k8s-prod`; the built-in dynamic pool `Hosted Ubuntu` runs Terraform steps in container `octopusdeploy/worker-tools:<worker-tools-version>` |
| Accounts | Stored: `Azure Runtime Provisioner` (slug `azure-runtime-provisioner`). New: `azure-oidc-deploy-tdd`, `azure-oidc-deploy-uat`, `azure-oidc-deploy-prod`, `azure-oidc-env-lifecycle-nonprod`, `azure-oidc-env-lifecycle-prod`. Each is scoped to the matching environment. Execution subject keys: `space`, `project`, `environment`. Audience `api://AzureADTokenExchange`. |
| Feeds | Built-in: `ChurchBulletin.Database`, `ChurchBulletin.AcceptanceTests`. `acr-workorders`: Azure Container Registry feed at `https://<acr-name>.azurecr.io`, OIDC client `id-octopus-acr-pull`, subject keys `space`, `feed`. `docker-hub`: anonymous Docker Hub feed for the worker-tools container (ADR-IR6). |
| Git credential | Stored: `GitHub clearmeasure-aisf-sample-apps` |
| Library variable sets | Stored: `Azure Runtime Provisioning` and `GitHub AISF Sample Apps`, both included nowhere. New: `WorkOrders Environment` and `WorkOrders Infrastructure`, both Terraform-managed from untracked `terraform.tfvars`. |
| Automation user | None created. The existing user `AISF-Service-Account` (Space Manager) is read by name; its API key serves Codefresh (`workorders-octopus`) and the gateway (ADR-IR32). Project variable `Platform.AutomationUsername` names it for `sod-guard`. |
| Teams | Space teams with built-in roles only (ADR-IR32): `Platform Engineers` (Space Manager), `Release Managers` (Project Deployer and Release Creator; override the prod freeze with a reason), `UAT Approvers` and `Prod Approvers` (Project Deployer, scoped to `uat` and `prod`), `SRE On-call` (Runbook Consumer, Project Viewer), `Developers` (Project Viewer), `CI Release Publishers` (Release Creator and Package Publisher; holds `AISF-Service-Account`) |
| User roles | None custom (ADR-IR32). |
| Terraform state | `octopus/terraform` uses key `octopus-space.tfstate` in the shared state container (ADR-IR9) |
| Argo CD instances (gateway registration names) | `argocd-nonprod` (environments `tdd`, `uat`) and `argocd-prod` (environment `prod`) |

### 7.3 Argo CD

| Object | Instance | Definition |
|---|---|---|
| Root Application `platform-root` | Each | Created only by the bootstrap `argocd-apps` Helm release, with values from `argocd/bootstrap/root-app-{cluster}.yaml`. Project `platform-addons`. Source `<ENV_REPO_URL>`, `main`, path `argocd/clusters/{cluster}`, `directory.recurse: true`. Automated sync with prune and self-heal. No finalizer. |
| AppProject `platform-addons` | Each | The only project allowed cluster-scoped kinds. Sources: the environment repo and the pinned Helm repositories (argo, external-secrets, kyverno, and the Octopus OCI registry). Destinations: the platform namespaces. |
| AppProject `workorders-nonprod` | nonprod | Sources: the environment repo only. Destinations: `workorders-tdd`, `workorders-uat`. No cluster-scoped kinds. Namespaced allow-list: ConfigMap, Secret, Service, ServiceAccount, Deployment, Job, PodDisruptionBudget, NetworkPolicy, HTTPRoute, SecretStore, ExternalSecret, Rollout, AnalysisTemplate. Role `sre-oncall`: `get`, logs. |
| AppProject `workorders-prod` | prod | Same allow-list, with destination `workorders-prod`. Role `oncall`: `get`, logs, `action/argoproj.io/Rollout/abort`. |
| AppProject `workorders-previews` | nonprod | Destinations `workorders-pr-*`. Namespaced kinds only, plus a `Password` generator. No `SecretStore` that points at Azure. |
| Project `default` | Each | Locked: no sources, no destinations. |
| Applications `workorders-tdd`, `workorders-uat` | nonprod | Project `workorders-nonprod`. Source `<ENV_REPO_URL>`, `main`, path `gitops/workorders/envs/{env}`. Destination `https://kubernetes.default.svc`, namespace `workorders-{env}`. Annotations `argo.octopus.com/project: workorders` and `argo.octopus.com/environment: {env}`. Sync: automated prune and self-heal; `PruneLast=true`; retry limit 5, backoff 30 s ×2 up to 5 m. No finalizer. |
| Application `workorders-prod` | prod | The same shape, with project `workorders-prod`, path `gitops/workorders/envs/prod` and annotations `workorders` / `prod`. |
| Add-on Applications | Each | `argocd` (the instance manages itself with the bootstrap values), `external-secrets`, `octopus-argocd-gateway` (registration environments: nonprod `tdd`, `uat`; prod `prod`), `kyverno` plus `kyverno-policies` (path `policies/kyverno/overlays/{cluster}`). None carries Octopus annotations. |
| ApplicationSet `workorders-previews` | nonprod, phase 6 | Kept at `argocd/optional/workorders-previews-appset.yaml`. PR generator: owner `clearmeasure-aisf-sample-apps`, repo `20260923-001`, labels `[preview]`, `requeueAfterSeconds: 300`. Application name `workorders-pr-{{.number}}`. Project `workorders-previews` (fixed, never templated). Path `gitops/workorders/previews`. `kustomize.images` overrides `ui-server`, `worker` and `db-migrator` to `workorders-previews/*:pr-{{.number}}-{{.head_sha}}` (ADR-IR8). Automated sync, `CreateNamespace=true` (ADR-IR25), finalizer `resources-finalizer.argocd.argoproj.io`. |
| Configuration | Each | `timeout.reconciliation: 120s`. `admin.enabled: false` after bootstrap. SSO through Entra (`<argocd-sso-app>`). Local account `octopus` with capability `apiKey`. Policies: `p, octopus, applications, get, workorders-*/*, allow`; `p, octopus, logs, get, workorders-*/*, allow`; `p, octopus, clusters, get, *, allow`. Impersonation off until phase 4. |

### 7.4 Kubernetes namespaces

| Cluster | Namespace | Created by | Purpose |
|---|---|---|---|
| nonprod, prod | `argocd` | Terraform bootstrap | Argo CD |
| nonprod, prod | `external-secrets`, `kyverno`, `octopus-argocd-gateway` | `argocd/clusters/{cluster}/namespaces.yaml` | Add-ons |
| nonprod | `workorders-tdd`, `workorders-uat` | same | App environments (labels `environment: {env}`, `tier: app`) |
| prod | `workorders-prod` | same | App environment |
| nonprod | `octopus-worker-tdd`, `octopus-worker-uat` | Terraform `helm_release` | Octopus Kubernetes workers |
| prod | `octopus-worker-prod` | Terraform `helm_release` | Octopus Kubernetes worker |
| nonprod | `workorders-pr-<number>` | Argo CD (phase 6) | Previews |
| nonprod, prod | `argo-rollouts` | Argo CD (phase 6) | Rollouts controller |
| runner cluster | `<cf-runtime-ci>`, `<cf-runtime-release>` namespaces | Codefresh runner install | CI only |

Workload objects in `workorders-{env}`:

| Kind | Name |
|---|---|
| Deployments | `ui-server`, `worker` |
| Services | `ui-server` (port 8080 → 8080) |
| ServiceAccounts | `ui-server`, `worker`, `workorders-eso` |
| PodDisruptionBudget | `ui-server` |
| HTTPRoute | `ui-server` |
| SecretStore | `key-vault` |
| ExternalSecret and target Secret | `workorders-app` |
| ConfigMap | `workorders-config` (from a generator) |

### 7.5 Images, packages and versions

| Artifact | Name | Tag or version | Producer |
|---|---|---|---|
| UI image | `<acr-name>.azurecr.io/workorders/ui-server` | `<VERSION>` and `sha-<sha7>`, both locked; never `latest` | `workorders/release` (root `app:Dockerfile` over staged `built/`) |
| Worker image | `<acr-name>.azurecr.io/workorders/worker` | Same | `workorders/release` (`containers/workorders/worker/Dockerfile`, base `mcr.microsoft.com/dotnet/aspnet:10.0`, entrypoint `dotnet Worker.dll`) |
| Migrator image | `<acr-name>.azurecr.io/workorders/db-migrator` | Same | `workorders/release` (`containers/workorders/db-migrator/Dockerfile`, base `mcr.microsoft.com/dotnet/runtime:10.0`, entrypoint `dotnet ClearMeasure.Bootcamp.Database.dll`) |
| Preview images (phase 6) | `<acr-name>.azurecr.io/workorders-previews/{ui-server,worker,db-migrator}` | `pr-<number>-<40-hex head sha>` | `workorders/preview` (ADR-IR8) |
| CI toolchain image | `<acr-name>.azurecr.io/platform/ci-dotnet` | `<ci-image-version>` (date-based), signed; consumers pin `@sha256:<ci-image-digest>` | `workorders/ci-image` from `codefresh/images/ci-dotnet/Dockerfile` (SDK 10 with pwsh, Playwright 1.54 browsers, go-sqlcmd, az CLI) |
| Octopus packages (built-in feed) | `ChurchBulletin.Database`, `ChurchBulletin.AcceptanceTests` | `<VERSION>` | `workorders/release`. `ChurchBulletin.UI` and `ChurchBulletin.Script` are not pushed. |
| Octopus Docker package IDs (feed `acr-workorders`) | `workorders/ui-server`, `workorders/worker` (and `workorders/db-migrator` for build information only) | `<VERSION>` | — |
| Version | `2.5.<first-parent height>` on master; `2.5.<n>-ci.<sha7>` on branches; Hotfix releases `<package-version>-hotfix.<n>` | — | `codefresh/workorders/scripts/version.sh` with `codefresh/workorders/version.env` |
| OCI labels | `org.opencontainers.image.source=https://github.com/clearmeasure-aisf-sample-apps/20260923-001` (for `platform/ci-dotnet`: the environment repo), `org.opencontainers.image.revision=<sha>`, `org.opencontainers.image.version=<VERSION>` | — | All image builds |

### 7.6 Environment pin contract

| Environment | File Octopus writes | Fields Octopus writes | Value |
|---|---|---|---|
| `tdd` | `gitops/workorders/envs/tdd/kustomization.yaml` | `images[name=<acr-name>.azurecr.io/workorders/ui-server].newTag`, `images[name=<acr-name>.azurecr.io/workorders/worker].newTag` | The package version selected in the release (equal to the release number on channel `Default`) |
| `uat` | `gitops/workorders/envs/uat/kustomization.yaml` | Same two fields | Same |
| `prod` | `gitops/workorders/envs/prod/kustomization.yaml` | Same two fields | Same |

Required shape of each pin file. Nothing else may appear except `resources: [config]`.

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
resources:
  - config
images:
  - name: <acr-name>.azurecr.io/workorders/ui-server
    newTag: "0.0.0-bootstrap"   # written by Octopus only
  - name: <acr-name>.azurecr.io/workorders/worker
    newTag: "0.0.0-bootstrap"   # written by Octopus only
```

Base manifests reference `<acr-name>.azurecr.io/workorders/ui-server` and `<acr-name>.azurecr.io/workorders/worker` without a tag. Whether Octopus matches the full image name, including the registry, is [VERIFY].

### 7.7 Codefresh

| Object | Name | Contract |
|---|---|---|
| Projects | `workorders` (app), `platform-env` (environment repo) | — |
| Pipeline specs (all `workorders/*`) | Spec `codefresh/workorders/specs/workorders-<name>.yml`; YAML `codefresh/workorders/pipelines/<name>.yml` | `specTemplate`: repo `clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh`, `revision: main`, context `github-aisf-sample-apps` (ADR-D18). Clones: `main_clone` = the app repo at the triggering commit, full depth; `platform_clone` = the environment repo at `main`, for the scripts. |
| Pipeline `workorders/ci` | See above | **Trigger:** `branch-push` on `push.heads` of the app repo `clearmeasure-aisf-sample-apps/20260923-001`, every branch except `master`; branch regex `/^(?!master$).+/` [VERIFY lookahead]; fork events off. **Runtime:** `<cf-runtime-ci>`. **Contexts:** `workorders-ci`. **Status:** `codefresh/ci`, the required status of the app repo (ADR-IR26). A newer build cancels older builds of the same branch. |
| Pipeline `workorders/release` | See above | **Trigger:** `master-push` on `push.heads` of the app repo, `/^master$/`. **Runtime:** `<cf-runtime-release>`. **Contexts:** `workorders-ci`, `workorders-release`, `workorders-octopus` (ADR-IR32). **Registry:** `acr-workorders-release`. **Concurrency:** 1 (builds queue; never cancelled). **Status:** `codefresh/release`. |
| Pipeline `workorders/preview` (phase 6) | See above | **Trigger:** `pullrequest.opened`, `pullrequest.synchronize`, `pullrequest.labeled` [VERIFY] on the app repo; fork events off. The YAML exits unless the PR carries the `preview` label and comes from the same repo. Builds `ui-server`, `worker` and `db-migrator` preview images (ADR-IR8). **Runtime:** `<cf-runtime-ci>`. **Registry:** `acr-workorders-preview`. **Status:** `codefresh/preview`. |
| Pipeline `workorders/ci-image` | See above | **Triggers:** cron `0 6 * * 1`, and a push to `main` of the environment repo that modifies `codefresh/images/**`. **Runtime:** `<cf-runtime-release>`. **Registry:** `acr-platform-ci`. Signs keyless. |
| Pipeline `platform-env/env-checks` | Spec `codefresh/specs/platform-env-checks.yml`; YAML `codefresh/pipelines/env-checks.yml` (environment repo) | **Trigger:** `push.heads` on every branch of the environment repo, through Git integration `github-aisf-sample-apps`. **Runtime:** `<cf-runtime-ci>`. **Contexts:** none. **Status:** `codefresh/env-checks`. Runs `scripts/checks/validate-all.sh` sub-commands with pinned public tool images. |
| Contexts | `workorders-ci` (secret): `CI_SQL_SA_PASSWORD`, `AI_OPENAI_APIKEY`, `AI_OPENAI_URL`, `AI_OPENAI_MODEL`. `workorders-release` (secret): `ACR_REGISTRY=<acr-name>.azurecr.io`, `ACR_TOKEN_NAME=cf-workorders-release`, `ACR_TOKEN_PASSWORD` (ADR-IR10). `workorders-octopus` (secret, `workorders/release` only): `OCTOPUS_URL`, `OCTOPUS_SPACE_ID`, `OCTOPUS_API_KEY` (ADR-IR32). | Stored `azure-runtime-provisioner` and `github-aisf-sample-apps-token` are attached to none of these pipelines. |
| Registry integrations | `acr-workorders-release`, `acr-workorders-preview` and `acr-platform-ci` (push); `acr-platform-pull` (pull only, the step image of ci, release and preview; ADR-IR19) | Repository-scoped ACR tokens (§5.2). The step image is pinned by digest. |
| Git integrations | Stored `github-aisf-sample-apps`, which covers the org: app-repo triggers and clones, environment-repo triggers, clones and `specTemplate` loads, commit statuses | Read, trigger and status only |
| Exported variables | `VERSION`, `BUILD_BUILDNUMBER` (= `VERSION`), `CODE_CHANGED`, `IS_RELEASE` | — |
| Gate step names (ci and release) | `main_clone`, `prepare`, `build_sql` (with CRAP), `build_sqlite`, `code_analysis`, `qodana`, `security_scan` (advisory), `acceptance`, `gate` | `gate` fails when any gate other than `security_scan` did not succeed while `CODE_CHANGED=true` |
| Release-only steps | `package`, `stage_images`, `ui_image`, `worker_image`, `migrator_image`, `supply_chain`, `octopus_preflight`, `octopus_packages`, `octopus_build_info`, `octopus_release` | Before the phase-3 exit the three Octopus steps become freestyle steps with a pinned CLI; names and arguments stay (ADR-IR18) |
| Handoff arguments | See the list below this table. | — |
| Forbidden step types | `deploy`, `approval`, `helm`, `launch-composition`; `argocd` or `kubectl` commands against app clusters | Enforced by `tool-boundaries.sh` |

Handoff arguments, in order:
1. `octopus_preflight`: fails closed, with no network call, when `workorders-octopus` is missing or holds placeholders (ADR-IR32).
2. Every following step passes `OCTOPUS_API_KEY`, `OCTOPUS_URL` and `OCTOPUS_SPACE: ${{OCTOPUS_SPACE_ID}}`.
3. `octopusdeploy-push-package` with:
   - the two nupkgs;
   - `OVERWRITE_MODE: ignore`.
4. `octopusdeploy-push-build-information` with:
   - `PACKAGE_IDS: [workorders/ui-server, workorders/worker, workorders/db-migrator, ChurchBulletin.Database, ChurchBulletin.AcceptanceTests]`;
   - commits `HEAD^1..HEAD`;
   - `BuildUrl=${{CF_BUILD_URL}}`;
   - `OVERWRITE_MODE: overwrite`.
5. `octopusdeploy-create-release` 1.0.1 with:
   - `PROJECT: workorders` and `CHANNEL: Default`;
   - `RELEASE_NUMBER` and `PACKAGE_VERSION` set to `${{VERSION}}`;
   - `GIT_REF: refs/heads/main` and no `GIT_COMMIT`;
   - `IGNORE_EXISTING: true`;
   - `RELEASE_NOTES_FILE` pointing at the notes that `buildinfo.sh` writes, whose first line is `app-commit: <40-hex sha>` (`${{CF_REVISION}}`). `RELEASE_NOTES` is never passed: the step renders it unescaped into YAML (E18, ADR-IR23).

### 7.8 Key Vault, ESO and workload identity

| Vault | Secret name | Writer | Consumer | Mapped to |
|---|---|---|---|---|
| `<kv-workorders-{env}>` | `workorders-ai-openai-apikey` | `env-apply` writes an obvious stand-in; a secret writer sets the key (ADR-IR29) | ESO → Secret `workorders-app` | `AI_OpenAI_ApiKey` (ui-server, worker); Octopus `OpenAIKey` (TDD acceptance) |
| same | `workorders-api-validation-key` | `env-apply` (generated) | ESO | `ApiKeyAuthentication__ValidationKey` |
| same | `workorders-appinsights-connection-string` | `env-apply` | ESO | `ApplicationInsights__ConnectionString`, `APPLICATIONINSIGHTS_CONNECTION_STRING` |
| same | `workorders-sql-migrator-password` | `env-apply`, `rotate-sql-passwords` | Octopus `read-deployment-secrets` | DbUp password argument (until WI-05) |
| `<kv-workorders-tdd>` only | `workorders-sql-acceptance-password` | `env-apply`, `rotate-sql-passwords` | Octopus `read-deployment-secrets` in `tdd` | Acceptance connection string |
| `<kv-workorders-platform-{cluster}>` | `argocd-repo-read-credential` | Secret writers | ESO → Secret `argocd-repo-creds` (label `argocd.argoproj.io/secret-type: repo-creds`) | Argo CD read access to the environment repo |
| same | `argocd-octopus-gateway-token` | Secret writers | ESO → Secret `argocd-octopus-token`, key `token`, namespace `octopus-argocd-gateway` | Gateway → Argo CD |
| same | `octopus-gateway-registration-token` | Secret writers | ESO → Secret `octopus-gateway-registration`, key `token` | Gateway registration |
| same | `argocd-sso-client-secret` (only if federation is unavailable) | Secret writers | ESO → `argocd` | Entra SSO |

| ESO object | Namespace | Authentication |
|---|---|---|
| `SecretStore key-vault` (sync-wave `-2`) | `workorders-{env}` | `authType: WorkloadIdentity`, `serviceAccountRef: workorders-eso`, `vaultUrl: https://<kv-workorders-{env}>.vault.azure.net` |
| `ExternalSecret workorders-app` (sync-wave `-1`) | `workorders-{env}` | Refresh 1 h; target `workorders-app`, `creationPolicy: Owner` |
| `ClusterSecretStore platform-keyvault` | cluster-scoped; conditions limit it to namespaces `argocd` and `octopus-argocd-gateway` | Service account `external-secrets/external-secrets` → `id-eso-platform-{cluster}` |
| `Password` generator `mssql-sa` (phase 6) | `workorders-pr-<n>` | None (no Azure) |

| Federated credential subject (environment layer) | UAMI |
|---|---|
| `system:serviceaccount:workorders-{env}:ui-server`, `system:serviceaccount:workorders-{env}:worker` | `id-workorders-{env}-app` |
| `system:serviceaccount:workorders-{env}:workorders-eso` | `id-workorders-{env}-eso` |
| `system:serviceaccount:external-secrets:external-secrets` | `id-eso-platform-{cluster}` |
| `system:serviceaccount:kyverno:kyverno-admission-controller` | `id-kyverno-{cluster}` |

The issuer is the cluster's OIDC issuer URL and the audience is `api://AzureADTokenExchange`. Create federated credentials on one UAMI sequentially, because concurrent writes return 409 (R1-SRE §8).

### 7.9 Health endpoints, configuration keys, labels and annotations

| Endpoint | Use | Exposure in uat and prod |
|---|---|---|
| `/alive` (port 8080) | Startup, liveness and readiness probes | Allowed |
| `/_healthcheck` | Octopus `smoke-test` only; never a probe | Allowed |
| `/_version` | Octopus `verify-version` | Allowed |
| `/api/version` | Metadata | Allowed |
| `/_healthcheck/detailed`, `/_demo/*`, `/_diagnostics/*`, `/mcp` | Diagnostics and demos | HTTPRoute redirects them to `/`. Allowed in `tdd`. |
| `/ready` | Database-only readiness (WI-01) | Replaces `/alive` for readiness once it exists |
| Worker | No HTTP endpoint and no probes until WI-04 | — |

Writable paths under `readOnlyRootFilesystem: true`: `/tmp` and `/app/.diagnostics` (NServiceBus startup diagnostics, ADR-IR31), both `emptyDir`, on `ui-server` and `worker`.

| Configuration key (ConfigMap `workorders-config` unless noted) | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` (tdd, uat, prod, previews) [VERIFY parity with legacy] |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` |
| `ConnectionStrings__SqlConnectionString` | `Server=tcp:<sql-workorders-{env}>.database.windows.net,1433;Database=<sqldb-workorders-{env}>;Authentication=Active Directory Workload Identity;Encrypt=True;` It must start with `Server=` (F4). |
| `AI_OpenAI_Url`, `AI_OpenAI_Model` | `<azure-openai-endpoint>`, `<model-deployment-name>` |
| `RemotableBus__ApiUrl` (worker) | `http://ui-server.workorders-{env}.svc.cluster.local:8080/api/blazor-wasm-single-api` |
| `ApiKeyAuthentication__Enabled` | `false` until WI-10 |
| `AI_OpenAI_ApiKey`, `ApiKeyAuthentication__ValidationKey`, `ApplicationInsights__ConnectionString`, `APPLICATIONINSIGHTS_CONNECTION_STRING` | From Secret `workorders-app` |

| Label or annotation | Where |
|---|---|
| `app.kubernetes.io/name: ui-server` or `worker`; `app.kubernetes.io/part-of: workorders`; `app.kubernetes.io/component: web` or `message-handler` | All workload objects |
| `environment: {env}`, `tier: app` or `platform` | Namespaces |
| `argo.octopus.com/project: workorders`, `argo.octopus.com/environment: {env}` | Named-environment Applications only |
| `azure.workload.identity/client-id: <client-id-of-<uami name>>` | ServiceAccounts `ui-server`, `worker` (`id-workorders-{env}-app`) and `workorders-eso` (`id-workorders-{env}-eso`) |
| `azure.workload.identity/use: "true"` | Pod templates of `ui-server` and `worker` |
| `argocd.argoproj.io/sync-wave` | `-2` SecretStore, `-1` ExternalSecret, `0` workloads |

### 7.10 Azure resources and the Terraform layer contract

| Layer | State key | Applied by | Creates | Outputs consumed by the next layer |
|---|---|---|---|---|
| `terraform/foundation` | `foundation.tfstate` (bootstrap: local, then migrated) | Human Owner | Resource groups (§7.1); VNets `vnet-workorders-{class}` with subnets for AKS nodes and private endpoints; private DNS zones for SQL and Key Vault; ACR (Standard; Premium if private endpoints are required, Q6) with scope maps and tokens for the four Codefresh integrations (three push, one pull-only); PIM-eligible Key Vault Secrets Officer for `secret-writers` and break-glass; Log Analytics; state storage (shared key disabled); all UAMIs in §5.2; all role assignments; Octopus-issuer federated credentials; SQL admin Entra groups `<sql-admins-{class}>`; `CanNotDelete` locks on `rg-workorders-prod`, `rg-workorders-aks-prod` and the legacy resource groups (by ID); Azure Policy assignments (Key Vault RBAC model, SQL Entra-only audit, federated-credential issuer allow-list [preview]) | UAMI IDs and client IDs, subnet IDs, private DNS zone IDs, ACR ID, workspace ID, SQL admin group object IDs |
| `terraform/environment` | `environment-{class}.tfstate` | Octopus `env-apply`, with `{class}.tfvars` from Git (ADR-IR14) and two `TF_VAR_*` secrets | AKS (pre-created identities, workload identity, OIDC issuer, Azure RBAC, local accounts off; node pools `system`, `apps`); SQL servers and databases per environment (Entra admin = group; SQL authentication enabled until WI-05); Key Vaults (RBAC) plus written secrets; App Insights; SLO alert; workload federated credentials; `helm_release` for `argo-cd` and `argocd-apps` (values from `argocd/bootstrap/*`, `ignore_changes` after bootstrap); `helm_release` Octopus workers `octopus-worker-{env}`. **No `azurerm_role_assignment`.** | Cluster OIDC issuer (back into the foundation for Argo CD SSO), SQL FQDNs, vault URIs (copied into `envs/*/config` and `WorkOrders Environment` by pull request) |
| `octopus/terraform` | `octopus-space.tfstate` (ADR-IR9) | A Space Manager: a platform engineer, or the automation user for the phase 0 preview (ADR-IR32) | The Octopus objects of §7.2 that config-as-code does not store | Account, feed and project IDs for the portal steps |

Provider pins: `azurerm ~> 5.6`, `azuread ~> 3.9`, `helm ~> 3.3`, `kubernetes ~> 3.2`, `random ~> 3.9`, `OctopusDeploy/octopusdeploy` 1.20.0; Terraform 1.11 or later (ADR-IR5).

## 8. App-side prerequisites (proposed work items)

These work items are not implemented in the sketch. Each needs the app team's approval as a board item.

| ID | Title | Why | Acceptance criteria | Gate | Owner |
|---|---|---|---|---|---|
| WI-01 | Add a database-only readiness endpoint `/ready` | `/_healthcheck` includes the LLM and `NeedsReboot` checks (F2), so readiness cannot use it | `/ready` runs only checks tagged `ready` (DataAccess); unit and integration tests; mapped in every environment | Before phase 4 exit (prod cutover) | App team |
| WI-02 | Gate `/_demo/*` and `/_diagnostics/*` by environment or feature flag | An anonymous request can flip health or reset pools (F3) | Both routes return 404 unless `Diagnostics:DemoEndpointsEnabled=true`; that flag is enabled only in `tdd` | Phase 4 | App team |
| WI-03 | Authenticate `/mcp` outside Development and Testing | `/mcp` is unauthenticated in every environment (F3) | API-key or Entra authentication; acceptance MCP tests updated | Phase 4 | App team |
| WI-04 | Add a Worker liveness signal | The generic host has no endpoint (F5) | A minimal health listener or heartbeat file; the Deployment gains a probe | Before Worker enablement in prod | App team |
| WI-05 | Entra (workload identity) authentication in the DbUp console; password taken from an environment variable, not a positional argument | Only SQL or integrated authentication exists today, and the password appears in the process arguments (F9) | `--auth workload-identity` mode; `DB_PASSWORD` environment variable fallback; tests | Before phase 4 exit | App team |
| WI-06 | Move NServiceBus installers from runtime into the migration step | `EnableInstallers()` needs DDL rights for the app identity (F5; `ServerApplication.cs`) | Installers run from a deployment-time command; the app runs with DML only | Phase 5 (post-cutover hardening) | App team |
| WI-07 | Add a non-test seed command (roles, sample employees) and an explicit server-certificate trust option | Fresh databases have no roles, and the only seed is test code (F8, F10); the console refuses the preview's self-signed SQL Server (ADR-IR24) | A `ChurchBulletin.Database seed` command that is idempotent and never deletes; a trust switch, off by default | Phase 3 (UAT data) or phase 6 (previews) | App team |
| WI-08 | Make the acceptance suite's destructive reset opt-in | `ZDataLoader` wipes whatever database it targets (F8) | `LoadData()` refuses unless `AcceptanceTests:AllowDestructiveReset=true`; Octopus sets it in `tdd` only; local and CI runs keep working | Before phase 2 exit | App team |
| WI-09 | Add a realtime notification backplane | The in-process hub breaks with more than one replica (ADR-C3) | Notifications reach clients on any replica | Phase 6 (blue-green) | App team |
| WI-10 | Send the API key from the Worker's `RemotableBus` | With `ApiKeyAuthentication:Enabled=true`, Worker calls would fail | The header is configured from a secret; tested | Before API keys are enabled | App team |
| WI-11 | Generic CI detection in `build.ps1` (Codefresh) | Non-GitHub CI forces `/tmp/nuget-packages` (F11) | The NuGet cache path is honoured under Codefresh; `build.ps1` changes need approval | Phase 1 (optional) | App team and platform |
| WI-12 | Remove the `user.name` metric tag | PII and cardinality (F15) | The login counter has no user tag; tests updated | Phase 3 | App team |
| WI-13 | Pin the UI base image by digest in the root `Dockerfile` | `mcr.microsoft.com/dotnet/aspnet:10.0` floats; the platform copies the file verbatim | `FROM` carries `@sha256:`; a renovation process updates it | Phase 4 (low) | App team |

## 9. Phased roadmap with exit criteria

The legacy path stays live and untouched in every phase before phase 5. Each phase can be reversed by stopping the new path.

| Phase | Scope | Exit criteria (all required) |
|---|---|---|
| **P0 Design** (now) | This document; the implementation sketch; the integration review (done 2026-09-24, §2.5) | Every file in §11 exists. All validations pass or are recorded as "not run" with a reason. Gitleaks is clean. The user accepts or amends §10. |
| **P1 Foundation and CI** | This tree becomes the first commits on `main` of the environment repo. An Owner applies `terraform/foundation`. A platform engineer (Space Manager) applies `octopus/terraform` with `tdd_auto_deploy = false`, adopting the phase 0 preview state (ADR-IR32). Codefresh specs, contexts, registry integrations and runtimes are created. `codefresh/ci` becomes the required status of the app repo; `workorders/release` creates releases. `platform-env/env-checks` is active. | 10 consecutive master builds pass every gate; the first one's TRX totals match a local `build.ps1` run of the same commit. Each release is created exactly once (a rerun is a no-op). The build of record takes at most 1.2× the legacy origin's `build-linux` + publish. Images are signed, locked and verifiable with `cosign verify`. The only Octopus API key in use is the `AISF-Service-Account` key in `workorders-octopus` (ADR-IR32). |
| **P2 TDD on AKS** | `env-plan` then `env-apply` in `infra-nonprod`; bootstrap guide steps (Argo CD token → Key Vault; gateway); `envs/*/config` values by pull request; `tdd_auto_deploy = true`; WI-08 merged; Kyverno in Audit mode. **TDD spike:** every [VERIFY] item marked for phase 2. | At least 20 consecutive TDD releases, at least 90 % green (legacy baseline: 207 of 289, 72 %; R1-P §8). Median commit → verified TDD is no worse than legacy. Drills pass: a bad migration leaves the old version serving with no pin commit; drift self-heals; redeploy-previous completes in under 15 min. `platform/tdd` is reported (once the statuses-only GitHub App exists, R16). 14 days of Kyverno audit without false denies. `infra-nonprod` has switched to OIDC and the provisioner secret is retired (R4). |
| **P3 UAT on AKS and Worker** | UAT deploys with sign-off. The Worker is enabled in `tdd` and `uat`. SLO alerts go live. WI-07 or a UAT data copy (Q8). WI-12. The Octopus handoff moves to pinned freestyle steps (ADR-IR18). | Two UAT cycles approved in Octopus. UAT smoke is blocking. The Worker runs 14 days in UAT with no growth in error or dead-letter queues. Insights shows lead time. No floating image runs in `workorders/release`. |
| **P4 Prod cutover** | Owner locks confirmed. `env-apply` in `infra-prod` with `azure-oidc-env-lifecycle-prod`. WI-01, WI-02, WI-03, WI-05 merged. Kyverno Enforce in prod. Impersonation and egress hardening decided. Cutover rehearsed in UAT. Then, in a maintenance window: freeze legacy prod deploys; disable the legacy prod migration owner (approved `.github/**` or `.octopus/**` change in the origin, made by the user); copy the prod database; deploy the same commit; switch DNS to `<prod-hostname>`. Worker in prod stays at `replicas: 0` until product sign-off. | The rehearsal succeeded. A PITR drill restored in under the agreed RTO. 14 days of prod SLO within budget. Rollback to the legacy path remains possible until P5 starts. |
| **P5 Decommission the legacy path** | After 30 days with a change-failure rate no worse than legacy, and with approved changes by the user in the origin: disable `deploy.yml` and the legacy publish jobs; retire the legacy Octopus project and the origin's `.octopus/`; delete the Container Apps and legacy resource groups after data retention; delete the `OCTO_API_KEY` and `AZURE_CREDENTIALS` secrets; decide the AI Software Factory contract (Q9); apply WI-06. | No consumer of legacy artifacts remains. Secrets are deleted. The docs are updated. |
| **P6 Optional enhancements** | PR previews (ADR-C4, needs WI-07); prod blue-green (ADR-C3, needs WI-09 and at least two replicas); the PreSync schema guard (ADR-C2); Platform Hub (ADR-C9, license); Octopus Approvals at GA (ADR-D13); runner workload identity (ADR-D8) | Each item needs a measured need and its own entry criteria. |

## 10. Recommendations to the user

Status on 2026-09-24. **Done by the user**: applied by the user. **Done by Claude**: applied by the orchestrating session with the user's approval. **Needs the user**: an action only the user (or someone the user names) can take. **Decided**: settled in the design; nothing to do yet.

| # | Recommendation | One-line rationale | When | Status |
|---|---|---|---|---|
| R1 | Install the Claude GitHub App on `clearmeasure-aisf-sample-apps` and attach the environment repo. | The platform tree needs its home. | Now | Done by the user |
| R2 | Make the environment repo private. | Push rulesets that restrict file paths require it (E31), and it makes committed tfvars acceptable (ADR-IR14). | Before P1 | Done by the user |
| R3 | Narrow the Octopus Git credential `GitHub clearmeasure-aisf-sample-apps` to this repo; back it with a machine user in team `platform-bots` or a GitHub App, with a 90-day expiry. | The credential wrote to every repo in the org, and ruleset bypass cannot name individual users. | Before P1 | Done by Claude: restricted to the environment repo, with and without `.git`. Needs the user: the machine user or GitHub App, and the expiry |
| R4 | Restrict the Octopus account `Azure Runtime Provisioner` to `infra-nonprod`; switch to OIDC (`id-env-lifecycle-*`) at the P2 exit; then delete the client secret from Entra, Octopus and the Codefresh context `azure-runtime-provisioner`. | A subscription-wide Contributor bearer secret must not reach prod, and OIDC removes it with no loss of function. | Restrict now; retire at the P2 exit | Done by Claude: account restricted; `infra-nonprod` created by hand, so `octopus/terraform` imports it first (`docs/bootstrap.md` step 3). Needs the user: scope the account variable of `Azure Runtime Provisioning` to `infra-nonprod`; the retirement at the P2 exit |
| R5 | Keep the Codefresh contexts `azure-runtime-provisioner` and `github-aisf-sample-apps-token`, and the Octopus variable set `GitHub AISF Sample Apps`, attached to nothing; delete them after P2 unless other sample apps need them. | Anything attached is reachable from pipeline steps. | Now | Done by Claude: verified that nothing uses them |
| R6 | Arrange a human Owner or User Access Administrator (PIM) and an Entra administrator for `terraform/foundation`, the SQL admin groups, the Argo CD SSO app and the locks. | Contributor cannot create role assignments, locks or policies (E36). | P1 | Needs the user |
| R7 | Confirm the Octopus license tier. Enterprise is needed only for Platform Hub, ITSM, SIEM streaming and space-level Insights; list price $24,600/year for Cloud (E25). | The design runs without Enterprise features. | Before P1 | Needs the user |
| R8 | Do not buy the Codefresh ARM Enterprise runtime or Windows incubation for this app. | The platform ships `linux/amd64` images only; ARM and Windows CI stays with the legacy origin (ADR-IR26). | — | Decided |
| R9 | Create two Codefresh runtimes (`<cf-runtime-ci>`, `<cf-runtime-release>`) on separate node pools of the runner cluster, never on an app cluster. | Keeps branch builds from poisoning release builds (ADR-D17). | P1 | Needs the user |
| R10 | Create the four repository-scoped ACR tokens for Codefresh (release, preview, ci-image push, and pull-only `cf-platform-pull`) with at most 90-day expiry; do not reuse the existing default registry integration's credential. | Least privilege; Codefresh OIDC cannot federate to Entra (E15, E37; ADR-IR19). | P1 | Needs the user |
| R11 | Create a read-only GitHub App for Argo CD on the environment repo; store its JSON credential in `ArgoCD.RepoReadCredential` and in the platform vaults. | The repo is private and the stored PAT must not reach clusters (ADR-IR15). | P2 | Needs the user |
| R12 | Approve work items WI-01 to WI-13 (§8), especially WI-08 before the P2 exit and WI-01, 02, 03, 05 before P4. | Each item gates a phase; none blocks P1. | P1 | Needs the user |
| R13 | Approve, and make, the changes to `.github/**` and `.octopus/**` of the legacy origin for cutover (a single migration owner) and decommission. | The live path changes only by the user's hand; no agent writes to the origin. | P4, P5 | Needs the user |
| R14 | Decide the required-check end state of ADR-C6. | Superseded: `codefresh/ci` is required on the app repo from day one (ADR-IR26). | — | Closed |
| R15 | Provide separate low-budget Azure OpenAI keys for CI and TDD. | The CI context is reachable from branch code in the gates. | P1 | Needs the user |
| R16 | Create a GitHub App with only `Commit statuses: write`, install it on `20260923-001` only, and give its private key to Octopus (`GitHub.StatusAppPrivateKey`). | Octopus then posts `platform/tdd` without a broader credential; the Git credential keeps one repo (ADR-IR27). | P2 | Needs the user |
| R17 | Move the staged tree to the environment repo and remove it from the app repo. | Superseded by the approved layout (ADR-D18): nothing was ever committed to an app repo, and this tree becomes the first commits on `main`. | P1 | Closed |
| R18 | Budget and cost controls. Main drivers: two AKS clusters (nonprod Free tier, prod Standard tier), runner node pools, three Azure SQL databases (prod tier with 35-day PITR), Log Analytics ingestion, App Insights and ACR. Set budgets per resource group; cap only the nonprod workspace. Price it with the Azure calculator [UNVERIFIED amounts]. | AKS adds fixed cost that Container Apps did not have (R1-P §3). | Before P2 | Needs the user |
| R19 | Plan a separate prod subscription later. | Limits the Contributor blast radius (R1-SRE §7 R1). | After P4 | Needs the user (later) |
| R20 | Adopt Octopus Approvals (with "block approvals by the deployment creator") when it reaches GA. | It replaces the script-based separation-of-duties guard (E39). | P6 | Decided (later) |
| R21 | Keep GitHub Actions disabled in `20260923-001`. If it is ever enabled, disable `deploy.yml` first. | The copied workflows would run legacy publish and deploy jobs; `deploy.yml` can no longer run today (E44). | Now | Holds; needs the user to keep it so |
| R22 | Delete the old working branch in `ClearMeasureLabs/bootcamp-palermo-workorders`. | The session's proxy refused the deletion (HTTP 403); no agent writes to that repo. | Now | Needs the user |
| R23 | Rotate the `AISF-Service-Account` API key every 90 days, set an expiry on it, and restore dedicated accounts (OIDC service account for Codefresh, separate gateway token, narrow approver role) when a System Manager becomes available. | One Space Manager key now serves Codefresh and the gateway (ADR-IR32). | P1, then when possible | Needs the user |
| R24 | Create the Entra group `secret-writers` (security owner, platform engineers). | People seed and rotate secrets through PIM without using break-glass (ADR-IR29). | P1 | Needs the user |
| R25 | Decide where new application commits land during the parallel run: the copy `20260923-001` (recommended) or the origin, with a mirroring rule if both change. | The build that reaches prod comes from the copy; two diverging sources would make cutover guesswork. | Before P2 | Needs the user |
| R26 | Optionally ask GitHub Support to detach `20260923-001` from the fork network [VERIFY the process]. | A fork's pull requests default to the upstream, and a fork of a public repo cannot turn private. | Optional | Needs the user |
| R27 | Set branch protection on `master` of `20260923-001`: pull request, one review, required status `codefresh/ci` after its first report. | The merge gate of ADR-IR26. | P1 | Needs the user |

## 11. Work packages

### 11.0 Rules for every package

- **Scope.** Write only the files listed in the package. Every path is relative to the environment-repo root; the application repo receives no file (ADR-D18). No git operations and no calls to the Octopus, Codefresh, GitHub or Azure APIs. The packages were built in a local copy of the environment repo and reviewed together (§2.5); the paths below are final.
- **Content.** No secrets and no realistic-looking keys. Use the §7 placeholders exactly. Mark unverified product behaviour `[UNVERIFIED]` or `[VERIFY]` in comments. Writing style follows the repo: no "I", "we", "you"; terse.
- **Contracts.** §7 is binding. If a contract cannot be met, record the conflict in the package's final report. Do not invent a new name.
- **Tooling.** Install into the scratchpad; nothing is preinstalled.
  - `GOBIN=<scratchpad>/bin go install sigs.k8s.io/kustomize/kustomize/v5@latest` and `…github.com/yannh/kubeconform/cmd/kubeconform@latest` (the Go module proxy is reachable).
  - Terraform zip from `https://releases.hashicorp.com/terraform/`.
  - Helm from `https://get.helm.sh/` (only if needed).
  - `pip install yamllint shellcheck-py`.
  - Gitleaks is at `<scratchpad>/gitleaks` (v8.28.0: `gitleaks dir <path>`).
  - Mermaid: `node <scratchpad>/mmd/validate.mjs <file.md>`.
  - `terraform init -backend=false && terraform validate` may fail offline. `terraform fmt -check` is the minimum.
- **Report.** Each implementer's final report lists every file written, each validation command with its result, and any contract conflicts.

### 11.1 Package `codefresh-engineer` (21 files)

| # | Path | Must contain |
|---|---|---|
| 1 | `codefresh/workorders/README.md` | The pipelines, triggers, contexts, runtimes and registry integrations (§7.7); how to register the specs (`codefresh create pipeline -f`); the parity map from the GitHub jobs to the Codefresh steps; the boundary rules |
| 2 | `codefresh/workorders/version.env` | `MAJOR=2`, `MINOR=5` |
| 3 | `codefresh/workorders/pipelines/ci.yml` | `version: "1.0"`, `mode: parallel`, `fail_fast: false`. Steps as in §7.7: a full-depth clone; `prepare` runs `version.sh` and `changed-paths.sh` piped into `.github/scripts/detect-code-changes.sh --from-list -` (fail-open), plus a worktree per gate. `build_sql` runs `Build` with an `mssql` service container, `SQL_EXTERNAL=true`, `SQL_SERVER_HOST=mssql,1433`, then CRAP. Also `build_sqlite` (`Build -UseSqlite`), `code_analysis`, `qodana` (image pinned to the baseline tag, threshold 0), `security_scan` (advisory: NuGet vulnerable/deprecated, Gitleaks), `acceptance` (`Invoke-AcceptanceTests` with an `mssql` service), and `gate` (`gate.sh`). Symlink the NuGet cache (F11). No publish, no Octopus, no deploy. |
| 4 | `codefresh/workorders/pipelines/release.yml` | The same gates, followed by: `package` (`Package-Everything`); `stage_images` (`stage-built.sh`); `ui_image` (root `Dockerfile`, context staged `built/`, `cosign.sign: true`); `worker_image` and `migrator_image`; `supply_chain` (`supply-chain.sh`: SBOM and provenance attestations, tag lock); and the Octopus handoff exactly as in §7.7. Tags are `<VERSION>` and `sha-<sha7>`. A final hook never changes the build result. |
| 5 | `codefresh/workorders/pipelines/preview.yml` | Phase 6. Label and same-repo guard; builds `workorders-previews/{ui-server,worker,db-migrator}:pr-<n>-<CF_REVISION>` (ADR-IR8); signs; no Octopus. |
| 6 | `codefresh/workorders/pipelines/ci-image.yml` | Builds and signs `platform/ci-dotnet:<date-tag>` from file 17 |
| 7–10 | `codefresh/workorders/specs/workorders-{ci,release,preview,ci-image}.yml` | `kind: pipeline` specs: `metadata.name` `workorders/<name>`, `project: workorders`; triggers on the app repo `clearmeasure-aisf-sample-apps/20260923-001` (ci-image: this repo); runtime, contexts, `specTemplate` (this repo, path, `revision: main`, context `github-aisf-sample-apps`), concurrency, termination policy, all per §7.7. |
| 11 | `codefresh/workorders/scripts/version.sh` | Unshallows or fails. Prints `MAJOR.MINOR.<first-parent count>` on master and `-ci.<sha7>` elsewhere; runnable locally; guards patch ≤ 65534 |
| 12 | `codefresh/workorders/scripts/changed-paths.sh` | Master: `HEAD^1..HEAD`. Branches: `merge-base origin/master`. On error, emits a non-docs path so the build fails open. No `grep -q` under a pipe. |
| 13 | `codefresh/workorders/scripts/stage-built.sh` | Extracts `build/ChurchBulletin.UI.<V>.nupkg` into `built/`, mirroring the `build.yml` Publish Release Candidate step (F6). Publishes the Worker and Database console into separate staging directories for files 18 and 19. |
| 14 | `codefresh/workorders/scripts/buildinfo.sh` | Writes Octopus build-information JSON (`BuildEnvironment`, `BuildNumber`, `BuildUrl`, `VcsType`, `VcsRoot`, `VcsCommitNumber`, `Commits` for `HEAD^1..HEAD`) and the release-notes file (ADR-IR23) |
| 15 | `codefresh/workorders/scripts/gate.sh` | Aggregates step results with `build-result` semantics (docs-only → pass). `security_scan` is advisory. |
| 16 | `codefresh/workorders/scripts/supply-chain.sh` | Syft SBOM; `cosign attest` for the SBOM and a provenance predicate labelled step-authored; ACR tag lock via the token's data plane [VERIFY]. Takes digests as input. |
| 17 | `codefresh/images/ci-dotnet/Dockerfile` | Base `mcr.microsoft.com/dotnet/sdk:10.0` (pinned by digest placeholder); pwsh; Playwright 1.54 browsers and dependencies; go-sqlcmd; az CLI; non-root user |
| 18 | `containers/workorders/worker/Dockerfile` | `mcr.microsoft.com/dotnet/aspnet:10.0`; copies the pre-published Worker output; `USER $APP_UID`; `ENTRYPOINT ["dotnet","Worker.dll"]`; OCI labels |
| 19 | `containers/workorders/db-migrator/Dockerfile` | `mcr.microsoft.com/dotnet/runtime:10.0`; Database console plus `scripts/`; `ENTRYPOINT ["dotnet","ClearMeasure.Bootcamp.Database.dll"]` |
| 20 | `codefresh/pipelines/env-checks.yml` | Clones the environment repo through `github-aisf-sample-apps`. Steps with pinned public images call `scripts/checks/validate-all.sh <sub-command>` with each of `yaml`, `kustomize`, `kubeconform`, `terraform`, `boundaries`, `consistency`, `secrets`. No contexts. |
| 21 | `codefresh/specs/platform-env-checks.yml` | Spec for `platform-env/env-checks` (§7.7) |

- **Contracts:** §7.5, §7.7, §7.6 (image names), ADR-C6, C7, D8, D11, D17.
- **Validation:**
  - Every YAML file parses (`python3 -c 'import yaml,sys;list(yaml.safe_load_all(open(p)))'`).
  - `yamllint -c .yamllint.yaml` once the pragmatist's file exists; otherwise the default config.
  - `bash -n` and `shellcheck` on the scripts.
  - `version.sh` run against a full clone fixture, or a documented dry run, because the session's checkouts are shallow (F17).
  - Grep: no `type: deploy|approval|helm|launch-composition`, no `latest` tag, no `azure-runtime-provisioner` context attached.
  - Every spec's contexts and statuses match §7.7.
  - Gitleaks is clean.

### 11.2 Package `gitops-architect` (38 files)

| # | Path | Must contain |
|---|---|---|
| 1–2 | `argocd/bootstrap/values-{nonprod,prod}.yaml` | `argo/argo-cd` chart values (app 3.5.3): server not exposed publicly; Entra SSO placeholders; `timeout.reconciliation: 120s`; `accounts.octopus: apiKey`; RBAC policies from §7.3; `admin.enabled: false`; resource tracking by annotation; a note on self-management |
| 3–4 | `argocd/bootstrap/root-app-{nonprod,prod}.yaml` | `argocd-apps` chart values defining `platform-root` (§7.3) |
| 5, 13 | `argocd/clusters/{nonprod,prod}/namespaces.yaml` | Namespaces from §7.4 with labels (not the worker namespaces) |
| 6, 14 | `argocd/clusters/{nonprod,prod}/projects.yaml` | AppProjects from §7.3, including the locked `default` |
| 7, 15 | `argocd/clusters/{nonprod,prod}/platform-secrets.yaml` | `ClusterSecretStore platform-keyvault` and ExternalSecrets for the repo credential, the gateway token and the registration token (§7.8), with `SkipDryRunOnMissingResource=true` |
| 8–11 | `argocd/clusters/nonprod/addons/{argocd,external-secrets,octopus-argocd-gateway,kyverno}.yaml` | Add-on Applications with pinned chart versions. `kyverno.yaml` holds two Applications (engine; policies from `policies/kyverno/overlays/nonprod`). The gateway registers `tdd`, `uat` as `argocd-nonprod`, with existing-secret references. |
| 12 | `argocd/clusters/nonprod/apps/workorders-tdd.yaml` | Application per §7.3 |
| 16–19 | `argocd/clusters/prod/addons/{argocd,external-secrets,octopus-argocd-gateway,kyverno}.yaml` | As 8–11 for prod (`argocd-prod`, environment `prod`, policies `overlays/prod`) |
| 20 | `argocd/clusters/nonprod/apps/workorders-uat.yaml` | Application per §7.3 |
| 21 | `argocd/clusters/prod/apps/workorders-prod.yaml` | Application per §7.3 |
| 22 | `argocd/optional/argo-rollouts.yaml` | Phase-6 add-on (app 1.10.0) |
| 23 | `argocd/optional/workorders-previews-appset.yaml` | Phase-6 ApplicationSet per §7.3 and ADR-C4 |
| 24 | `gitops/workorders/base/kustomization.yaml` | Resources only. **No `images:`.** Common labels per §7.9. |
| 25 | `gitops/workorders/base/ui-server.yaml` | ServiceAccount, Deployment (1 replica, RollingUpdate `maxUnavailable: 0`, probes `/alive` on 8080, `envFrom` ConfigMap `workorders-config` and Secret `workorders-app`, workload identity label, non-root, resources), Service `ui-server:8080`, PodDisruptionBudget |
| 26 | `gitops/workorders/base/worker.yaml` | ServiceAccount; Deployment (`replicas: 0` default, `RemotableBus__ApiUrl`, workload identity, no probes until WI-04) |
| 27 | `gitops/workorders/base/secrets.yaml` | ServiceAccount `workorders-eso`, `SecretStore key-vault`, `ExternalSecret workorders-app` (§7.8, sync waves) |
| 28 | `gitops/workorders/base/network.yaml` | HTTPRoute `ui-server` (parentRef placeholders; hostname patched per environment); default-deny ingress NetworkPolicy plus the allows in ADR-D12 |
| 29–31 | `gitops/workorders/envs/{tdd,uat,prod}/kustomization.yaml` | Exactly the §7.6 shape |
| 32–34 | `gitops/workorders/envs/{tdd,uat,prod}/config/kustomization.yaml` | `resources: [../../../base]`; `namespace: workorders-{env}`; `configMapGenerator workorders-config` (§7.9); patches for service-account client IDs, the HTTPRoute hostname, Worker replicas (0 until enabled) and, for uat and prod, redirect rules for the denied paths |
| 35–36 | `gitops/workorders/components/bluegreen/{kustomization,rollout}.yaml` | `kind: Component`; Rollout with `workloadRef` to Deployment `ui-server`, preview Service, Job-provider `AnalysisTemplate smoke`, `autoPromotionEnabled: true`; referenced by no overlay |
| 37–38 | `gitops/workorders/previews/{kustomization,database}.yaml` | Preview overlay: SQL Server Deployment and Service, ESO `Password` generator, Sync-hook Job (wave `-1`) running `workorders/db-migrator` `rebuild` then `seed` (WI-07), resource quota, Worker enabled |

- **Contracts:** §7.3, §7.4, §7.6, §7.8, §7.9. Kyverno policy paths belong to sre-security. Bootstrap files are consumed by `terraform/environment/bootstrap.tf`.
- **Validation:**
  - Every YAML file parses.
  - `kustomize build` succeeds for `gitops/workorders/envs/{tdd,uat,prod}` and `gitops/workorders/previews`. Test the component in a temporary scratchpad overlay, not a committed file.
  - `kubeconform -strict -summary` on the rendered output, with the CRD catalog schema location and `-ignore-missing-schemas` for kinds without schemas.
  - Every Application `path` exists in the tree.
  - Octopus annotations appear only on the three named Applications.
  - No `syncWindows`, no Image Updater, no `latest`.
  - Gitleaks is clean.

### 11.3 Package `octopus-architect` (31 files)

| # | Path | Must contain |
|---|---|---|
| 1 | `.octopus/workorders/schema_version.ocl` | The schema version from a current Octopus export [VERIFY] |
| 2 | `.octopus/workorders/deployment_settings.ocl` | `connectivity_policy { allow_deployments_to_no_targets = true }`; release notes template that includes build information |
| 3 | `.octopus/workorders/deployment_process.ocl` | The twelve steps of §7.2 in order, with slugs, environment and channel scoping, pools, containers, packages, timeouts, retries and interlocks. The Argo step's action type is `Octopus.ArgoCDUpdateImageTags` (ADR-IR21). |
| 4 | `.octopus/workorders/variables.ocl` | The project variables from §7.2 (non-sensitive only) |
| 5–7 | `.octopus/workorders/runbooks/{db-backup,db-restore-pitr,run-acceptance-tests}.ocl` | Runbooks per §7.2 [VERIFY config-as-code runbook file layout] |
| 8–11 | `.octopus/workorders-infrastructure/{schema_version,deployment_settings,deployment_process,variables}.ocl` | Runbook-only project. The process has no steps. Variables include `Azure.LifecycleAccount` and `Provisioner.SecretExpiresOn`. |
| 12–16 | `.octopus/workorders-infrastructure/runbooks/{env-plan,env-apply,env-destroy,rotate-sql-passwords,provisioner-credential-check}.ocl` | Terraform plan, apply and destroy steps with source "project Git repository", directory `terraform/environment`, backend settings from `WorkOrders Infrastructure`, substitution off, manual interventions, `configure-db-principals-<env>` on `k8s-<env>` |
| 17–31 | `octopus/terraform/{versions,providers,variables,environments,lifecycles,projects,channels,feeds,accounts,worker-pools,library-variable-sets,teams,freezes,outputs}.tf`, `octopus/terraform/terraform.tfvars.example` | Provider `OctopusDeploy/octopusdeploy` pinned to `1.20.0`. Objects from §7.2: environments; lifecycles (`tdd_auto_deploy` variable); project group and projects (version control via `git_library_persistence_settings` with the stored credential's ID from a data source or variable); channels; feed `acr-workorders` (OIDC); OIDC accounts; worker pools; both new library variable sets from `terraform.tfvars`; teams, user role, service accounts and the OIDC identity; the prod freeze. The stored account and variable sets are looked up by name, never created. |

- **Contracts:** §7.2, §7.5, §7.6 (package IDs, feed), ADR-C2, C10, C11, D4, D7, D13.
- **Validation:**
  - Brace, quote and heredoc balance for every `.ocl` file, with a small scratchpad script.
  - Every step slug and variable name matches §7.2.
  - `terraform fmt -check -recursive`; `terraform init -backend=false && terraform validate` if the provider downloads.
  - Grep: no `kubectl`, `helm`, `Octopus.KubernetesDeploy*` steps against app namespaces; `Azure Runtime Provisioning` included in no project.
  - Gitleaks is clean.

### 11.4 Package `sre-security` (38 files)

| # | Path | Must contain |
|---|---|---|
| 1–15 | `terraform/foundation/{versions,providers,variables,resource-groups,network,registry,identities,role-assignments,federation,governance,observability,state,entra,outputs}.tf`, `terraform/foundation/foundation.tfvars.example` | Everything in the foundation row of §7.10. **Identities and grants:** every UAMI and grant in §5.2; the Octopus-issuer federated credentials (issuer `<OCTOPUS_URL>` without trailing slash, the exact subjects from §5.2). **Registry:** ACR scope maps, not token passwords. **Locks:** on prod and on legacy resources (IDs as variables). **Policies:** Key Vault RBAC model, SQL Entra-only audit. **Entra:** SQL admin groups and the Argo CD SSO app placeholder. **Terraform state:** shared key disabled. Providers `azurerm ~> 5.6`, `azuread ~> 3.9` (current majors, ADR-IR5). |
| 16–26 | `terraform/environment/{versions,providers,variables,aks,data-services,monitoring,workload-federation,bootstrap,outputs}.tf`, `terraform/environment/{nonprod,prod}.tfvars.example` | Everything in the environment row of §7.10. **AKS:** consumes the foundation outputs through variables. **Helm and Kubernetes providers:** authenticate with `kubelogin`, local accounts off. **`bootstrap.tf`:** reads `../../argocd/bootstrap/{values,root-app}-${var.cluster}.yaml`, installs the Octopus workers per environment, and `ignore_changes` covers the chart versions Octopus upgrades. **Monitoring:** the SLO alert (ADR-D15). **Must not contain `azurerm_role_assignment`.** |
| 27–29 | `policies/kyverno/base/{kustomization,verify-release-signatures,workload-baseline}.yaml` | `ImageValidatingPolicy` for `workorders/*` (Deployments, Jobs, Rollouts), with the keyless attestor issuer `https://oidc.codefresh.io` and subject `https://g.codefresh.io/<cf-account-name>/workorders/release:<CF_ACCOUNT_ID>/<CF_RELEASE_PIPELINE_ID>` (E33); no `mutateDigest`. Baseline: disallow `latest`, require probes and resources, disallow privileged pods in `workorders-*`. |
| 30–31 | `policies/kyverno/overlays/{nonprod,prod}/kustomization.yaml` | Nonprod runs in Audit and adds the preview-pipeline attestor. Prod runs in Enforce. |
| 32 | `policies/octopus/prod-deployment-guardrails.rego` | Inactive Platform Hub policy (ADR-C9): deployments to prod need an unskipped `prod-go-no-go`; `Release.GitRef` is `refs/heads/main`; scoped to deployments |
| 33–37 | `docs/runbooks/{break-glass,rollback-and-forward-fix,database-restore-pitr,credential-rotation,slo-fast-burn}.md` | Human procedures: roles, preconditions, steps, verification, audit evidence. `credential-rotation` covers the provisioner secret, the PAT, the ACR tokens, the SQL passwords and the Argo CD token. |
| 38 | `.gitleaks.toml` | Extends the default rules; the allow-list covers only `<…>` and `${…}` placeholders |

- **Contracts:** §5, §7.1, §7.8, §7.10, ADR-C10, D9, D10, D11, D12, D15.
- **Validation:**
  - `terraform fmt -check -recursive` on both layers; `validate` with `-backend=false` if the providers download.
  - `grep -rn azurerm_role_assignment terraform/environment` returns nothing.
  - `kustomize build policies/kyverno/overlays/{nonprod,prod}` succeeds; the output parses.
  - Every Markdown file has correct headings and fences.
  - `gitleaks dir . --config .gitleaks.toml` is clean.

### 11.5 Package `pragmatist` (16 files)

| # | Path | Must contain |
|---|---|---|
| 1 | `README.md` | Purpose; one verb per tool; the layout map (§6.1); the phase status table; links to the design, the bootstrap guide and the walkthroughs; contributor guidance for both repos |
| 2 | `CODEOWNERS` | The ownership in §6.2, using team placeholders (`@<org>/platform-owners`, `@<org>/security-owners`) |
| 3 | `.yamllint.yaml` | Config compatible with Kubernetes, Argo CD and Codefresh YAML (line length relaxed, document start optional) |
| 4 | `contracts/platform-contracts.yaml` | Machine-readable §7: environments, namespaces, Applications, AppProjects, images, package IDs, Octopus objects, pools, accounts, variable names, Key Vault secret names, statuses, pipelines |
| 5 | `scripts/checks/tool-boundaries.sh` | The deny rules from R1-P §8, extended: Codefresh step types; Image Updater and `syncWindows`; `latest` in desired state; `kubectl apply/set image/patch` in `.octopus`; `azurerm_role_assignment` in `terraform/environment`; Octopus annotations outside the named Applications; the `--audit-bot-commits` mode (§6.2); `codefresh/` and `containers/` are scanned like every other tree |
| 6 | `scripts/checks/consistency.sh` | Checks every file against `contracts/platform-contracts.yaml`: annotation slugs match Octopus environments; image names match Octopus package IDs and Kustomize `images[].name`; pin files have the exact shape; probe paths; `Server=` prefix; Key Vault names match the ExternalSecrets; no prod destroy runbook |
| 7 | `scripts/checks/validate-all.sh` | Sub-commands `all`, `yaml`, `kustomize`, `kubeconform`, `terraform`, `mermaid`, `boundaries`, `consistency`, `secrets`. Skips a missing tool with a warning locally; fails in CI (`CI=true`). |
| 8 | `docs/bootstrap.md` | Ordered bootstrap with the owner of each step. User actions R1–R6. Foundation, then Octopus Terraform, then Codefresh objects, then `env-apply`, then the Argo CD token into Key Vault, then the gateway, then the config pull requests, then enabling TDD auto-deploy. Prod through OIDC only. |
| 9 | `docs/tool-boundaries.md` | One verb per tool; consoles by role (R1-P §2); forbidden features and why |
| 10 | `docs/cutover-and-decommission.md` | Checklists with the exit criteria from §9 for P2–P5, rollback of each phase, and the single-migration-owner procedure |
| 11 | `docs/consistency-notes.md` | Cross-slice consistency notes, produced by running files 5–7 over the whole tree; every mismatch with its owner and state |
| 12–16 | `docs/walkthroughs/{01-follow-a-commit,02-schema-change,03-promotion-and-hotfix,04-drift-and-rollback,05-environment-lifecycle}.md` | Teachable walkthroughs (labs 18–22 in R1-P §8). Each has an offline variant that reads the environment repo and predicts every handoff. `02` covers schema and configuration expand/contract (ADR-D6). |

- **Contracts:** all of §7; ADR-D2, D6; §9.
- **Validation:**
  - `bash -n` and `shellcheck` on the scripts.
  - Run `validate-all.sh all` over the tree once the other packages exist; record the results in file 11.
  - Mermaid blocks, if any, pass the validator.
  - `yamllint` parses its own config.
  - Gitleaks is clean.

### 11.6 Coverage and overlap check

| Package | Files | Exclusive roots |
|---|---|---|
| codefresh-engineer | 21 | `codefresh/**`, `containers/**` |
| gitops-architect | 38 | `argocd/**`, `gitops/**` |
| octopus-architect | 31 | `.octopus/**`, `octopus/**` |
| sre-security | 38 | `terraform/**`, `policies/**`, `docs/runbooks/**`, `.gitleaks.toml` |
| pragmatist | 16 | `README.md`, `CODEOWNERS`, `.yamllint.yaml`, `contracts/**`, `scripts/**`, `docs/*.md`, `docs/walkthroughs/**` |
| chief-architect | 1 | `design/platform-design.md` (existing: `design/debate/**`) |

The roots are disjoint, so no file can appear in two packages. Together they cover every path in §6.1: 144 implementation files plus this document. The integration review edited files across packages (§2.5); ownership is unchanged, except that `policies/**` is approved by security owners alone (ADR-IR4).

**Cross-package interfaces:**
- `terraform/environment/bootstrap.tf` (sre-security) reads `argocd/bootstrap/*` (gitops-architect).
- `argocd/clusters/*/addons/kyverno.yaml` (gitops-architect) points at `policies/kyverno/overlays/*` (sre-security).
- `codefresh/pipelines/env-checks.yml` (codefresh-engineer) calls `scripts/checks/validate-all.sh` (pragmatist).
- The Octopus runbooks (octopus-architect) run `terraform/environment` (sre-security).
- The pin files (gitops-architect) are written by the Octopus step defined by octopus-architect.

## 12. Open questions

| # | Question | Default until answered |
|---|---|---|
| Q1 | Is Argo CD in Octopus generally available on the instance's 2026.4 build, and do the action type and property keys match an OCL export? | Treat it as Preview (E9). Action type `Octopus.ArgoCDUpdateImageTags` from the OCL catalog (ADR-IR21); compare with an export in the spike. The fallback writer is a script step running `git commit` with the same credential. |
| Q2 | Can the Kubernetes worker chart label script pods for workload identity, and add service-account annotations for workers? | Use password steps until WI-05. Fallback: a Job in the worker namespace (ADR-C2). |
| Q3 | Does lifecycle auto-deploy to TDD need `DeploymentCreate` for the release creator? | Moot while the release creator is the Space Manager `AISF-Service-Account` (ADR-IR32); revisit on the path back. |
| Q4 | What happens when two deployments commit pins to `main` at the same time? | Automatic step retry; serialize deployments of the project if conflicts persist. |
| Q5 | Which Gateway API implementation, and where do TLS certificates come from (Application Gateway for Containers, the AKS application routing add-on, or another)? Public or private endpoints for tdd and uat? | HTTPRoute with placeholders. Public endpoints with TLS, matching legacy. |
| Q6 | ACR SKU: are private endpoints required (Premium)? Do repository-scoped tokens allow the tag-lock operation? | Standard; locks applied by a platform identity if tokens cannot. |
| Q7 | Are Octopus Cloud dynamic-worker egress IPs stable enough for AKS authorized IP ranges? | AKS API public with Entra RBAC and local accounts off; authorized ranges `<octopus-cloud-static-ips>` [VERIFY]. |
| Q8 | Where does UAT data come from: the WI-07 seed or a sanitized copy of legacy UAT? | Seed. |
| Q9 | Does the AI Software Factory keep consuming `rc-<version>` prereleases and GHCR images after cutover, and which pipeline publishes them? | Legacy publishes them until P5; decide at P5. |
| Q10 | Are legacy prod SQL and the new prod SQL in the same subscription (database copy or bacpac)? What maintenance window is acceptable? | Rehearse a bacpac export and import in UAT. |
| Q11 | Is the registered Codefresh runner cluster `<aks-cluster-context>` fit for the two runtimes (dind node pools, the 2024 samples on it)? | Reuse it with dedicated node pools. |
| Q12 | Which `ASPNETCORE_ENVIRONMENT` does the legacy Container App use? | `Production`. |
| Q13 | Can the Octopus access token for gateway registration be short-lived, or does the gateway use it continuously? | Store it in Key Vault and rotate it every 90 days. |
| Q14 | What is the environment repo's default branch? | Answered: `main`; the repo is attached, private and empty, and this tree becomes its first commits. |
| Q15 | Which Argo CD SSO variant is permitted: workload-identity federation on the app registration, or a client secret? | Federation. |
| Q16 | Will a separate prod subscription be provided, and when? | One subscription with locks until then. |
| Q17 | Can an identity with Contributor on the cluster resource group only create AKS, given that the resource provider creates the node resource group? Does AKS with a custom VNet attempt role assignments? | Pre-created identities and a foundation-granted Network Contributor role on the subnet. If the spike fails, the foundation grants the lifecycle identity Contributor on the node resource group name, `MC_<rg>_<cluster>_<region>`. |
| Q18 | Does `features.policyExceptions` also govern the CEL `PolicyException` kind of Kyverno 1.19? | Assume yes (ADR-IR2); prove it in the phase-2 spike, fallback: a reviewed temporary Audit patch. |
| Q19 | After a maintainer pushes a fork pull request's commits to a branch of `20260923-001`, does the fork's pull request show `codefresh/ci` for the same SHA? | Assume yes (ADR-IR26); prove it with the first external contribution. |
| Q20 | Does `CreateNamespace=true` work for Applications in a project without cluster-scoped kinds? | Prove it in the phase-6 spike; fallback: allow kind `Namespace` in `workorders-previews` only (ADR-IR25). |
| Q21 | Does the Octopus `JsonEscape` filter produce a valid `EnvVariables` JSON value for the Argo CD credential, including a multi-line private key? | Assume yes (ADR-IR1); prove it in the first `env-plan`. Fallback: store the credential base64-encoded and decode it in the layer. |
