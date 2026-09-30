# Plan: replace Codefresh with GitHub Actions and deprecate Codefresh

Status: PROPOSAL for review (2026-09-29). Nothing in this plan has been implemented. It supersedes no design record until
the ADR of work item WI-01 is accepted. Basis: five parallel read-only audits of the platform pipelines, the app
pipelines, the Azure/identity/network layer, the tests/contracts/tooling layer and the design/docs record, plus live
checks of the GitHub repositories on 2026-09-29. File and line references are to this repository at commit `4e78776`.

## 1. Bottom line

- **It is feasible, and cheaper to run.** Both repositories are public, so standard GitHub-hosted runners cost nothing
  and have no minute cap. A hosted runner (4 vCPU, 16 GB RAM, 14 GB SSD, Docker preinstalled) per job also removes
  the single-node bottleneck that shapes the Codefresh pipelines today (one D4as_v6 node, at most two SQL Servers at once,
  shared volume, marker files, `wt/*` worktrees).
- **The pipeline scripts are mostly portable; the governance is not.** Of about 40 PowerShell scripts, only about 10 need real
  changes for `CF_*`, `cf_export`, `CF_VOLUME_PATH`, the Codefresh OIDC variables or the Codefresh REST API
  (section 5). The hard part is that the platform's rules and tests encode "Codefresh builds, GitHub Actions is off":
  tool-boundary rule TB24, TB01-TB03, TB13-TB22, contract C25, capabilities CAP-CF-001..014, the `codefresh/ci` and
  `codefresh/env-checks` required checks, and the Kyverno signer policy that pins the Codefresh OIDC identity. That
  is a design change ("changing a lane", [tool-boundaries.md](tool-boundaries.md) line 142) and needs a new ADR.
- **The one architectural decision that matters** (D1 below): GitHub Actions only runs workflows from the repository
  where the event happens, and today's rule (ADR-D18) is that app repositories receive no platform file. Organization
  "required workflows" that could avoid this need GitHub Enterprise, and the org is on the Free plan. Recommendation:
  each app repository gets a tiny caller stub (about 15 lines, no logic) that calls reusable workflows in this
  repository. All logic, scripts and pinning stay here. ADR-D18 is amended, not abandoned.
- **Estimate** (rough): about 30-40 engineer-days of change across 10 workstreams, then a soak of about 2 weeks before
  Codefresh is deleted. The time-bound P1 evidence (ten green master builds stands at 1 of 10; five green nightlies not
  started) barely counts against a switch now, and would count heavily later. Recommendation: migrate now, inside P1.

## 2. Verified facts and corrections to the docs

| Fact (live, 2026-09-29) | Consequence |
|---|---|
| `basic-environment-octopus-codefresh` is **public** (README says private, and the design's cost notes say its minutes would be billed) | Free unlimited standard-runner minutes here too. Fix README and design §3.5 |
| `20260923-001` (app #1) is a public fork of `ClearMeasureLabs/bootcamp-palermo-workorders`, Actions **enabled**, three legacy workflows active (`Build`, `Deploy`, `Render diagrams (check)`), no secrets or variables, fork-PR approval policy `all_external_contributors` | Docs say Actions is disabled there (ADR-IR26, R21). The API says enabled with 0 runs listed. Unverified whether they were ever triggered (rate limit hit before I re-checked). Enabling by push would run `Build` (on every push) and `Deploy` (after `Build` on master). See WI-13 |
| Org `clearmeasure-aisf-sample-apps` is on the **Free** plan | 20 concurrent hosted-runner jobs org-wide; no required-workflows rulesets; environments with secrets, deployment-branch rules and required reviewers **are** available because the repos are public |
| Rulesets: env repo `main-protection` requires `codefresh/env-checks` (admin role bypasses); app repo `master-protection` requires `codefresh/ci` (no bypass) | Both required checks must be swapped without blocking merges (section 8) |
| No `codefresh/env-checks` status on current `main` commits (trigger paused for demos) | The env-checks gate is effectively not enforced today. An Actions version fixes that for free |
| Codefresh plan: docs disagree (PRO_1 with 3 concurrent builds vs BASIC_1 with 1); builds pool: docs say 0-2 scale-from-zero, Terraform default is 1-3 with a warm node | Re-baseline before comparing costs and durations. Licence expiry and price are not in the repo: ask the account owner |
| The 5,000/hour GitHub API budget was exhausted during this analysis by parallel readers | Migration tooling and tests must be frugal (REST, one call per check), as the feature-loop rules already say |

## 3. What Codefresh does today (inventory)

Seven platform pipelines (project `platform-env`, `codefresh/platform/`):

| Pipeline | Trigger | Needs | Notes |
|---|---|---|---|
| `env-checks` | push to any branch except `main` | nothing secret; full-history clone | Nine parallel checks; posts `codefresh/env-checks` |
| `ci-image-dotnet` | push to `main` touching `containers/platform/**`; weekly cron Mon 06:00 UTC | ACR push token | Builds and keyless-signs `platform/ci-dotnet` and `platform/db-tools-mssql` |
| `conformance-arm` | cron weekdays 07:00 UTC (enabled) | Octopus key, Azure SP secret, GitHub PAT | Force-sleeps both clusters, holds sleep, pushes sandbox commits, queues other builds through the Codefresh API |
| `conformance` | queued by `conformance-arm`, or manual | same | Up to 6 h; publishes TRX to the sandbox repo; teardown |
| `conformance-destructive` | cron Sunday 08:00 UTC (enabled) | same | Nonprod only, 4 h, unattended |
| `fixtures` | manual | ACR push token | Unsigned fixture image |
| `registry-retention` | nightly 03:00 UTC (currently disabled) | ACR retention token | Deletes old tags with safeguards |

App pipelines (`codefresh/apps/<app>/`, scaffolded from three starters in `codefresh/templates/`):

| Pipeline | Trigger | Shape |
|---|---|---|
| `workorders/ci` | push to any non-`master` branch of the app repo (never PRs, never forks); posts `codefresh/ci` | `prepare` then six parallel gates (SQL build, acceptance, code analysis, SQLite, Qodana, security scan) then `gate` (its exit code is the status) |
| `workorders/preview` | PR opened, synchronized or labeled `preview` against `master`, same-repo only | Builds three signed preview images; Argo CD deploys them |
| `workorders/release` | push to `master`, one build at a time, every commit gets a release | `prepare` (CI-tree skip via `codefresh/ci` status), gates, package, stage images, image reuse, three signed images, SBOM and SLSA attestations, tag lock, Octopus packages, build info and release |
| `sandbox/ci`, `sandbox/release` | same, on `platform-sandbox` `main` | Small conformance fixture |

Supporting objects (from `register.ps1`, `runner/values.yaml`, the Terraform): runtime `aks-platform-build/codefresh` on
the `aks-platform-build` cluster (system pool always on; `builds` pool D4as_v6 with a Docker-in-Docker sidecar sized
3 CPU / 11 GiB); five account-wide registry integrations and four secret contexts (`platform-octopus`,
`platform-registry`, `platform-registry-retention`, `platform-conformance`) plus optional `app-workorders-ci`; a git
integration and one webhook per repository; five 90-day ACR scope-map tokens; one Entra service principal with a
90-day client secret (`sp-platform-conformance`); keyless signing through the Codefresh OIDC provider.

## 4. Target architecture: decisions and recommendations

### D1. Where the workflows live (blocking)

| Option | Verdict |
|---|---|
| **A. Reusable workflows in this repo, thin caller stubs in each app repo** | **Recommended.** Works on the Free plan. Logic, scripts and versions stay here; the app repo holds three stub files and settings (environments, secrets, variables, ruleset check names). |
| B. Organization "required workflows" ruleset pointing at this repo | Not available: Enterprise-only, org is Free. Also covers only pull requests, not pushes to `master`. Verify once in the UI before discarding. |
| C. Poller in this repo (cron every few minutes checks app repos, dispatches runs, posts statuses) | Keeps app repos file-free but adds minutes of latency, cron drift, a token that can write statuses to app repos, and no natural PR check. Fallback only if the user insists on zero files in app repos. |
| D. Full copies of workflows in each app repo | Rejected: duplicates logic, loses the "authors cannot change the pipeline" property completely. |

Caller stub, illustrative (one per pipeline; the release stub triggers on `master` only):

```yaml
name: platform-ci
on:
  push:
    branches-ignore: [master]
permissions:
  contents: read
jobs:
  ci:
    uses: clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/.github/workflows/app-ci.yml@main
    with:
      app: workorders
    secrets: inherit
```

Trust model (this is what ADR-D18 protected, and how it is preserved):

- A branch author can edit the stub, so they can change `with:` inputs and which secrets are passed. They cannot change
  the reusable workflow or any script: those load from `main` of this repository, protected by its ruleset.
- Release credentials (ACR push token, Octopus key) live only in a GitHub **environment** `release` of the app repo,
  restricted to deployment branch `master`. A feature branch cannot read them, whatever its stub says. Environment
  protection rules and deployment branches are available on the Free plan for public repositories.
- CI-only secrets (`AI_OPENAI_*`) sit in an environment or repo scope that branch pushes may read, exactly as
  `app-workorders-ci` behaves today.
- The reusable workflow's first job checks `github.repository` and `github.ref` against `apps/<app>.yaml`
  (`repositories`, default branch), so a foreign repo or branch cannot drive the release path.
- Fork pull requests trigger nothing, as today: `push` only, no `pull_request_target`. Keep "a maintainer pushes a
  reviewed commit to a branch" (ADR-IR26).

### D2. Runner

**Recommended: standard GitHub-hosted runners** (free, no cluster to run, OIDC to Azure). Keep the runner label in a repo
variable (`runs-on: ${{ vars.CI_RUNNER || 'ubuntu-24.04' }}`, with `fromJSON` when a label array is needed) so a later
flip to self-hosted is configuration, not a rewrite.

Why it fits: every gate becomes its own job on its own VM, so the one-node serialization disappears; the 14 GB disk is
per job; Docker is preinstalled, so SQL Server 2022 as a container, Playwright and buildx work; nothing needs a private
network today (section 6).

Watch items (validate in the spike, WI-02): SQL Server reachable as `localhost:1433` for the app's DbUp certificate rule
(ADR-IR24); Qodana and Playwright memory; the 6-hour job cap versus the `conformance` run (currently `timeout: 6h`);
the 20-concurrent-job org cap; cold NuGet and Docker layers (mitigate with `actions/cache` and a buildx registry cache).

Escape hatch: Actions Runner Controller scale sets on the existing `aks-platform-build` cluster (`containerMode: dind`,
privileged; toleration for `codefresh.io/builds`; x86 `builds` pool; pods pull `ci-dotnet` with an imagePullSecret).
Needed only if later network hardening (AKS authorized IP ranges, Key Vault or Octopus IP allow-lists, design Q7)
makes hosted-runner egress unusable. It keeps the cluster and its cost (about $55/month plus a warm node) and forces a
rewrite of TB21 and CAP-CF-002/003/012 rather than deleting them.

### D3. Toolchain image

Keep `platform/ci-dotnet` (pinned tag and digest; it holds pwsh, dotnet SDK, docker CLI, cosign, syft, crane, gitleaks,
octopus, az, terraform, sqlcmd, Chromium and more). Because this repository is public and the image holds no secret,
**publish it to GHCR as a public package** (built and signed by the Actions `ci-image` workflow) as well as ACR. GHCR
pulls need no stored credential and are local to the runners, and `cf-platform-pull` and `cf-platform-ci` tokens can be
retired. Run gate steps with a small composite action that does `docker run --network host` with the workspace and the
Docker socket mounted, rather than a `container:` job: `container:` jobs put services on a job network, not on
`localhost`. App images stay in ACR (`apps/<app>/...`), because the Octopus feed, Kyverno path policy and pins use it.

### D4. Signing and the admission policy

- Cosign stays keyless. The signer becomes the GitHub OIDC issuer (`https://token.actions.githubusercontent.com`) with the
  workflow identity as subject. `id-token: write` is needed on the release job only.
- For a reusable workflow the Fulcio certificate names the **called** workflow in this repository
  (`.../basic-environment-octopus-codefresh/.github/workflows/app-release.yml@refs/heads/main`), and the app repository
  and ref appear as certificate extensions. The Kyverno policies (floor `policies/kyverno/base/verify-release-signatures.yaml`
  lines 100-101 and 172-173; per-app `gitops/platform/tenant/templates/signer-policy.yaml` lines 88-89, values
  `cfAccountName`/`cfAccountId`) must therefore match the subject **and** the source-repository and source-ref
  extensions, otherwise a feature branch could call the reusable workflow at `main` and mint a valid identity. Exact
  Kyverno key names for the extensions: verify in the spike.
- **Dual trust during transition.** Add the GitHub identity next to the Codefresh identity in all three policies, first.
  Admission is evaluated only on create and update, so running pods are unaffected, but prod is Deny: dropping the
  Codefresh identity before every retained release (the last ten, CAP-CF-010) has been re-released would block a rollback.
- The tenant chart needs a descriptor-driven value (GitHub org and repository) because the repository name
  (`20260923-001`) differs from the app name (`workorders`), and TB22 forbids naming apps in platform files.
- Provenance: keep the step-authored SLSA predicate at first with a new builder id; consider
  `actions/attest-build-provenance` later (Kyverno keeps provenance in Audit).

### D5. Azure and registry credentials

- Conformance: add a federated credential on the existing `sp-platform-conformance` app registration (subject
  `repo:clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh:environment:conformance`, audience
  `api://AzureADTokenExchange`). Existing roles are reused; the client secret is retired, and `aks-power.ps1`
  keeps working (it only needs a token). Never grant roles to `:pull_request` subjects. Put it in `terraform/foundation`
  (only the provisioner writes roles; `terraform/build` must stay grant-free under TB21).
- ACR push for app images: **phase 1, keep the existing repository-scoped tokens** (`cf-apps-release`,
  `cf-apps-preview`) and store them as `release`/`preview` environment secrets; they keep the tag lock
  (`metadata/write`) and repository scoping. **Later (optional):** OIDC with per-app identities needs ACR ABAC mode, which
  invalidates every AcrPull grant (kubelet, Kyverno, Octopus feed): large change, defer.
- `cf-platform-retention` becomes a `retention` environment secret (or `-Auth aad` with a suitable role: verify that
  AcrDelete plus metadata write does not widen scope).
- Octopus: the Space Manager API key becomes an environment secret (`release`, `conformance`); URL and space id become
  variables. Octopus Cloud IP allow-lists are unknown: check before the spike, since hosted-runner IPs are dynamic.
- The org PAT of `platform-conformance` stays a secret for now (name it `CONFORMANCE_GITHUB_TOKEN` and set it as
  `GITHUB_TOKEN` in the step env, because Actions injects its own `GITHUB_TOKEN`); replace with the statuses-only
  GitHub App already wired in R16 when practical.
- A new operator script `github/register.ps1` (idempotent, like `codefresh/register.ps1`) creates environments,
  deployment-branch rules, secrets, variables and ruleset check names per app from `apps/<app>.yaml`.

### D6. Required checks and status names

Actions produces check runs (`GET /commits/{sha}/check-runs`), not commit statuses. That is what the global rule
"API-verify CI via check-runs" expects, and it is why Codefresh statuses never appeared there. Plan:

- One terminal `gate` job per pipeline, always run (`if: always()`), failing unless every needed job succeeded (or was
  legitimately skipped). Required check names: `env-checks / gate` here and `platform-ci / gate` in app repos (check
  the exact composed `<caller job> / <called job>` name in the spike).
- Update together: rulesets, `contracts/platform-contracts.yaml` (`requiredStatus`, `statuses`),
  `.claude/factory-loop.json`, `.claude/skills/*`, `board.ps1`, `CLAUDE.md`, `CODEOWNERS`, and `ci-tree.ps1` (release reads
  the CI result of an equivalent tree; port to the check-runs API, still anonymous on a public repo).
- Bonus: a re-run of a release posts a check run natively, fixing the Codefresh limitation that API re-runs post no
  status (`codefresh/apps/workorders/README.md` line 276).

### D7. Concurrency, schedules, long jobs

- CI: `concurrency: {group: ci-${{ github.ref }}, cancel-in-progress: true}` (replaces the branch-onCreate termination).
- Release must not lose commits. GitHub keeps only the newest pending run per group and cancels the middle ones, so
  "queue every master commit" is not reproducible with one group. Use one group per SHA (`release-${{ github.sha }}`,
  no cancel). Releases are idempotent (`image_reuse`, `--ignore-existing`, version = git first-parent height), and
  Octopus orders deployments. Confirm in the spike that two concurrent releases are safe.
- Conformance workflows share one group per tier (`conformance-nonprod`, `-prod`) with `cancel-in-progress: false`, so
  the arm, the suite, the destructive run and the e2e pass never overlap. Verify that a cancelled pending run cannot
  skip a teardown or a sleep-hold release (`if: always()` on teardown).
- Crons are UTC, best-effort (delays, occasionally dropped) and run only from the default branch. Public repos disable
  schedules after 60 days without activity; this repo has constant activity (pin commits). Keep `registry-retention` disabled
  until its dry run is reviewed (P1-13), for example behind a repo variable.
- The 5-minute log heartbeats in `conformance-*` were only there for Codefresh's 45-minute inactivity kill. Harmless; can stay.

### D8. Repository layout

Add a new `ci/` tree and `.github/workflows/`, leave `codefresh/` untouched until decommission (both engines must run in
parallel during the soak, and the rules and tests read `codefresh/` until they are repointed):

```text
.github/workflows/   env-checks.yml ci-image.yml fixtures.yml registry-retention.yml
                     conformance-arm.yml conformance.yml conformance-destructive.yml e2e-pass.yml
                     app-ci.yml app-preview.yml app-release.yml   (reusable, workflow_call)
                     project-board.yml                            (unchanged)
.github/actions/     run-in-ci-image/  octopus-handoff/ ...       (composite actions)
ci/platform/scripts/ ports of codefresh/platform/scripts
ci/apps/<app>/scripts/ + ci/apps/<app>/app.env   app-owned scripts (scaffold-then-own is kept)
ci/templates/{minimal,multi-image,dotnet-buildps1}/   starters, as reusable-workflow inputs plus scripts
github/register.ps1  environments, secrets, variables, rulesets (replaces codefresh/register.ps1)
```

## 5. Codefresh construct to Actions mapping

| Codefresh | GitHub Actions |
|---|---|
| Spec (triggers, contexts, runtime, concurrency) | Caller stub (`on:`, `with:`, `secrets:`) plus repo settings written by `github/register.ps1` |
| `${{CF_REVISION}}`, `CF_BRANCH`, `CF_SHORT_REVISION`, `CF_BUILD_ID/URL`, `CF_REPO_*`, `CF_PIPELINE_NAME` | `github.sha`, `github.ref_name`, `substr(github.sha,0,7)`, `github.run_id` and run URL, `github.repository*`, `github.workflow` |
| `CF_PULL_REQUEST_{NUMBER,LABELS,HEAD_BRANCH}` | `github.event.pull_request.*`; the preview guard becomes an `if:` on label and `head.repo.full_name == github.repository` |
| `cf_export VAR` (and `--mask`) | `$GITHUB_ENV`, job `outputs`, `::add-mask::` |
| Shared `/codefresh/volume` (markers, TRX, nupkg, image contexts, NuGet and Qodana caches, worktrees) | Job outputs, `upload-artifact`/`download-artifact`, `actions/cache`; per-job checkout replaces `wt/*` |
| `when.steps[].on: success/finished`, `steps.any`, `condition` | `needs:` with `if: always() && needs.x.result == ...`, job outputs |
| `fail_fast: false` plus `strict_fail_fast`; "never wait on a skipped step" hacks and marker files in `gate.ps1` | Native: a `gate` job over `needs.*.result`; the early-exit-instead-of-skip pattern and marker files can go |
| `type: git-clone` | `actions/checkout` (`fetch-depth: 0` where history matters: version, env-checks bot audit); env repo checkout pinned to the caller's ref |
| `type: build` with `cosign.sign`, `tags`, `cache_from`, `buildkit` | `docker/login-action`, `docker/build-push-action` (`cache-from`, `build-args`), `sigstore/cosign-installer` plus `cosign sign` on the digest |
| `services:` SQL Server with `shared_host_network` and readiness | Job `services:` with health cmd and host port, or `docker run --network host` (validate localhost, spike) |
| Contexts, registry integrations | Environment and repo secrets, `docker/login-action` |
| `hooks.on_finish` | Final job with `if: always()` writing `$GITHUB_STEP_SUMMARY` |
| Build annotations (`POST /api/annotations`) | Step summary, job outputs, `::notice::` |
| Pipeline chaining via the Codefresh run API (`conformance-arm` queues `conformance`, sandbox pipelines) | `workflow_call` for shared pieces, `gh workflow run` / `workflow_dispatch` for sandbox triggers; locate a dispatched run by `head_sha` |
| Codefresh OIDC for cosign (`CF_OIDC_REQUEST_*`) | `ACTIONS_ID_TOKEN_REQUEST_*` (audience `sigstore`) or cosign's built-in GitHub provider |
| `verify_trigger` step | Not needed |

Scripts needing a port (the rest run unchanged once their environment variables are supplied):
`prepare.ps1`, `ci-tree.ps1`, `preview-guard.ps1`, `supply-chain-step.ps1` (the `cf_export` helper and volume paths),
`supply-chain.ps1` (OIDC variables, builder id, `codefresh-build-id`), `buildinfo.ps1` (`BuildEnvironment = 'Codefresh'` and
`CF_*` fields), `release-notes.ps1` (build links), `conformance-arm.ps1` and `conformance-run.ps1` (export helper, annotations,
Codefresh REST calls), `validate-all.ps1` (`CF_VOLUME_PATH`, `CF_BRANCH`). The three starter templates have drifted from
`workorders` (`minimal/pipelines/ci.yml` is empty, contradicting its README): port `workorders` first, derive the
templates from it.

## 6. Network exposure (what the audit found)

ACR is public (Standard SKU), all three AKS API servers are public with no authorized ranges, Key Vault and state storage
are public with Entra RBAC only, Octopus is Octopus Cloud. So hosted runners reach everything today with valid
credentials, and nothing requires a private path. Only the conformance workflows touch clusters (ARM and AKS
APIs); CI and release need ACR, Octopus, GitHub and Sigstore. The exposure is a **future** constraint: design Q7
(authorized ranges in P4 hardening) and any Octopus IP allow-list conflict with hosted-runner egress. Then use the ARC
escape hatch (a stable egress IP; the build cluster egress is not pinned in Terraform today) or larger static-IP runners
(paid, avoid).

## 7. Work breakdown (feature-loop tree)

Suggested board shape: one epic ("Replace Codefresh with GitHub Actions") with the children below as sub-issues, worked
children-first under the feature-loop rules. Sizes: S under a day, M one to three days, L a week or more. Nothing here
has been filed.

| ID | Item | Size | Depends on |
|---|---|---|---|
| WI-01 | ADR-IR35 "GitHub Actions is the build server": supersede ADR-C6, ADR-D17, ADR-D18 (amend), ADR-IR26/R21, ADR-IR34 decisions 4, 13, 16, 19, 28; amend ADR-D2, D8, D11, IR10, IR19, IR32, IR33; update design §5.1 (TB1-TB5, TB15), §6.3, §7.0, §9, §3.5; owner and security-owner review | M | user decisions D1-D8 |
| WI-02 | Spike on a scratch repository: workorders CI and release on hosted runners; measure against the `build-duration.md` baseline (1.2x gate); SQL on localhost, Playwright, Qodana, GHCR image pull time, keyless cosign identity and Kyverno extension keys, ACR push and tag lock with the scope token, Octopus reachability from hosted IPs, composed check names, concurrent releases | M | none |
| WI-03 | Lane-change PR: redesign TB24 as an allowlist of platform workflows with per-file reason, secret allowlist, SHA-pinned actions, `id-token: write` only on release/conformance workflows, `self-hosted` only if listed; repoint TB01/02/03/06/13/14/16/18/21/22 target paths and the `IsReleasePipeline`/`IsConformancePipeline`/`IsWakeScript` globs to `.github/workflows` and `ci/`; update `contracts/platform-contracts.yaml` (new `ci` section, `statuses`, `requiredStatus`, signer), C01 `need` list, C25; update `docs/tool-boundaries.md`, README line 44, CODEOWNERS, `project-board.yml` header. Must land before any new workflow, or the first workflow fails its own env-checks | L | WI-01 |
| WI-04 | `env-checks.yml` (push to non-`main`, `fetch-depth: 0`, nine checks as parallel jobs plus `gate`); port `validate-all.ps1` env fallbacks; the required check becomes real | M | WI-03 |
| WI-05 | `ci-image.yml`: build, sign and push `ci-dotnet` and `db-tools-mssql` to GHCR (public) and ACR; smoke jobs; weekly cron; composite action `run-in-ci-image` | M | WI-03 |
| WI-06 | `fixtures.yml` and `registry-retention.yml` (dry-run first) | S | WI-03 |
| WI-07 | Reusable `app-ci.yml`: job graph (prepare, six gates, gate), NuGet cache, artifacts, docs-only skip, `CODE_CHANGED`; port `prepare`, `changed-paths`, `gate` (native `needs` result), `trx-summary`; sandbox first, then workorders | L | WI-03, WI-05 |
| WI-08 | Reusable `app-preview.yml`: label and same-repo guard, three signed preview images | S | WI-07 |
| WI-09 | Reusable `app-release.yml`: prepare (CI-tree skip via check-runs), gates, package, stage images, image reuse, matrix image build and sign, supply chain and reuse, Octopus handoff (`octopus-handoff` composite), `wake-nonprod`; per-SHA concurrency; shadow mode (build, no push, no Octopus) | L | WI-07 |
| WI-10 | Signer chain: dual-trust Kyverno floor and tenant policies with extension matching; descriptor-driven repo value in the tenant chart; nonprod Audit first; update offline Kyverno tests and `supply-chain-evidence.md` | M | WI-02, WI-01 |
| WI-11 | `github/register.ps1`, app repo settings: environments (`release`, `preview`, `ci`) with deployment branches, secrets, variables; caller stubs and PRs into `platform-sandbox` and `20260923-001`; app-scoped path and blast-radius rule for the stub files (CAP-KIT-004) | M | WI-09 |
| WI-12 | Entra federated credential for `sp-platform-conformance` (`terraform/foundation`); Owner script and role review | S | WI-01 |
| WI-13 | Fork hygiene for `20260923-001`: disable the three legacy workflows through the API (not by editing the files; reversible), confirm they have never run, choose stub file names that do not collide (`platform-ci.yml`, `platform-release.yml`, `platform-preview.yml`), record in R21 | S | user decision |
| WI-14 | Conformance workflows: `conformance-arm`, `conformance`, `conformance-destructive`, `e2e-pass` with shared tier concurrency, environment `conformance`, step summaries instead of annotations, TRX publish; port `conformance-arm.ps1`, `conformance-run.ps1` and their tests; environment approval as the missing gate for destructive runs | L | WI-03, WI-12 |
| WI-15 | Harness: replace `ICodefreshApi` with an Actions client (dispatch, run, jobs, cancel, rerun, check-runs, runners); remove `CODEFRESH_API_KEY` and `CodefreshUrl`; update settings, secrets, `platform.settings.json`, README and the offline tests | M | WI-03 |
| WI-16 | Offline invariant tests against workflow YAML: fail-closed builds, never-deploy, no fork triggers, release reuse graph, hold ordering, no cloud identity outside conformance; a workflow-YAML parser helper replacing `CodefreshRepository` | L | WI-07, WI-09, WI-14 |
| WI-17 | Live tests re-implemented (CAP-CF-004..010, 013, 014, CAP-HARNESS-003, Kit live tests, end-to-end test); delete or replace CF-001/002/003/012; catalogue, owner enum, rendered `docs/capabilities.md`, all in one commit per cluster (CAP-HARNESS-001 fails otherwise) | L | WI-15, WI-16 |
| WI-18 | Onboarding tool and descriptors: `codefresh` block replaced (`ci:` with workflow names and signer identity), schema, `AppDescriptor`, `AppFilesCheck`, `Scaffolder` (`--ci`), `Inventory`, `LiveCheck`, fixtures; freeze becomes a workflow disable through the API or a variable guard; retire `register.ps1` tests | M | WI-11 |
| WI-19 | Tooling: `.claude/factory-loop.json`, `board.ps1`, `settings.json`, skills, `Check-StalledLanes.ps1`, `CLAUDE.md`; keep signal names consistent with WI-06 rulesets | S | WI-04, WI-07 |
| WI-20 | Cutover, sandbox then workorders: run both engines, compare, swap required checks, disable Codefresh triggers (section 8) | M | WI-04..WI-19 |
| WI-21 | Decommission (section 9) | M | WI-20 plus soak |
| WI-22 | Docs and diagrams: README, `bootstrap.md`, `onboarding.md`, `scripting.md`, runbooks (`conformance`, `build-duration`, `credential-rotation`, `supply-chain-evidence`, `sleep-and-wake`, `demo-commit-to-prod`), walkthroughs 01, 06, 07 (Lab 24 is almost all Codefresh handshake), `preview-codefresh.md` retired, about 35 PlantUML sources and their PNGs and manifest hash | L | progressively, final in WI-21 |

Critical path: WI-01, WI-02, WI-03, then WI-04/05, WI-07, WI-09, WI-10, WI-11, WI-20. WI-14 to WI-17 (conformance and tests)
can run beside app-pipeline work after WI-03 but must finish before Codefresh is deleted, because the nightly and
destructive suites are the evidence that the platform still works.

## 8. Cutover and rollback

1. **Shadow.** Enable the Actions `ci` and `preview` workflows next to Codefresh (not required). `release` runs in
   shadow mode: build and verify, no push, no signing to the real tags, no Octopus call. Reason: release tags are
   write-locked after signing, so two engines cannot both publish the same version; the first publisher wins and the
   other must reuse.
2. **Compare.** Ten consecutive green Actions CI runs and shadow releases per repo, durations within the 1.2x gate, gate
   parity (same pass and fail on the sandbox fixtures `toggles/failing-test` and `failing-migration`).
3. **Switch, per repo (sandbox first).** Add the new required check to the ruleset **before** removing the old one, so
   merges never block; enable the real release workflow and disable Codefresh's `release` trigger in the same change
   window, using a quiet period so no commit is released twice. The first Actions release is signed with the GitHub
   identity, which requires the dual-trust Kyverno change (WI-10) to be live.
4. **Verify.** Octopus release created with `app-commit:` first line and build info; tdd auto-deploys; Kyverno admits the
   pods in nonprod (Audit) and prod (Deny); `cosign verify` passes for the new identity.
5. **Rollback (documented nowhere today, define it now).** Before the soak ends: re-enable the Codefresh trigger and
   re-require `codefresh/ci`. Images already signed by either identity stay valid because both identities are trusted.
   After the Codefresh identity is dropped from Kyverno, rollback of the CI engine is no longer possible; that is
   the point of the soak.

## 9. Decommission checklist (WI-21, only after the soak)

Codefresh (mostly by hand; `register.ps1` never deletes, and deleting a pipeline drops its build history, so export first
if evidence is needed):

- Disable then delete all 12 pipelines; delete projects `platform-env`, `workorders`, `sandbox`.
- Delete contexts `platform-octopus`, `platform-registry`, `platform-registry-retention`, `platform-conformance`,
  `app-workorders-ci`, `github-aisf-sample-apps-token`; registry integrations `acr-apps-release`, `acr-apps-preview`,
  `acr-platform-ci`, `acr-platform-pull`; git integration `github-aisf-sample-apps`.
- Make another runtime default (Codefresh refuses to delete the default), then delete runtime
  `aks-platform-build/codefresh` and agent `aks-platform-build_codefresh`; confirm the dead `trf-CodeFresh-dev/codefresh` runtime is gone.
- Revoke API keys (operator `CF_API_KEY`, runner `codefresh-token`, optional conformance key); cancel the subscription
  (renewal date unknown: ask the account owner).

GitHub: remove the Codefresh webhook from every repository; remove `codefresh/ci`, `codefresh/env-checks` from the rulesets
(after the new checks have been required for the soak); revoke the org PAT used by the Codefresh git integration if unused.

Azure: uninstall the `cf-runtime` Helm release and delete Secret `codefresh-token`; delete the five ACR scope-map tokens
and token objects that are no longer used; delete the `sp-platform-conformance` client secret; decide the fate of
`aks-platform-build` (see below); update budgets, Terraform descriptions, and `terraform/build` tests.

Repository: delete `codefresh/`; remove `retiredPaths` only after the rules stop reading them; delete CAP-CF tests and
entries, C25, `register.ps1` tests; remove the Kyverno Codefresh identity **only after** every retained release
(last ten) has been re-released or the retention window has passed; remove `ci-builds` links to `g.codefresh.io`;
purge Codefresh wording from comments, Dockerfile labels and docs; regenerate diagrams and `docs/capabilities.md`.

`aks-platform-build`: with hosted runners the cluster has no job. Destroying it saves about $55/month plus about
$125-145/month for the warm build node (nothing else in the repo depends on it), but `rg-platform-build` also holds the
registry and carries a CanNotDelete lock and `prevent_destroy`, so only the cluster module is destroyed, with a
deliberate temporary lock lift. Keep it until the ARC escape hatch is no longer wanted.

## 10. Risks and open questions

| Risk | Mitigation |
|---|---|
| Amending ADR-D18 (stubs and settings in app repos) is a policy change, and the fork's legacy workflows come alive when Actions runs | Explicit ADR (WI-01); disable legacy workflows through the API before the first stub lands (WI-13); unique stub file names |
| Kyverno prod is Deny: an unaccepted signer identity blocks deployments; a too-loose subject lets a feature branch sign | Dual trust first; match certificate extensions; nonprod Audit soak; security-owner review |
| Hosted runners are cold: NuGet, Docker layers, Playwright; SQL on `localhost`; 6-hour cap for `conformance` | Spike (WI-02); `actions/cache`, buildx cache; split or self-host only the long conformance job if it exceeds the cap |
| Concurrency semantics differ (pending runs are dropped) | Per-SHA release groups plus idempotent release; tier groups for conformance; verify teardown always runs |
| Hosted-runner IPs versus later network hardening or an Octopus allow-list | Check the Octopus Cloud IP restriction now; runner label as a variable; ARC escape hatch |
| Tests and rules go silently green when `codefresh/` moves ("absent paths skip a rule") | Repoint rules in WI-03 first; keep `codefresh/` until WI-21; CAP-HARNESS-001 forces catalogue and test changes together |
| Evidence-window clocks restart (nightlies, ten green master builds, 1.2x build time) | Migrate now while the streak is 1 of 10; state the reset in the ADR |
| 20 concurrent jobs org-wide, 5,000/hour API budget | Keep gate fan-out modest; REST only; reuse tokens |
| Shared PAT still used by Octopus, Argo CD and the conformance harness | Split into least-privilege GitHub Apps as part of WI-14/WI-19 (R3, R16) |
| Docs drift (PRO_1 vs BASIC_1, builds pool, README visibility, fork Actions state) | Re-baseline in WI-22; verify the fork Actions state first |

Decisions needed from the owner (defaults in brackets are what this plan assumes):

1. Amend ADR-D18 so app repos carry caller stubs and settings? [yes, option A]
2. Hosted runners, with ARC as escape hatch only? [yes]
3. May the three legacy workflows in the fork `20260923-001` be disabled through the API, and stub files added there? [yes]
4. Migrate now, inside P1, restarting the evidence streaks? [yes]
5. Keep ACR scope-map tokens now and defer OIDC for ACR push? [yes]
6. Codefresh licence renewal date and any commitment? [unknown, needed for the decommission date]
7. Should the CI toolchain image move to a public GHCR package? [yes]

## Appendix: source audits

The audits behind this plan (5 read-only reports, this session) cover: `codefresh/platform` and `register.ps1` and
`runner/values.yaml`; `codefresh/apps` and `codefresh/templates`; `terraform/`, `policies/kyverno`, secrets and network;
`tests/`, `tools/Platform.Onboarding`, `contracts/`, `catalogue/`, `.claude/`; and the design record
(`design/platform-design.md`, `docs/`). Key anchors: TB24 `tests/Platform.Conformance.Offline/Kit/Boundaries/GitHubWorkflowRule.cs`;
required checks `contracts/platform-contracts.yaml` lines 134, 659-660; signer `contracts/platform-contracts.yaml` lines 507-511,
`policies/kyverno/base/verify-release-signatures.yaml`, `gitops/platform/tenant/templates/signer-policy.yaml`; runner
`codefresh/runner/values.yaml`, `terraform/build/`; rationale for Codefresh
`design/debate/round-1-codefresh-engineer.md` lines 154 and 210 (no written "why not GitHub Actions" ADR exists);
cutover criteria `docs/cutover-and-decommission.md` lines 20-30.
