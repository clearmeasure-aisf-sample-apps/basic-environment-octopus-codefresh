# App repository personal access token audit

Read-only audit of every GitHub credential that the app repository `clearmeasure-aisf-sample-apps/20260923-001`
(workorders, default branch `master`) references, and of what this repository's Codefresh and Octopus configuration uses
to reach it. Work item #66. The owner's goal is that no personal access token (PAT) is used anywhere; this repository
already retired its own (history section of [credential-rotation.md](../runbooks/credential-rotation.md), which is the
only page allowed to name the retired variables, so this page describes them instead of naming them).

Only names, paths, line numbers and classifications are recorded. No secret value was read, and none is on this page.

## Result

- **Four PAT-class findings in the app repository**, each with a child issue: A1 (a retired operator PAT variable, first
  in the token order of the feature-loop scripts), A2 (a stale reference to the retired board-workflow PAT secret), A3
  (`COPILOT_PAT`, a workflow secret), A4 (the personal `gh` token that the legacy AI Factory executor copies into
  containers and cluster Secrets).
- **No literal token or key is committed** in the app repository (searched for the personal, OAuth, server and
  fine-grained token prefixes and for `x-access-token` URLs: the only hits are variable names, a regular expression that
  checks prefixes, and a documentation placeholder).
- **The app repository has no Codefresh pipeline files, no Octopus credential and no App key.** Its `.octopus/` folder
  is the pre-migration Octopus config (no Git credential, no GitHub variable but a NuGet feed name). Every pipeline,
  trigger and Octopus project for it lives in this repository.
- **Every other GitHub credential on the path is a GitHub App or the workflow `GITHUB_TOKEN`**, and needs no
  replacement.
- **A guard gap exists**: the app repository has an offline NUnit suite that reads tracked files but nothing that
  forbids a PAT name; see [Guard gap](#guard-gap).

## Scope and method

| | |
|---|---|
| Audit date | 2026-09-30 |
| App repository commit read | `6b02ffb95693434f87d561fb8a8717fbd2cfb0ed` (`master`, 1388 tracked files); the clone was fresh, read-only, kept outside both repositories, and its HEAD and work tree were unchanged after the audit. Nothing was pushed to it |
| Searched (app repository) | every tracked text file: `.github/workflows`, `.octopus`, `.octopus_original_from_od`, `.claude` (tooling, skills, `factory-loop.json`, settings, hooks), `.cursor`, `.bob`, `.githooks`, `scripts`, `build*.ps1`, `PrivateBuild.ps1`, `AcceptanceTests.ps1`, `BuildFunctions.ps1`, `NuGet.Config`, `Dockerfile`, `docs`, `README.md`, `CLAUDE.md`, `AGENTS.md`, `src` |
| Search terms | the retired PAT names, `GH_TOKEN`, `GITHUB_TOKEN`, `_PAT`, `PAT`, `secrets.`, `Authorization`, `Bearer`, `gh auth`, `x-access-token`, `git credential`, `credential.helper`, `AISF_*`, `repository_dispatch`, token prefixes, `TOKEN`/`SECRET`/`API_KEY`/`PASSWORD` names |
| Searched (this repository) | `codefresh/`, `.octopus/`, `octopus/`, `apps/`, `scripts/`, `docs/`, `design/` for anything that authenticates to the app repository |
| Read-only API reads | Actions settings, workflow list and run count of the app repository, repository-level Actions secret and variable names, and the organization secret names visible to the repository (names only; values are not returned by the API) |
| Not readable | organization Actions secret list (needs an org admin), Codefresh context contents, Octopus sensitive variable values, the org's fine-grained token list. These are the owner-verify rows below; nothing is asserted about them |

## Inventory

Classes: **App** = GitHub App installation token, **PAT** = personal access token, **GITHUB_TOKEN** = the per-run
workflow token, **User** = a user session token from `gh` login (OAuth or PAT, not knowable from the file), **Other** =
a non-GitHub credential. "Where" is `file:line` in the app repository unless the path starts with `env:` (this
repository).

### PAT-class (fix children)

| ID | Credential | Class | Where used | Purpose | Replacement | Permissions needed | Owner-only step | Child |
|---|---|---|---|---|---|---|---|---|
| A1 | Retired operator PAT variable (first name of the history section of the runbook), read first by the token order `<that variable>`, `GH_TOKEN`, `GITHUB_TOKEN`, `gh auth token` | PAT | `.claude/factory-loop.json:28`; `.claude/skills/feature-loop/board.ps1:26,85,99`; `.claude/skills/feature-loop-dispatch/Check-StalledLanes.ps1:79,89`; `.claude/skills/feature-loop/SKILL.md:36,87`; `.claude/skills/feature-loop-dispatch/SKILL.md:82` | Feature-loop helper reads (issues, sub-issues, PRs, commit statuses) and sends the board `repository_dispatch` to this repository | App `aisf-board` through `scripts/github/GitHubAppAuth.ps1`, the order this repository's helper uses | Reads: Issues, Pull requests, Metadata read (held). `status` and `wait`: Commit statuses read (**missing**, add). `move`: Contents write on this repository (**missing**, decision pending, `gh` fallback stays for the dispatch) | Add Commit statuses read to the App; revoke the token behind the variable after one App-only run | [#68](https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/issues/68) |
| A2 | Retired board-workflow secret (the first entry of the history section), named as the workflow secret | PAT (stale reference) | `.claude/factory-loop.json:59` (`boardMoves.dispatch.workflowSecret`); `.claude/skills/feature-loop/reference.md:10` | Documentation of how cards move; the secret is not in the app repository, it lived in this one and is retired | App `aisf-board` (secrets `BOARD_APP_ID`, `BOARD_APP_PRIVATE_KEY` in this repository) | none in the app repository | none beyond A1 (revoke) | [#69](https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/issues/69) |
| A3 | `COPILOT_PAT` (repository secret name) | PAT (by name; value not readable) | `.github/workflows/build.yml:246` (step "Mark PR as ready for review", `gh pr ready`) | Flip a PR to ready when a branch build passes | Delete the step, or use `GITHUB_TOKEN` (the job already grants `pull-requests: write`, `build.yml:102-105`). If a workflow-triggering identity were needed: App `aisf-conformance` | `GITHUB_TOKEN`: none new. `aisf-conformance` (not recommended): Pull requests read and write, held on `20260923-001` | Check the org secrets for `COPILOT_PAT` and delete; revoke the token; decide whether Actions stays enabled (ADR-IR26) | [#70](https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/issues/70) |
| A4 | Personal `gh` token of the operator, handed to the legacy AI Factory executor as `gh_token` files, `GITHUB_TOKEN` and `.git-credentials` | User / PAT (`inject-credentials.ps1:35` accepts personal, OAuth and server prefixes; the documentation shows a PAT-shaped placeholder) | `.bob/skills/ai-factory-executor/inject-credentials.ps1:28,35`; `container-manager.ps1:120,123`; `agent-entrypoint.ps1:526,543-546`; `dev-entrypoint.ps1:44-50`; `experiment-entrypoint.ps1:47-52`; `argo/implement-issue.ps1:77`; `argo/install.ps1:99-101`; `argo/scripts/dispatch.ps1:9,32`; `docker-compose.yml:24,53`; documentation in `DOCKER.md:75-99`, `DOCKER-AGENT-PLAN.md`, `EXAMPLE.md`, `README.md`, `EXECUTION-REPORT-6970.md` | The executor clones the repository, pushes a branch and opens a PR for issues labelled "AI Factory" | Retire the executor; otherwise a per-run one-hour installation token, `aisf-conformance` (needs Issues read, not held) or a dedicated App | Contents read/write, Pull requests read/write, Metadata read (held by `aisf-conformance`); Issues read (**missing**) | Widen or create the App; revoke any personal token ever put in a container or the cluster Secrets `ai-factory-gh` and `agent-secret-*` | [#71](https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/issues/71) |

### Session tokens and fallbacks (no child of their own)

| ID | Credential | Class | Where used | Purpose | Note |
|---|---|---|---|---|---|
| B1 | `gh auth token` fallback | User | `.claude/skills/feature-loop/board.ps1:26,93`; `Check-StalledLanes.ps1:85` | Last resort of the token order | This repository keeps the same fallback on purpose (the App cannot dispatch yet). Handled in #68 |
| B2 | `GH_TOKEN`, `GITHUB_TOKEN` as caller-supplied environment variables | Unknown (whatever the session provides) | `.claude/factory-loop.json:28`; `board.ps1:85`; `Check-StalledLanes.ps1:79` | Second and third in the token order; cloud sessions provide `GH_TOKEN` | Owner-verify what the sessions inject. Handled in #68 |

### Already the right kind (no action)

| ID | Credential | Class | Where used | Purpose | Replacement |
|---|---|---|---|---|---|
| C1 | `secrets.GITHUB_TOKEN` (also as `GH_TOKEN`, `NUGET_AUTH_TOKEN`, `github-token:`) | GITHUB_TOKEN | `.github/workflows/build.yml:136,219,673,686,732,872`; `.github/workflows/deploy.yml:46,89,153,182,377`; scoped by `permissions:` blocks (`build.yml:102-105,269-271,528-535,720-722`; `deploy.yml:38,79-80`) | Build, GitHub Packages and image push, release-candidate prerelease, artifact download, TDD commit status | none. The workflows are the legacy CI (see [Actions state](#actions-state-in-the-app-repository)) |
| D1 | Codefresh Git integration `github-aisf-sample-apps` | App (the vendor "Codefresh Github App"; ADR-IR35) | `env:codefresh/platform/integrations.yaml:62-65`; the clone steps `env:codefresh/apps/workorders/pipelines/ci.yml:61,74`, `preview.yml:47,58`, `release.yml:95,113` | Clones, triggers and the `codefresh/ci` and `codefresh/release` statuses | none. Owner-verify that the integration is the App type and not the token type (the runbook says `register.ps1 --full` reports it) |
| D2 | App `aisf-octopus-status-reporter` (`GitHub.StatusAppId`, `GitHub.StatusAppInstallationId`, sensitive `GitHub.StatusAppPrivateKey`) | App, statuses write, `20260923-001` only | `env:.octopus/apps/workorders/workorders/deployment_process.ocl:979-1078`; `variables.ocl:64-76` | Octopus posts `platform/tdd`, `platform/uat`, `platform/prod` | none. Off until `GitHub.StatusEnabled` is true (owner) |
| D3 | App `aisf-conformance` | App, Contents rw, Pull requests rw, Commit statuses read, Metadata read; installed on the app repository | `env:codefresh/platform/github-app.json`; `env:codefresh/platform/scripts/conformance-github.ps1` | The end-to-end pass opens and merges a PR on the app repository | none |
| D4 | App `aisf-board` | App, Projects rw (org), Issues, Pull requests, Metadata read; installed on the app repository | `env:.claude/factory-loop.json` (`githubApp`); `env:scripts/github/GitHubAppAuth.ps1` | Board reads; the board workflow runs in this repository | none |
| D5 | Octopus GitHub App connection `GitHubAppConnections-41` | App | `env:octopus/terraform/variables.tf:87-94` | Config-as-code Git access to **this** repository, not the app repository | none |
| D6 | Unauthenticated GitHub API calls | none | `env:codefresh/apps/workorders/scripts/ci-tree.ps1:31-33,109-112` | The release reads statuses and PRs of the public app repository without a token (fails closed to full gates) | none |
| D7 | App `aisf-pin-writer` (#58) | App, Contents rw, Metadata read; to be installed on this repository only (owner-created; not the app repository); tokens minted per run and narrowed to this repository | `env:octopus/step-templates/pin-writer.ps1`; `env:octopus/terraform/step-templates.tf` | The pin writer pushes pin commits to `main` of this repository as `octopus-argocd-pin-bot`; no reach into `20260923-001` | none |

### Related, outside the app repository (owner-verify, not a #66 child)

| ID | Credential | Class | Where | Note |
|---|---|---|---|---|
| E1 | Octopus stored Git credential `GitHub clearmeasure-aisf-sample-apps` | PAT (org fine-grained; design: restricted to this repository) | `env:octopus/terraform/variables.tf:81-84`; `env:octopus/step-templates/pin-writer.ps1` (the pin writer no longer reads any token variable: it mints a per-run GitHub App token, D7); `env:design/platform-design.md` (credentials table) | Pin commits of the Argo CD step go to **this** repository. Owner-verify that its repository access does not include `20260923-001`. Retired by issue #58: stage 1 (the pin writer authenticates as the App `aisf-pin-writer`, D7) is delivered; stage 2 switches the processes to the writer and deletes the credential. The Octopus documentation, release notes 2026.2 and 2026.3 and provider 1.20.0 name no GitHub App connection for the Argo CD step (recorded finding, not an owner-verified negative) |
| E2 | Codefresh context `github-aisf-sample-apps-token`; Octopus variable set `GitHub AISF Sample Apps` | PAT copies, used by nothing | `env:docs/runbooks/credential-rotation.md` (schedule table, sections 2 and 12) | Owner deletes them (already documented) |

### Non-GitHub credentials seen (out of scope, names only)

`OCTO_API_KEY` and `OCTOPUS_URL` (Octopus, `build.yml:774-775`, `deploy.yml:116-256`), `AZURE_CREDENTIALS`
(`build.yml:632`, `deploy.yml:265`), `AI_OPENAI_APIKEY` (`build.yml:139,297,358,494,965,1035`, `deploy.yml:350`),
`GITLEAKS_LICENSE` (`build.yml:873`), `NVD_API_KEY` (`.claude/skills/owasp-dependency-scan/scripts/scan.ps1:27,50,188`,
`references/ci.md:29`), local database passwords (`BuildFunctions.ps1:264-334,480-484`, `README.md:97`), the AI factory's
model keys (`.bob/skills/ai-factory-executor/*-entrypoint.ps1`), the Octopus variable `AzureAccount.Password`
(`.octopus/variables.ocl:81`, a `#{...}` reference, no value), and the organization secret `ANTHROPIC_API_KEY` visible to
the repository.

### Actions state in the app repository

Read-only API reads on the audit date: GitHub Actions is **enabled** on the repository (`allowed_actions: all`), the
workflows `Build`, `Deploy` and `Render diagrams (check)` are `active`, the total number of workflow runs is **0**, and
the repository has **0 Actions secrets and 0 variables**; the only organization secret visible to it is a model API key.
So the workflows have never run and `COPILOT_PAT` is not a repository secret. This contradicts the design rule that
GitHub Actions stays disabled in app repositories (ADR-IR26; `.claude/factory-loop.json`, `boardMoves.automatic.scope`),
and is why A3 can be closed by deleting a step: it is a decision for the owner, recorded in #70.

## Summary counts

| Class | Count | Rows |
|---|---|---|
| PAT or PAT-class (child each) | 4 | A1, A2, A3, A4 |
| User session token (covered by a PAT child) | 2 | B1, B2 |
| `GITHUB_TOKEN` | 1 | C1 |
| GitHub App | 6 | D1 to D5, D7 |
| No credential (anonymous) | 1 | D6 |
| Related PAT outside the app repository (owner-verify) | 2 | E1, E2 |
| Non-GitHub credentials (not audited further) | 10 names or groups | list above |

Literal secrets committed in the app repository: **0**.

## Guard gap

This repository fails its offline suite when a retired PAT name reappears: `RetiredCredentialGuard` (CAP-KIT-007,
`tests/Platform.Conformance.Offline/Kit/Boundaries/`) scans every tracked text file, Markdown included, against a
shrinking allow-list.

The app repository has a suitable place but no guard:

- It has an offline NUnit suite, `src/UnitTests` (192 tracked files, no database), and `src/UnitTests/BuildGates/`
  already holds tests that read repository files (`EnvironmentsDocPresenceTests`, `CrapMetricsArtifactWorkflowTests`,
  `DeployWorkflowContainerAppUrlTests`, each resolving the file with a `FindRepoFile` helper). It runs in
  `PrivateBuild.ps1` and in `codefresh/ci`.
- It has `.gitleaks.toml` (default rules) and a Gitleaks step in `build.yml:869-873`. These catch token values, not names
  of retired PAT variables, and the workflow has never run.
- It has no test that fails on a PAT name.

Recommendation: a `RetiredCredentialGuardTests` fixture in `src/UnitTests/BuildGates/` that lists the retired names (A1,
A2, A3), fails on any tracked file naming one outside a shrinking allow-list, fails a stale allow-list entry, and covers
each name in a workflow, script, JSON and Markdown file with temporary trees. It is a change in the app repository, so it
is the child [#72](https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/issues/72); it is
not opened in this loop and lands after, or together with, the fixes of #68 to #71 (or starts with an allow-list that
lists their files).

## Owner-only steps

Documented here, never executed by a loop, a script or this audit:

1. In the organization settings, GitHub Apps: add **Commit statuses: Read** to `aisf-board` (read-only, needed by #68).
   Decide separately whether to add **Contents: Write** (needed for `repository_dispatch`; the App lacks it on purpose,
   see [tool-boundaries.md](../tool-boundaries.md), "Board automation"). Create a private key for local and cloud use.
2. Decide the identity of the AI Factory executor (#71): retire it, widen `aisf-conformance` with Issues read, or create
   a dedicated App.
3. Organization settings, Actions: look for the organization secret `COPILOT_PAT` and delete it if present; look at the
   organization's personal access token list for tokens whose repository access includes `20260923-001`, and revoke
   them, after one verified App-only run of each replacement.
4. Repository settings of `20260923-001`, Actions: decide between disabling Actions (ADR-IR26; retires the three
   workflows) and keeping them under the same no-PAT rule.
5. Codefresh, Account settings, Integrations, Git: confirm `github-aisf-sample-apps` is the GitHub App integration
   ([credential-rotation.md](../runbooks/credential-rotation.md), section 2a).
6. Octopus: confirm the stored Git credential's repository access excludes `20260923-001` (E1) and that
   `GitHub.StatusAppPrivateKey` holds the statuses-only App key, not a token.
7. Remove the personal token variable from operator profiles and cloud-session secrets once #68 is verified.

## Owner-verify (could not be read)

| What | Why unreadable | How the owner checks |
|---|---|---|
| Organization Actions secrets (whether `COPILOT_PAT` exists) | The API needs an org admin | Organization settings, Secrets and variables, Actions |
| Codefresh contexts and git integration type | Contents are not readable through the API without the key | Codefresh, Integrations; `pwsh codefresh/register.ps1 --full` reports the type |
| Octopus sensitive variables (`GitHub.StatusAppPrivateKey`, the stored Git credential) | Sensitive values are never returned | Octopus, Variables and Library, Git credentials |
| Fine-grained tokens of the organization and their repository access | Not listable without org admin rights | Organization settings, Personal access tokens |
| What the cloud and operator sessions inject as `GH_TOKEN` or `GITHUB_TOKEN` | Outside both repositories | The session's environment configuration |

## How to verify each replacement

Never a green build alone:

- A1/A2: run `board.ps1 status <pr>` in the app repository with only App inputs set; the helper's log line names the
  credential kind (App installation token), and `gh api` on the same PR returns the statuses it printed.
- A3: `gh api repos/clearmeasure-aisf-sample-apps/20260923-001/actions/secrets` lists no PAT-named secret, and
  `grep -rn COPILOT_PAT` on the app repository is empty.
- A4: no `gh auth token` call under `.bob/`, or the directory is gone; a run's log line names an installation token with
  a one-hour lifetime.
- Guard (#72): the new test fails on a planted retired name in a temporary tree and passes on the real tree.

## Children

All are sub-issues of #66. Each fix is a pull request in `clearmeasure-aisf-sample-apps/20260923-001`, not opened in
this loop.

| Child | Title |
|---|---|
| [#68](https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/issues/68) | Replace the operator PAT variable in `board.ps1` and `Check-StalledLanes.ps1` with the `aisf-board` App token |
| [#69](https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/issues/69) | Remove the stale board-workflow PAT reference from `factory-loop.json` and the feature-loop reference |
| [#70](https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/issues/70) | Remove `COPILOT_PAT` from `build.yml` (use `GITHUB_TOKEN` or delete the step) |
| [#71](https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/issues/71) | Replace the personal `gh` token in the `.bob` AI Factory executor with an App installation token, or retire it |
| [#72](https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/issues/72) | Add a `RetiredCredentialGuard` equivalent to the offline unit tests |
