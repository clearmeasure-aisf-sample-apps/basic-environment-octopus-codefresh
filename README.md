# Work Orders platform environment

Environment repo (`clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh`, private) for the Work Orders app in `clearmeasure-aisf-sample-apps/20260923-001` (public). This repo holds every platform file: the Codefresh pipelines and image definitions, Argo CD, the GitOps overlays, Octopus config-as-code, Terraform, admission policies, the environment checks and the design record. The application repo holds none and stays untouched (ADR-D18).

Paths in every document are paths in this repo; `app:` marks a path in the application repo. The application repo is a copy of `ClearMeasureLabs/bootcamp-palermo-workorders` at `24da122`; that legacy origin keeps the live GitHub Actions → Octopus → Container Apps pipeline, which nothing here touches.

Status: design and implementation sketch, integration review done (phase P0). Nothing is provisioned. Every environment-specific value is a placeholder from design §7.1.

## One verb per tool

| Tool | Verb | Owns | Stays off |
|---|---|---|---|
| Codefresh | builds | `codefresh/ci`, the required merge check of the application repo; build of record on `master`, version `2.5.<first-parent height>`, signed images in ACR, NuGet packages and the release in Octopus (API key of `workorders-octopus`, ADR-IR32); the early wake request for the nonprod cluster; `platform-env/env-checks` for this repo | `deploy`, `approval`, `helm`, `launch-composition` steps; GitOps Runtime; Promotions |
| Octopus Deploy | releases, promotes, approves, migrates, runs runbooks | Lifecycles, channels, freezes, manual interventions, DbUp on in-cluster workers, the pin commit, verification, day-2 and environment runbooks, including cluster sleep and wake | Kubernetes YAML and Helm steps against app namespaces |
| Argo CD | reconciles | Sync, prune and self-heal of `gitops/workorders/envs/<env>` into `workorders-<env>`; add-ons | Image Updater; sync windows |
| GitHub | enforces merge rules | Branch protection on the application repo (`codefresh/ci` required) and the `main` ruleset here (`codefresh/env-checks` required) | GitHub Actions stays disabled in the application repo, a fork (ADR-IR26) |

`scripts/checks/tool-boundaries.sh` enforces the lanes. Details, consoles by role and the reasons: [docs/tool-boundaries.md](docs/tool-boundaries.md).

## Commit to production in one paragraph

A master merge makes Codefresh mint `2.5.<first-parent height>`, ask Octopus to wake the nonprod cluster (without waiting), run the `build.ps1` gates, push signed `workorders/ui-server`, `workorders/worker` and `workorders/db-migrator` images and the `ChurchBulletin.Database` and `ChurchBulletin.AcceptanceTests` packages, then create the Octopus release. Octopus deploys `tdd` automatically and `uat` and `prod` on approval. For each environment it first wakes the environment's cluster if it sleeps, then reads secrets from that environment's Key Vault, runs DbUp on the environment's Kubernetes worker, commits two `newTag` values to `gitops/workorders/envs/<env>/kustomization.yaml`, waits until Argo CD reports Synced and Healthy at that commit, then checks `/_version` and `/_healthcheck`. In `tdd` only it runs the Playwright suite. The legacy GitHub Actions → Octopus → Container Apps path runs untouched until cutover.

## Sleep by default, wake on the first job

The two AKS clusters sleep when nobody needs them and wake on the first Codefresh or Octopus job (user directive; ADR-IR33).
- **Sleep.** Runbook `env-sleep` (project `workorders-infrastructure`) runs every hour. It skips while any task runs. Otherwise it stops the cluster outside the working window (weekdays 07:00–19:00, America/Chicago) or after 120 idle minutes. It enables the alert suppression rule `apr-sleep-<cluster>` first, so a stopped cluster pages nobody.
- **Wake.** Runbook `env-wake` is the only thing that starts a cluster. Every deployment wakes it first without a key: step `wake-environment` deploys the platform-owned project `platform-wake`, whose one step runs `env-wake` and waits. The `workorders-infrastructure` runbooks run `env-wake` themselves; the `workorders` runbooks only wait for a wake. `workorders/release` also requests one early (step `wake_nonprod`), so the nonprod cluster starts while CI runs; that request never fails the build. On-call runs `env-wake`, or `env-sleep` with `Sleep.Force`, by hand. The Space Manager key stays in platform-owned steps; project `workorders` never holds it (ADR-IR33).
- **Always on.** Azure SQL, ACR, Key Vault, Log Analytics, the state storage, the private endpoints and each cluster's load balancer keep running. Design §3.4 estimates the platform at about $325 a month instead of about $1,640 always on [UNVERIFIED].
- `Sleep.Enabled` is `true` for both classes in this sample; a real production sets it to `false` for `infra-prod`. Procedures (force-wake, force-sleep, pause): [docs/runbooks/sleep-and-wake.md](docs/runbooks/sleep-and-wake.md). Lab: [06 Sleep and wake](docs/walkthroughs/06-sleep-and-wake.md). Checks: `tool-boundaries.sh` TB17–TB20 and `consistency.sh` C23.

## Layout and writers

Writers: **H** people through a reviewed pull request to `main`; **O-pin** Octopus, direct commit to `main`, `images[].newTag` only; **O-branch** Octopus UI edits of config-as-code on non-`main` branches, merged by H. Readers: Argo CD, Codefresh `env-checks`, Octopus, the environment Terraform.

```text
basic-environment-octopus-codefresh/
├── README.md  CODEOWNERS  .gitleaks.toml  .yamllint.yaml       H
├── contracts/platform-contracts.yaml         H     machine-readable design §7; read by the checks
├── design/platform-design.md, debate/        H     adjudicated design and debate record
├── docs/                                     H
│   ├── bootstrap.md  tool-boundaries.md  cutover-and-decommission.md  consistency-notes.md
│   ├── walkthroughs/01..06-*.md                    teaching labs 18–23
│   └── runbooks/*.md                               break-glass, rollback, PITR, rotation, SLO burn, sleep and wake
├── scripts/checks/{tool-boundaries,consistency,validate-all}.sh     H   run by env-checks
├── codefresh/
│   ├── pipelines/env-checks.yml, specs/platform-env-checks.yml     H   this repo's checks
│   ├── workorders/{pipelines,specs,scripts}/, version.env           H   app build, release, previews
│   └── images/ci-dotnet/Dockerfile                                  H   toolchain image platform/ci-dotnet
├── containers/workorders/{worker,db-migrator}/Dockerfile           H   Worker and migrator images
├── .octopus/workorders/**                    H, O-branch   deployment process, variables, runbooks
├── .octopus/workorders-infrastructure/**     H, O-branch   env-plan, env-apply, env-destroy, env-wake, env-sleep, …
├── octopus/terraform/                        H     Octopus objects outside config-as-code
├── argocd/                                   H     bootstrap values, cluster roots, projects, add-ons, apps
├── gitops/workorders/
│   ├── base/                                 H     shared manifests; no images: block
│   ├── components/                           H     roll-through changes (ADR-D6); bluegreen (phase 6)
│   ├── previews/                             H     phase 6
│   └── envs/{tdd,uat,prod}/
│       ├── kustomization.yaml                O-pin newTag only; H for anything else (break-glass)
│       └── config/                           H     per-environment configuration
├── policies/{kyverno,octopus}/               H     admission policies; inactive Platform Hub policy
└── terraform/
    ├── foundation/                           H     applied by a human Owner: every role assignment
    └── environment/                          H     applied only by Octopus runbooks
```

No other identity writes to this repo. Argo CD and Codefresh never write; Codefresh posts commit statuses only.

## Phase status

| Phase | Scope | Status (2026-09-24) | Exit criteria (summary) |
|---|---|---|---|
| P0 Design | Design, sketch, integration review | Integration review done; user actions open (design §10) | Every §11 file exists; validations pass or are recorded; Gitleaks clean; user accepts or amends §10 |
| P1 Foundation and CI | Foundation, Octopus Terraform, Codefresh objects; `codefresh/ci` required on the application repo; `workorders/release` creates releases | Not started | 10 consecutive green master builds; each release created once; signed, locked images; the only Octopus API key is the `AISF-Service-Account` key |
| P2 TDD on AKS | `env-apply` nonprod; Argo CD, gateway; TDD auto-deploy; WI-08 | Not started | ≥ 20 consecutive TDD releases, ≥ 90 % green; drills pass; 14 days of Kyverno audit; OIDC replaces the provisioner secret |
| P3 UAT and Worker | UAT with sign-off; Worker in tdd and uat; SLO alerts | Not started | Two approved UAT cycles; blocking UAT smoke; Worker 14 days clean |
| P4 Prod cutover | Prod environment over OIDC; WI-01/02/03/05; single migration owner; DNS | Not started | Rehearsal passed; PITR drill within RTO; 14 days of prod SLO; legacy rollback still possible |
| P5 Decommission | Legacy workflows, project, Container Apps and secrets retired | Not started | No legacy consumer; secrets deleted; docs updated |
| P6 Optional | Previews, blue-green, PreSync guard, Platform Hub, Octopus Approvals | Not planned | A measured need per item |

Checklists, evidence and rollback per phase: [docs/cutover-and-decommission.md](docs/cutover-and-decommission.md).

## Start here

| Need | Read |
|---|---|
| Why the platform looks like this | [design/platform-design.md](design/platform-design.md) (§2 decisions, §7 contracts, §9 phases, §10 recommendations) |
| Stand the platform up, in order, with an owner per step | [docs/bootstrap.md](docs/bootstrap.md) |
| Which tool does what, and which console each role uses | [docs/tool-boundaries.md](docs/tool-boundaries.md) |
| Move environments across, and retire the legacy path | [docs/cutover-and-decommission.md](docs/cutover-and-decommission.md) |
| Operate: break-glass, rollback, restore, rotation, SLO burn, sleep and wake | [docs/runbooks/](docs/runbooks/) |
| Learn the platform (labs 18–23, each with an offline variant) | [01 Follow a commit](docs/walkthroughs/01-follow-a-commit.md) · [02 Schema and configuration change](docs/walkthroughs/02-schema-change.md) · [03 Promotion and hotfix](docs/walkthroughs/03-promotion-and-hotfix.md) · [04 Drift and rollback](docs/walkthroughs/04-drift-and-rollback.md) · [05 Environment lifecycle](docs/walkthroughs/05-environment-lifecycle.md) · [06 Sleep and wake](docs/walkthroughs/06-sleep-and-wake.md) |
| Cross-package findings from the checks | [docs/consistency-notes.md](docs/consistency-notes.md) |

## Contributing

- **Application changes:** open pull requests against `clearmeasure-aisf-sample-apps/20260923-001`, base `master`. The repo is a fork, and a fork's pull requests default to the upstream repository: pick the base repository explicitly in the web UI, or run `gh repo set-default clearmeasure-aisf-sample-apps/20260923-001` before `gh pr create`. Never open a pull request against `ClearMeasureLabs/bootcamp-palermo-workorders`. Push branches to the application repo itself: fork pull requests get no `codefresh/ci` until a maintainer pushes the reviewed commits to a branch there (ADR-IR26).
- **Platform changes:** pull requests to `main` of this repo, reviewed by the CODEOWNERS teams, with `codefresh/env-checks` green.

## Checks

`codefresh/pipelines/env-checks.yml` runs these on every push and posts `codefresh/env-checks`, which the `main` ruleset requires. Run them locally from the repo root:

```bash
scripts/checks/validate-all.sh all            # every check; missing tools are skipped with a warning
scripts/checks/validate-all.sh consistency    # names and shapes against contracts/platform-contracts.yaml
scripts/checks/tool-boundaries.sh             # one verb per tool
CI=true scripts/checks/validate-all.sh yaml   # CI mode: a missing tool fails
```

Tools: bash 4 or later (Alpine images and the macOS system bash lack it), git, yamllint, kustomize, kubeconform, terraform, gitleaks, python3 with PyYAML, and a Mermaid parser for `mermaid`. In Codefresh, `RENDER_DIR` on the shared volume carries rendered overlays from the `kustomize` step to the `kubeconform` step when they run in different images. Change a name in `contracts/platform-contracts.yaml` only in the same pull request that changes design §7.
