# Tool boundaries

Every tool of the platform can deploy something. Three deployers is the biggest source of confusion for the people who run it (R1-P §6 D1), so each tool gets one verb and the overlapping features of the others stay off (ADR-D2). The rules hold for every app, whatever its pipelines look like after "scaffold, then own" (ADR-IR34). the tool-boundary rules (`Kit.Boundaries`) enforces them on every push through `platform-env/env-checks`; CAP-KIT-006 runs their C# port from the offline suite (`tests/Platform.Conformance.Offline/Kit/Boundaries`, one test per rule), which also holds TB23 and TB24. The rule IDs below refer to both.

## One verb per tool

| Tool | Verb | Decides | Never |
|---|---|---|---|
| **Codefresh** | builds | Whether a commit of an app is releasable: the app's own gates, its version, signed and locked images under `apps/<app>/`, build information, and the Octopus release (key in context `platform-octopus`, ADR-IR32, decision 4) | Deploys, approves, syncs Argo CD, touches an app cluster, commits to any repo, holds a cloud identity |
| **Octopus Deploy** | releases, promotes, approves, runs runbooks | When a release may enter an environment (lifecycles, channels, freezes), who approved it, which tags each environment runs (the pin commit), whether the deployment verified; day-2, tier and app-layer runbooks | Applies Kubernetes objects, runs Helm or kubectl against app namespaces, creates releases from feed triggers |
| **Argo CD** | applies | How fast `main` becomes cluster state and that the cluster stays that way (prune, self-heal); every tenant, from `apps/*.yaml` through the ApplicationSet `apps`; migrations as PreSync Jobs | Chooses versions (no Image Updater), holds a calendar (no sync windows), rolls back |
| **GitHub** | enforces merge rules | Which change may merge: each app repository requires `codefresh/ci` and one review; `main` here requires `codefresh/env-checks` and CODEOWNERS review | Runs platform workflows: GitHub Actions stays off in app repositories, and here runs only the board-only workflow `project-board.yml` and the alert-only workflow `release-stall-check.yml` (TB24) |

![View: one verb per tool](../design/diagrams/view-responsibility.png)

*The owners of design §4's responsibility matrix. Red dashed lines are the boundary rules below (TB01, TB02, TB04, TB07).*

Two rules follow:
- **Git is the gate between Octopus and Argo CD.** Octopus writes only the pin fields of `gitops/apps/<app>/envs/<env>/<deployable>/`; Argo CD applies whatever `main` holds. Neither calls the other to change state; the gateway only reports health.
- **One writer per fact.** The version comes from the app's Codefresh release pipeline, the release record from Octopus, the running tags from the pin files, and everything else in this repository from reviewed pull requests.

## Where the lanes meet

| Handoff | From → to | Contract (design §7.0) | Checked by |
|---|---|---|---|
| Image publish | Codefresh → registry | M1 images at `apps/<app>/<image>` through the shared token `cf-apps-release`; M2 keyless signature and SBOM; tags locked after signing | consistency check C25 (M1); CAP-CF-006, CAP-CF-007 |
| Release creation | Codefresh → Octopus | M3 `octopus_release` with an explicit version per package (`--package` of `octopus release create`, or `PACKAGES` of the typed step), no default package version; key `OCTOPUS_API_KEY` from `platform-octopus`, attached to release pipelines only | consistency check C25; tool-boundary rules TB14; CAP-CF-008 |
| Pin commit | Octopus → this repository | The image-tag step (stored Git credential) or the pin writer step template `platform-pin-writer` (a per-run installation token of the GitHub App `aisf-pin-writer`, Contents write and Metadata read, this repository only; the writer once the processes switch to it, #58 stage 2): direct commit to `main` of `newTag`, Helm image values at `argo.octopus.com/image-replace-paths`, or raw `image:` fields | `Platform.Onboarding check` (pin shape); the bot-path audit (`PinWriterTests`); CAP-OCT-012 |
| Reconciliation | This repository → Argo CD | Application `tenant-<app>` renders `<app>-<deployable>-<env>` and `<app>-db-<env>` with automated prune and self-heal | consistency check C03, C04, C09 |
| Health report | Argo CD → Octopus | Gateway with the read-only account `octopus` (`applications, get, app-*/*`); annotations rendered only by the tenant chart | consistency check C02, C06; tool-boundary rules TB09 |
| Early wake | Codefresh → Octopus | Step `wake_nonprod` of a release pipeline asks for `env-wake` in `infra-nonprod` and never fails the build | tool-boundary rules TB18; CAP-CF-009 |
| Wake first | Octopus → Azure | Step 0 of every app process that touches a cluster deploys a release of `platform-wake`, whose one step runs `env-wake`; in-cluster app runbooks wait with `Wake.WaitMinutes`; only `env-wake` and `env-sleep` change power state (ADR-IR33) | consistency check C23; tool-boundary rules TB17 to TB20 |

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
| 7 | Kubernetes YAML, Helm, Kustomize or kubectl steps against app namespaces; `rotate-db-passwords` excepted, which changes no desired state (`ALTER LOGIN`, ESO force-sync, restart of named Deployments; [credential-rotation.md §4](runbooks/credential-rotation.md#4-database-passwords)) | Octopus | Self-heal would revert them, and Git would stop being the truth | TB07 |
| 8 | Role assignments, locks or policy assignments in `terraform/tier` or `terraform/apps/tier` | Terraform | Every grant is the provisioner's (decision 3); locks come from the Owner script | TB08 |
| 9 | Octopus annotations outside the tenant chart; tenant annotations | Argo CD | The gateway would map add-ons or databases into deployments; there are no tenants (ADR-C8) | TB09 |
| 10 | Trigger sync in the Argo CD step | Octopus | Keeps the gateway account read-only (E7) | TB10 |
| 11 | Feed or built-in release triggers | Octopus | A second release creator; duplicate numbers caused incidents EP20 and EP21. The keyless handoff stays an optional pattern (decision 4) | TB11 |
| 12 | Kubernetes deployment targets on app clusters | Octopus | Shared `k8s-<env>` workers run in-cluster steps; targets would invite direct deploys | TB12 |
| 13 | The stored provisioner account, stored variable sets and stored contexts in any project or pipeline | Octopus, Codefresh | Subscription-wide or org-wide secrets must not reach branch-controlled YAML (ADR-C10, R5) | TB13a, TB13b, TB13c |
| 14 | Octopus API keys other than `OCTOPUS_API_KEY` from `platform-octopus` in release and conformance pipelines (with `conformance-*.ps1` and `octopus-runbook.ps1`); a key option on a command line (`--api-key`, `-ApiKey`) | Codefresh | One key, one context, one rotation (ADR-IR32) | TB14 |
| 15 | `mutateDigest` in image verification | Kyverno | Live state would differ from Git and self-heal would fight it (ADR-D11) | TB15 |
| 16 | Commits and pushes, also through a Git wrapper or with quoted or array arguments in PowerShell | Codefresh | Only Octopus (pins) and people (pull requests) write here; the conformance pipelines push only to `<sandbox-app-repo>` (`conformance-*.ps1` through `sandbox-git.ps1`) | TB16 |
| 17 | `az aks start`, `az aks stop` (`Start-AzAksCluster`, `Stop-AzAksCluster`, ARM `…/start` and `…/stop`) or alert-rule toggles outside `env-wake` and `env-sleep`; no conformance script is exempt (`aks-power.ps1` only reads the power state) | All | One place decides whether a cluster runs, paired with the alert suppression | TB17 |
| 18 | REST calls that run runbooks outside the platform places (the conformance pipelines run them through `octopus-runbook.ps1`) | Octopus, Codefresh | They bypass approvals and the audit trail; apps wake keylessly through `platform-wake` | TB18 |
| 19 | Azure rights that can start a cluster in app projects, starters or app grants | Octopus, Terraform | Apps ask `platform-wake`; they never hold the right | TB19 |
| 20 | `Platform.OctopusApiKey` outside `platform-infrastructure`, `PlatformWake.*` outside `platform-wake`, any literal key | Octopus | The Space Manager key controls the space; passed variables override a child's (decision 17) | TB20 |
| 21 | A second runtime, grants on the build cluster, cloud identities for pipelines | Codefresh, Terraform | Trust boundary TB2 | TB21 |
| 22 | App names in platform files | All | The platform is app-neutral; onboarding touches only app-scoped paths | TB22 |
| 23 | Shell scripts (`*.sh`) and Bash script steps (`Octopus.Action.Script.Syntax = "Bash"` in `.octopus/`, `octopus/templates/`) | All | One script language beside .NET: PowerShell 7. Permanent exceptions: `containers/apps/*/*/migrate.sh` (entrypoints of .NET runtime images, which ship no pwsh) and `terraform/tier/scripts/aks-token.sh` (exec credential plugin, started per client, so start-up time matters). Every other script and OCL file is listed as pending conversion (`ScriptLanguageRule.cs`) and leaves the list in the change that converts it | TB23 |
| 24 | Bot commits that change more than pin fields | Octopus machine user | The machine user bypasses review, so every push to `main` audits its commits | AUDIT |
| 25 | Fork events in any trigger | Codefresh | A fork's pull request would run with the platform's contexts | consistency check C25; CAP-CF-005 |
| 26 | Schedules that wake, apply or destroy | Octopus | Clusters stay asleep until the first job (ADR-IR33); schedules run only `env-sleep` | consistency check C23 |
| 27 | GitHub Actions workflows in this repository. Two narrow exceptions, each in a lane of its own: `.github/workflows/project-board.yml` (board-only), which only sets the status of issues and pull requests on the GitHub Project board ([board automation](#board-automation-the-board-only-workflow)), and `.github/workflows/release-stall-check.yml` (alert-only), which only reads the pin commits of `main` and the open pull requests and commit statuses of this repository and the app repository, and writes GitHub issues here ([release stall alert](#release-stall-alert-the-alert-only-workflow)) | GitHub | A second build or deploy engine beside Codefresh and Octopus, with its own secrets and audit trail. The board workflow checks out no code, runs no build, deploy or cluster tool, keeps `GITHUB_TOKEN` at `contents: read` and reads only the board credentials (`BOARD_APP_ID`, `BOARD_APP_PRIVATE_KEY`: the GitHub App, no personal access token). The alert workflow runs on a schedule or by hand only, reads no secret, runs no build, deploy or cluster tool (it reports a build that did not start and starts none), checks out with a SHA-pinned action and `persist-credentials: false`, and holds `issues: write` as its only write permission | TB24 |

## Board automation: the board-only workflow

`.github/workflows/project-board.yml` keeps [GitHub Project 678](https://github.com/orgs/clearmeasure-aisf-sample-apps/projects/678) of `clearmeasure-aisf-sample-apps` in step with work. It is one of the two workflows TB24 admits (`GitHubWorkflowRule.Exceptions`, lane `BoardOnly`; the other is the [release stall alert](#release-stall-alert-the-alert-only-workflow)), and it stays in the GitHub lane: it reads the event payload and calls only the GitHub GraphQL API. It never checks out code, builds, deploys, pushes, or reaches a cluster, a registry, Octopus or Azure.

| Event | Board change |
|---|---|
| `issues` opened, reopened | The issue is added (if needed) with Status `Todo` |
| `issues` closed, for any reason (completed, not planned, duplicate) | The issue: `Done`, added first if it is not on the board. Every closed issue ends in `Done` |
| `pull_request` opened, reopened, ready_for_review (drafts wait for ready_for_review), same-repository branches only | Every issue the pull request closes (GraphQL `closingIssuesReferences`, plus `Closes`, `Fixes` or `Resolves` `#n` or `owner/repo#n` in the body) and the pull request itself: `In Review` |
| `pull_request` closed with `merged = true` (same-repository branches only) | The same items: `Done`. Closed without merge: no change |
| `repository_dispatch` type `board-status`; `workflow_dispatch` | The named issue or pull request: the named status, for example `Deployed to TDD` or `Deployed to UAT` |

How it behaves:
- **By name at run time.** The project (`PROJECT_OWNER`, `PROJECT_NUMBER`), the field `Status` and its options are looked up on every run, so renamed or extra options work without a change here. The board's options are app-neutral and shared by every repository: `Todo`, `In Progress`, `In Review`, `Deployed to TDD`, `Deployed to UAT`, `Deployed to Prod`, `Done`. The events use `Todo`, `In Review` and `Done` (the workflow's `env`: `STATUS_TODO`, `STATUS_IN_REVIEW`, `STATUS_DONE`); the others come by dispatch. Names match without regard to case; a status that is not an option fails the run and lists the available options.
- **App field (optional).** When the project has a single-select field `App` (`APP_FIELD`) and an item has no value yet, the workflow sets it when it adds the item: this repository is `platform`; an app repository gets the app whose descriptor `apps/<app>.yaml` lists it under `repositories` (for example `workorders`, `sandbox`), read from `main` through the API with `GITHUB_TOKEN`. The workflow names no app itself (the platform is app-neutral, TB22). Without the field, or without a matching descriptor or option, the App step is skipped with a warning and never fails the run.
- **Idempotent.** `addProjectV2ItemById` returns the existing item when the issue or pull request is already on the board; setting a status twice is harmless. Runs are serialized per item (`concurrency` group per repository and number).
- **Fork pull requests never reach the secrets.** The workflow listens to `pull_request`, never `pull_request_target` (TB24 rejects it): GitHub gives a fork's `pull_request` run no secrets, and the job also runs only when `github.event.pull_request.head.repo.full_name == github.repository` (TB24 requires that guard wherever `pull_request` is a trigger). A same-repository pull request runs the workflow from its own merge ref, so a change to `project-board.yml` is reviewed under CODEOWNERS before it can run with the secret on `main`; the title and body are read from the event file as data and never expanded into the script.
- **No secret in a tracked file.** The repository is public, so the offline check `NoSecretsInTrackedFiles` (CAP-KIT-007, `NoSecretsInTrackedFilesTests`) fails when a tracked file holds a private key PEM block, a GitHub token, an Octopus API key, an Azure storage account key, a Slack token or an AWS access key ID; `<...>` and `${...}` placeholders pass, as in `.gitleaks.toml`. It backs gitleaks and needs no tool.
- **Credential: the GitHub App only, no personal access token.** The repository Actions secrets `BOARD_APP_ID` and `BOARD_APP_PRIVATE_KEY` hold the App `aisf-board` (App ID 5130401). A first step mints an installation token with `actions/create-github-app-token` (pinned to a full commit SHA, `continue-on-error` only so the next step can report the outcome), and the script uses it: an App token has its own rate limit, separate from any account token shared with Codefresh and the agents. Without a token the script fails the run with `GitHub App token unavailable (mint outcome: ...)`, which names both secrets; nothing falls back to another credential. The log line `Board credential: GitHub App installation token (BOARD_APP_ID / BOARD_APP_PRIVATE_KEY).` names which one ran, never a value. The App has organization permission *Projects: Read and write* and repository permissions *Issues: Read*, *Pull requests: Read* and *Metadata: Read* on the repositories the board tracks (this repository and `20260923-001`); it deliberately has no *Contents: write* (an App that can write contents could push to `main`). TB24 admits exactly these two secrets, and only in the board workflow (`GitHubWorkflowRule.AllowedSecrets`), and the retired-credential guard (CAP-KIT-007, `RetiredCredentialGuard`) fails any tracked file that names one of the retired personal access tokens outside the history section of [credential-rotation.md](runbooks/credential-rotation.md).
- **Unattended tooling uses the App too.** `scripts/github/GitHubAppAuth.ps1` signs the App JWT (RS256, from a key file or an environment variable, never printed or written) and exchanges it for a one-hour installation token narrowed to the repositories and permissions of `githubApp` in `.claude/factory-loop.json`. The Octopus step `report-commit-status` uses the same code as a marked inline copy of the helper (a conformance test keeps the two identical; no openssl, no key file). The feature-loop helper `board.ps1` and the stall watchdog take a token in this order: `AISF_BOARD_APP_TOKEN` (pre-minted), a token minted from `AISF_BOARD_APP_ID` and `AISF_BOARD_APP_PRIVATE_KEY_PATH` or `AISF_BOARD_APP_PRIVATE_KEY`, then `gh auth token`. Because the App lacks *Contents: write* (which `repository_dispatch` requires) and *Commit statuses: read*, a call the App token cannot make is retried once with the `gh` token; `board.ps1 move` therefore normally sends its dispatch with the `gh` token and only reads run on the App. The private key stays outside the repository (owner steps in [credential-rotation.md](runbooks/credential-rotation.md), section 11).

Deployment tools push a status with a repository dispatch to this repository (token with *Contents: Read and write* on this repository, which `repository_dispatch` requires; the call starts the workflow and changes nothing else):

```bash
curl -fsS -X POST \
  -H "Authorization: Bearer $DISPATCH_TOKEN" \
  -H "Accept: application/vnd.github+json" \
  -H "X-GitHub-Api-Version: 2022-11-28" \
  https://api.github.com/repos/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/dispatches \
  -d '{"event_type":"board-status","client_payload":{"repository":"clearmeasure-aisf-sample-apps/20260923-001","issue":42,"status":"Deployed to TDD"}}'
```

`client_payload.repository` is `<owner>/<repo>` of the issue or pull request (empty: this repository), `issue` its number and `status` the option name. By hand: *Actions → project-board → Run workflow* with the same three inputs, or `gh workflow run project-board.yml -f repository=<owner>/<repo> -f issue=<n> -f status="Deployed to UAT"`. Wiring a deployment tool to send the dispatch is a separate lane change (its token, where it is stored, which step sends it); a failed board update must never fail a deployment.

## Release stall alert: the alert-only workflow

Octopus deploys a version by committing it here, and a release that is pinned in one environment and never in the next leaves no other trace: from 2026-09-30 to 2026-10-06 nine releases of one app were pinned in uat and never in prod, two more in tdd and never in uat, and nobody was told (#86). `.github/workflows/release-stall-check.yml` runs `scripts/release/release-stall-check.ps1 -Issues -PullRequestBuilds` every hour (and by hand, with a dry-run input); all logic is in the script, which the offline suite runs against recorded history and a stub of the issues API (CAP-KIT-012). The same run reports a second thing that leaves no trace, a pull request whose Codefresh build never started ([below](#a-pull-request-whose-build-never-started), CAP-KIT-013).

Why GitHub Actions and no other lane: the alert needs a clock, this repository's history and a token that writes issues here. Octopus schedules run only `env-sleep` (consistency check C23), Codefresh holds no credential that writes to this repository (TB16), and the default `GITHUB_TOKEN` of a workflow run is the one credential that needs no storing and no rotation. TB24 therefore admits the workflow in a second lane, `AlertOnly` (`GitHubWorkflowRule.Exceptions`), narrower than a build and different from the board:

| | Board-only | Alert-only |
|---|---|---|
| Triggers | `issues`, `pull_request` (same-repository guard), `repository_dispatch`, `workflow_dispatch` | `schedule`, `workflow_dispatch`; no pull request, issue or push event, so no fork code and no event payload |
| `GITHUB_TOKEN` | `contents: read` | `contents: read`, `issues: write`; no other write. Pull requests and commit statuses are read as public data, which needs no permission, here and in the app repository |
| Secrets | `BOARD_APP_ID`, `BOARD_APP_PRIVATE_KEY` | none |
| Checkout | never | `actions/checkout` pinned to a full commit SHA, `persist-credentials: false`, full history |
| Tools | no build, deploy or cluster tool, no self-hosted runner | the same |

The rule the script applies, per app with a folder `gitops/apps/<app>/envs/<environment>` and per environment in the order tdd, uat, prod (the last environment of an app has no next one and is never checked):

| State | When | Issue |
|---|---|---|
| Promoted | The newest version pinned in the environment (versions compare as versions, never as text) is also pinned in a later environment | None; an open one is closed with a comment |
| Bypassed | The next environment holds a newer version, pinned there directly | The same |
| RolledBack | The latest pin of the environment is an older version than the newest one | The same |
| Frozen | The next environment is prod and the moment lies in the prod weekend freeze (Saturday 00:00 to Monday 00:00 UTC, `prod_freeze_first_window` of `octopus/terraform`) | None is opened; an open one stays |
| Waiting | The pin is at most 90 minutes old (`-ThresholdMinutes`); minutes inside the freeze do not count towards prod | The same |
| Stalled | Everything else | One issue `Release <version> of <app> has not left <environment>` with the pin commit, the deployment ID and the age |

How it behaves:
- **Only the newest release of an environment counts.** An older release that a newer one replaced is never reported, and its open issue is closed with a comment naming the newer release and saying whether the older one was ever promoted.
- **One issue per stall.** No issue is opened while an open one has that exact title, or when one with that title was closed after the pin: closing a stall issue by hand is an answer, and the check does not open it again for that pin. A later pin of the same version (after a rollback) is a new stall.
- **It closes only what it wrote.** An issue is touched only when its title has that shape, its body carries the script's marker line (`<!-- release-stall app=... version=... environment=... -->`) and the pin history knows that release. Pull requests and other issues are never changed, and no text of an issue is ever echoed into a comment.
- **The conformance fixture is not checked.** Releases of `sandbox` stay in tdd on purpose (`-ExcludeApp`, default `sandbox`).
- **The board does not see these issues by itself.** GitHub starts no workflow for an event that `GITHUB_TOKEN` caused, so `project-board.yml` does not add a stall issue to the board or move it to `Done`. Put it there by dispatch when it should be tracked as a work item ([board automation](#board-automation-the-board-only-workflow)).
- **By hand.** `pwsh -NoProfile -File scripts/release/release-stall-check.ps1` prints one line per stall and exits 1 when there is one; `-Now <time>` judges a past moment, `-All` lists every app and environment with its state, `-Json` writes the data, and `-PullRequestBuilds` adds the pull requests (it reads GitHub, without a token when `GITHUB_TOKEN` is not set). *Actions → release-stall-check → Run workflow* with `dry_run` prints what a run would open and close.
- **Limits.** GitHub may delay a scheduled run and pauses schedules of a repository without activity for 60 days; a stall is therefore reported within about an hour after the threshold, not at it. The check reads pin commits only: a deployment that failed before its pin commit and one that nobody started look the same, and Octopus says which ([If a step stalls](runbooks/demo-commit-to-prod.md#if-a-step-stalls)). The repository is public and anyone may open an issue: an issue that already has a stall's exact title counts as that stall's issue, whoever wrote it.

### A pull request whose build never started

A push to a pull request branch sometimes starts no Codefresh build: an hour later the head commit still has no `codefresh/ci` (app repository) or `codefresh/env-checks` (this repository) status, the pull request cannot merge, and nothing says so. It was seen at least three times from 2026-10-06 to 2026-10-08, last on pull request 84 of the app repository (head `4ca02cb`), and each time someone noticed only when a wait ran out (#101). With `-PullRequestBuilds` the hourly run reads the open pull requests of both repositories and the statuses of their head commits, and keeps one issue per pull request whose build did not start. The repositories and their contexts are the script's `-BuildContext` default, which a test keeps equal to `repos.*.ci.prContext` of `.claude/factory-loop.json`.

| State | When | Issue |
|---|---|---|
| Fork | The head is in another repository. Codefresh starts no build for a fork (row 25), so its statuses are not read | None |
| Started | The head commit has a status in the required context, in any state: `pending`, `success`, `failure` or `error` | None; an open one is closed with a comment naming the state and the head |
| Draft | A draft without that status | None is opened; an open one stays |
| Waiting | No status, and the wait is at most 30 minutes old (`-BuildThresholdMinutes`). The wait starts at the committer date of the head commit, or at the opening of the pull request when that is later | The same |
| NotStarted | Everything else | One issue `Build <context> of pull request <owner>/<repo>#<number> has not started` with the branch, the head commit, the wait and what to do |

A pull request that was merged or closed is not listed: none is opened, and an open issue is closed with a comment saying which. A push to `main` here starts no build and is no pull request, so it is never checked.

How it behaves:
- **What to do when the issue appears.** Push an empty commit to the branch (`git commit --allow-empty -m "Start the build"`, then `git push`), or start the build of that branch in Codefresh. If Codefresh shows a build of the branch that waits for a slot, it is the queue and not a lost push ([If a step stalls](runbooks/demo-commit-to-prod.md#if-a-step-stalls)).
- **One issue per pull request, in this repository.** Also for a pull request of the app repository: the token writes here only. The body names the pull request as `<owner>/<repo>#<number>`, which GitHub renders as a link to it. No issue is opened while an open one has that exact title, or when one with that title was closed after the wait began: closing it by hand is an answer for that head. A later head that gets no build either is a new wait and a new issue: an issue of the check names its head commit, and a closed one about another head is no answer, even when it was closed after the later head was committed (a commit is often made some time before its push).
- **It closes only what it wrote, and only when it knows.** An issue is touched only when its title has that shape, its body carries the marker line (`<!-- build-not-started repository=... pull=... context=... -->`), its repository and context are in `-BuildContext` and that repository was read in this run. A number that is no pull request there is left alone with a `WARN` line. Nothing of a pull request's title or body is ever copied into an issue or a comment; the branch name is, as code, when it has only letters, digits, `.`, `_`, `/` and `-`.
- **The credential is unchanged.** The workflow's `GITHUB_TOKEN` holds `contents: read` and `issues: write` on this repository and nothing in the app repository. Pull requests and commit statuses are read through the REST API as public data, which both repositories are, so no permission, secret or App was added (checked on 2026-10-08: both listings answer a request without any token). If a repository turns private, its reads are refused: the run prints `FAIL build-start: <owner>/<repo> could not be read`, leaves that repository's issues alone, still reconciles the release stalls and the other repository, and fails. The fix is then a lane change (a permission or a credential), not a quiet gap.
- **Limits.** The check sees statuses, not Codefresh: a build that Codefresh queued and that reported nothing for more than 30 minutes looks the same as a push that started none. The committer date stands in for the time of the push, which GitHub does not list: a commit made long before it was pushed to a pull request that is already open can be reported at the next run and is closed again by the run after it. A pull request that is closed and reopened on the same head is not reported again, because its issue was closed after that wait began. The alert comes with the hourly run, so between 30 and about 90 minutes after the push.

## Commit and pull request text

Commit messages and pull request text carry no model identifier and no co-author trailer (owner rule, #57). The guard is `Kit.Boundaries.CommitAttributionGuard` (capability `CAP-KIT-011`, fixture `CommitAttributionGuardTests`), which the offline suite runs in `codefresh/env-checks`. It matches line by line, case-insensitively, on identifier shapes and never on substrings, so the platform name Octopus, the pin commit titles (`Pin workorders 2.5.757 in prod (Deployments-54634)`) and `.claude/` paths never match:

| Rule | Forbidden |
|---|---|
| `co-author-trailer` | a line that starts (after optional spaces) with a `Co-Authored-By:` header |
| `attribution-footer` | a "Generated with Claude" footer (with or without "Code", with or without a link) and the vendor no-reply address |
| `model-identifier` | `claude-<family>-...` identifiers, `Claude <family>` with or without a version, and a bare family name followed by a version (for example `<family> 4.5`), for the families Opus, Sonnet, Haiku and Fable |

What is read:
- **The commit range** `origin/<default branch>..HEAD` (the default branch from `origin/HEAD`, else `origin/main`; `PLATFORM_COMMIT_RANGE` or an explicit range replaces it): subject, body and trailers of every commit, merge commits included. On `main` the range is empty, so history that is already merged never fails a later run. A missing base ref (shallow clone, first push) is a reported `SKIP` for this part, never a failure and never a silent pass.
- **The pull request title and body**, when the caller passes them in `PLATFORM_PR_TITLE` and `PLATFORM_PR_BODY`; unset or empty means none was supplied and the report says so. A squash merge builds its message from this text and discards the branch commits, so the branch commits alone are not enough.

Limits: `codefresh/env-checks` does not expose the pull request title or body, so in CI only the commit range is checked. The PR text is checked where the caller has it: the feature-loop pre-PR gate ([SKILL.md](../.claude/skills/feature-loop/SKILL.md), Gates) runs the fixture with both variables set, and the same command after opening the PR (`gh pr view --json title,body`) covers a later edit. Whether merges are squash-only, and whether the local harness appends an attribution footer by default, are repository and owner settings that no file check sees.

## What the checks cannot see

- **Console actions.** A Sync click in Argo CD or a variable edit in the Octopus UI leaves no file behind. RBAC, config as code on protected `main`, Octopus Git drift detection and Argo CD self-heal cover them.
- **GitHub settings.** Rulesets, secret scanning and push protection are settings on github.com, not files, so no offline check sees them. The repository is public, which rules out a push ruleset that restricts file paths (E31); the target is a required-review branch ruleset with the Octopus GitHub App as the only bypass actor, backed by the bot-path audit (CAP-OCT-012). The live ruleset is weaker than the target until the owner-only steps in [owner/public-repo-checklist.md](owner/public-repo-checklist.md) are done ([design §6.2](../design/platform-design.md#62-enforcing-the-write-matrix)).
- **The Argo CD UI allow-list.** This repository is public, so the client CIDRs of SecurityPolicy `argocd/argocd-ui-allowlist` live in Key Vault secret `argocd-ui-allowlist` and in the cluster only; `platform-ingress` ignores that field and Git holds a deny-all placeholder ([argocd-ui-access.md](argocd-ui-access.md#the-allow-list-is-cluster-only)).
- **Values in the Octopus database.** Sensitive variables and some settings are not in Git (E26); `octopus/terraform` manages the non-sensitive ones.
- **PR text as written by an agent.** env-checks has no pull request title or body, and a squash merge writes the merge commit from them. Only a caller that passes `PLATFORM_PR_TITLE` and `PLATFORM_PR_BODY` gets them checked ([Commit and pull request text](#commit-and-pull-request-text)).
- **Runtime identity misuse.** Federated subjects are exact (§7.0), each app's ClusterSecretStores admit only its namespaces, and the prod signer policy admits only images signed by the app's own release pipeline. The live suite proves these (CAP-GIT-004, CAP-AZ-001, CAP-AZ-002, CAP-AZ-017).

## Changing a lane

A lane change is a design change. One pull request updates the ADR in `design/platform-design.md`, the names in `contracts/platform-contracts.yaml` and the rule in the tool-boundary rules (`Kit.Boundaries`) and its C# port (`ToolBoundaryRules.cs`, `GitHubWorkflowRule.cs` for workflows); platform owners review it, and security owners too when a credential, a grant or a policy moves.
