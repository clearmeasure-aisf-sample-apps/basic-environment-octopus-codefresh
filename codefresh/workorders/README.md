# Codefresh pipelines for Work Orders

Codefresh **builds**: it runs the Linux gates, mints the version, builds, signs and attests the images, pushes the Octopus packages and creates the Octopus release. It never deploys, approves, waits for a deployment or touches a cluster. Octopus releases and promotes. Argo CD reconciles. The rules come from the platform design, `design/platform-design.md`: §7.7 (contracts), ADR-C6, C7, D8, D11 and D17.

`codefresh/ci` is the single required status on the application repo from day one (ADR-C6 as amended by ADR-IR26). GitHub Actions stays disabled there: the repo is a fork, and GitHub keeps workflows of a fork off until someone enables them. The legacy origin keeps its own `build-result`.

Contributors open pull requests against `clearmeasure-aisf-sample-apps/20260923-001`, base `master`. A fork's pull requests default to the upstream repository, so select the base repository explicitly (web UI), or run `gh repo set-default clearmeasure-aisf-sample-apps/20260923-001` before `gh pr create`. Never open a pull request against `ClearMeasureLabs/bootcamp-palermo-workorders`.

## Two repositories

| Repository | Role for these pipelines |
|---|---|
| `clearmeasure-aisf-sample-apps/20260923-001` (the application repo, default branch `master`) | What gets built and tested. Its pushes and pull requests trigger `workorders/ci`, `workorders/release` and `workorders/preview`. It holds no platform file. |
| `clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh` (this repo, default branch `main`) | Everything that defines the build: specs, pipeline YAML, scripts, the image Dockerfiles and the version prefix |

The application repo is a copy of `ClearMeasureLabs/bootcamp-palermo-workorders` at `24da122`. That origin keeps the live legacy pipeline (GitHub Actions and Octopus); nothing here touches it.

Every spec loads its YAML from `main` of this repo. Each pipeline clones:
- `main_clone`: the application repo at the triggering commit, with full history, into `${CF_VOLUME_PATH}/app`. It is the default working directory of every later step.
- `platform_clone`: this repo at `main` (depth 1) into `${CF_VOLUME_PATH}/platform`. Scripts run as `${{platform_clone}}/codefresh/workorders/scripts/<script>.sh`, against the application checkout.

`workorders/ci-image` builds only from this repo; its `main_clone` is this repo.

**Security consequence.** Branch authors in the application repo can no longer change pipeline YAML, scripts, Dockerfiles or `version.env`; those change only through reviewed pull requests here (CODEOWNERS, the `main` ruleset, `codefresh/env-checks`). Branch code still runs in the gates (`build.ps1`, the tests, `.github/scripts/detect-code-changes.sh`), so:
- `workorders/ci` keeps only the `workorders-ci` context and the runtime without cloud identity.
- Release credentials stay in `workorders/release`, which triggers only on `master`.
- The preview label and same-repository guard stay. They are now reliable, because pull request authors cannot edit them.

The only guard that became moot is the `specTemplate` pin `revision: master` on the application repo. Every spec now loads from `main` of this repo.

## Layout

| Path (this repo) | Purpose |
|---|---|
| `codefresh/workorders/pipelines/ci.yml` | `workorders/ci`: the gates for branch pushes |
| `codefresh/workorders/pipelines/release.yml` | `workorders/release`: the same gates, then packages, images, supply chain and the Octopus handoff |
| `codefresh/workorders/pipelines/preview.yml` | `workorders/preview` (phase 6): preview images for labelled pull requests |
| `codefresh/workorders/pipelines/ci-image.yml` | `workorders/ci-image`: the toolchain image `platform/ci-dotnet` |
| `codefresh/workorders/specs/*.yml` | Pipeline specs: triggers, runtime, contexts, YAML location, concurrency |
| `codefresh/workorders/scripts/version.sh` | `MAJOR.MINOR.<first-parent height>` on master, `…-ci.<sha7>` elsewhere |
| `codefresh/workorders/scripts/changed-paths.sh` | Changed paths for docs-only detection (master: `HEAD^1..HEAD`; branches: merge base with `origin/master`) |
| `codefresh/workorders/scripts/gate.sh` | `build-result` semantics over the step results |
| `codefresh/workorders/scripts/buildinfo.sh` | Octopus build information and the release notes file |
| `codefresh/workorders/scripts/stage-built.sh` | Lean Docker contexts for the three images |
| `codefresh/workorders/scripts/supply-chain.sh` | SBOM and provenance attestations, ACR tag lock |
| `codefresh/workorders/version.env` | `MAJOR=2`, `MINOR=5` |
| `codefresh/images/ci-dotnet/Dockerfile` | SDK 10, pwsh, Playwright 1.54 Chromium, go-sqlcmd, az CLI, Gitleaks, Syft, cosign, crane |
| `containers/workorders/worker/Dockerfile`, `containers/workorders/db-migrator/Dockerfile` | Worker and DbUp migrator images |
| `codefresh/pipelines/env-checks.yml`, `codefresh/specs/platform-env-checks.yml` | `platform-env/env-checks`, the checks of this repo |

The UI image keeps the application repo's root `Dockerfile`. `stage-built.sh` copies it into the staged context and appends the OCI labels; the file in the application repo never changes.

## Pipelines

| Pipeline | Trigger | Runtime | Contexts | Registry integration | Status |
|---|---|---|---|---|---|
| `workorders/ci` | Application repo `push.heads`, branch regex `/^(?!master$).+/` | `<cf-runtime-ci>` | `workorders-ci` | none (pulls with `acr-platform-pull`) | `codefresh/ci` (required) |
| `workorders/release` | Application repo `push.heads`, `/^master$/` | `<cf-runtime-release>` | `workorders-ci`, `workorders-release`, `workorders-octopus` | `acr-workorders-release` | `codefresh/release` |
| `workorders/preview` (phase 6) | Application repo `pullrequest.opened`, `.synchronize`, `.labeled` [VERIFY]; forks off | `<cf-runtime-ci>` | none | `acr-workorders-preview` | `codefresh/preview` |
| `workorders/ci-image` | Cron `0 6 * * 1`; a push to `main` of this repo that touches `codefresh/images/**` | `<cf-runtime-release>` | none | `acr-platform-ci` | — |
| `platform-env/env-checks` | `push.heads` on every branch of this repo | `<cf-runtime-ci>` | none | none | `codefresh/env-checks` (required on `main`) |

All triggers and clones use the stored Git integration `github-aisf-sample-apps`. It covers both repositories in the org and is used only to read, trigger and post commit statuses. Whether its token may create webhooks and post statuses on the application repo is [VERIFY].

Concurrency and termination:
- `workorders/ci` and `workorders/preview`: a new build cancels older builds of the same branch (`terminationPolicy: branch/onCreate`).
- `workorders/release` and `workorders/ci-image`: `concurrency: 1`. Builds queue and are never cancelled.
- `platform-env/env-checks`: no termination policy, so every push to `main` finishes its bot-path audit.

Contexts (values never in Git):
- `workorders-ci` (secret): `CI_SQL_SA_PASSWORD` (a throwaway password for the service container), `AI_OPENAI_APIKEY`, `AI_OPENAI_URL`, `AI_OPENAI_MODEL` (a CI-only, low-budget key).
- `workorders-release` (secret): `OCTOPUS_PROJECT=workorders`, `ACR_REGISTRY=<acr-name>.azurecr.io`, `ACR_TOKEN_NAME=cf-workorders-release` and `ACR_TOKEN_PASSWORD` (the token of `acr-workorders-release`, for `supply_chain`; ADR-IR10). Rotate the password in the integration and the context together. Its former Octopus keys (`OCTOPUS_URL`, `OCTOPUS_SPACE`, `OCTOPUS_SERVICE_ACCOUNT_ID`) are dropped: `workorders-octopus` supplies the Octopus values, and two attached contexts must not define the same key.
- `workorders-octopus` (secret, created by the orchestrator, attached to `workorders/release` only): `OCTOPUS_URL`, `OCTOPUS_SPACE_ID`, `OCTOPUS_API_KEY`.
- The stored contexts `azure-runtime-provisioner` and `github-aisf-sample-apps-token` are attached to no pipeline.

Octopus identity (USER DIRECTIVE, binding): the only Octopus credential is the user's **Space Manager API key**. There is no Octopus OIDC service account for Codefresh, so `obtain-oidc-id-token` and `octopusdeploy-login` are gone and the marketplace steps take `OCTOPUS_API_KEY` directly. Controls around the key:
- Only `workorders/release` has `workorders-octopus`. Its YAML and scripts come from reviewed `main` of this repo, and it triggers only on `master` of the application repo.
- The key is used by `wake_nonprod` (one runbook-run request, see "Sleep and wake") and by `octopus_packages`, `octopus_build_info` and `octopus_release`, which run after every gate, the supply chain and the master-only `package` guard. `octopus_preflight` fails closed first if a key is missing or still a placeholder, and never prints it.
- `workorders/ci`, `workorders/preview`, `workorders/ci-image` and `platform-env/env-checks` get no Octopus credential.
- Residual risk: the key carries Space Manager rights, not a create-release-only role, and it does not expire on its own. Any step of the release build can read it, and so can application code from `master` (`build.ps1`, the tests) running in the same build. The octopus-architect records the risks and the rotation in the ADR.

Registry integrations hold repository-scoped ACR tokens: `acr-workorders-release` (`cf-workorders-release`, push `workorders/*`), `acr-workorders-preview` (`cf-workorders-preview`, push `workorders-previews/*`), `acr-platform-ci` (`cf-platform-ci`, push `platform/*`, used only by `workorders/ci-image`) and `acr-platform-pull` (`cf-platform-pull`, pull-only `platform/*`). All four share the `<acr-name>.azurecr.io` domain, so freestyle steps that pull `platform/ci-dotnet` set `registry_context: acr-platform-pull` (ADR-IR19). The step image is pinned by digest (`<ci-image-version>@sha256:<ci-image-digest>`).

Runtimes (ADR-D17, recommended to the user): `<cf-runtime-ci>` runs branch code and has no cloud identity; `<cf-runtime-release>` runs on a separate, tainted node pool. A branch build can therefore never poison the layer cache of a release build.

## Registering the specs

Prerequisites (bootstrap, `docs/bootstrap.md`): both runtimes, the four registry integrations, both contexts and the projects `workorders` and `platform-env` exist.

```sh
# From the root of this repo
codefresh create pipeline -f codefresh/workorders/specs/workorders-ci-image.yml
codefresh create pipeline -f codefresh/workorders/specs/workorders-ci.yml
codefresh create pipeline -f codefresh/workorders/specs/workorders-release.yml
codefresh create pipeline -f codefresh/workorders/specs/workorders-preview.yml   # phase 6 only
codefresh create pipeline -f codefresh/specs/platform-env-checks.yml

# Later changes to a spec
codefresh replace pipeline -f codefresh/workorders/specs/workorders-release.yml
```

Before registering, replace the placeholders in the specs and pipelines: `<cf-runtime-ci>`, `<cf-runtime-release>`, `<acr-name>`, `<ci-image-version>`, every `<…-digest>`, `<…-version>` and `<…-sha256>`, and `<platform-bots-author-regex>`.

Order:
1. Register `workorders/ci-image` and run it once. After `smoke` passes, put the new tag and its digest into `StepImage.CiDotnet` (both Octopus projects) and into the `platform/ci-dotnet` references in `codefresh/workorders/pipelines/{ci,release,preview}.yml`: one pull request in this repo. `consistency.sh` check C14 fails when the references differ.
2. Register `workorders/ci` and `workorders/release`. `workorders/release` needs the contexts `workorders-ci`, `workorders-release` and `workorders-octopus` before its first run.
3. Register `platform-env/env-checks`. After its first report, require `codefresh/env-checks` on `main`.

The spec field names follow the CLI spec format (<https://codefresh-io.github.io/cli/pipelines/spec/>) and the JSON tags in `codefresh-io/terraform-provider-codefresh` (`codefresh/cfclient/pipeline.go`). The termination-policy entry `{type: branch, event: onCreate}` is the provider's mapping of `on_create_branch`. `specTemplate` carries its own `repo`, `path`, `revision` and `context`, independent of the triggers, so the YAML can come from this repo while the triggers watch the application repo; `revision: main` pins it.

## Step flow

`workorders/ci` and the first half of `workorders/release`:

```text
main_clone (application repo, full depth) ─┐
platform_clone (this repo, main) ──────────┴─ prepare: VERSION, BUILD_BUILDNUMBER, CODE_CHANGED, IS_RELEASE; one worktree per gate
       ├─ build_sql      app checkout    Build + mssql service, then CRAP
       ├─ build_sqlite   wt/sqlite       Build -UseSqlite
       ├─ code_analysis  wt/analysis     restore, format style, format analyzers, build -warnaserror
       ├─ qodana         wt/qodana       jetbrains/qodana-cdnet:2026.2, baseline, threshold 0
       ├─ security_scan  wt/security     Gitleaks, NuGet vulnerable and deprecated (advisory)
       └─ acceptance     wt/acceptance   Invoke-AcceptanceTests + mssql service
            └─ gate: gate.sh (finished on all six)
```

`workorders/release` also starts `wake_nonprod` right after `prepare`, in parallel with the gates, when the build will release (master, `CODE_CHANGED=true`). See "Sleep and wake".

Every gate is skipped when `CODE_CHANGED=false`; `gate` then passes, as `build-result` does. Only `release.yml` continues:

```text
gate ─ package (master, CODE_CHANGED=true) ─ stage_images ─┬─ ui_image ───────┐
                                                            ├─ worker_image ───┼─ supply_chain ─ octopus_preflight
                                                            └─ migrator_image ─┘       ─ octopus_packages ─ octopus_build_info ─ octopus_release
```

Image builds: `stage-built.sh` stages one lean context per image under `${CF_VOLUME_PATH}/image-contexts/` from the application checkout's build outputs. The UI context uses `built/` from the UI nupkg and the application repo's root `Dockerfile`. The worker context uses `dotnet publish` output and `containers/workorders/worker/Dockerfile` from this repo. The migrator context uses `dotnet publish` output, `src/Database/scripts` and `containers/workorders/db-migrator/Dockerfile`. The build steps use these directories as build context, not the whole checkout: the application repo has no `.dockerignore`, and its `bin/`, `obj/` and `video/` would otherwise go to the Docker daemon.

Shared volume: the NuGet cache lives at `${CF_VOLUME_PATH}/.nuget/packages`. Each step links `/tmp/nuget-packages` to it, because `build.ps1` pins `NUGET_PACKAGES=/tmp/nuget-packages` outside GitHub Actions (F11). Reports go to `${CF_VOLUME_PATH}/reports/<step>/`, which `prepare` clears at the start of each build.

Handoff (contract §7.7, in order): `octopus_preflight` (context check, no network), then, each authenticated with `OCTOPUS_API_KEY` against `OCTOPUS_URL` and `OCTOPUS_SPACE_ID`: `octopusdeploy-push-package:1.0.1` (`ChurchBulletin.Database`, `ChurchBulletin.AcceptanceTests`; `OVERWRITE_MODE: ignore`), `octopusdeploy-push-build-information:1.0.1` (five package IDs, commits `HEAD^1..HEAD`, `VcsRoot` = the application repo, `OVERWRITE_MODE: overwrite`), `octopusdeploy-create-release:1.0.1` (`PROJECT: workorders`, `CHANNEL: Default`, release number and package version `VERSION`, `GIT_REF: refs/heads/main`, no `GIT_COMMIT`, `IGNORE_EXISTING: true`, `RELEASE_NOTES_FILE`).

Re-runs mint the same `VERSION`. Octopus accepts that, but the locked tags reject a second image push. After a failure past `supply_chain`, restart the build from the failed step [VERIFY] instead of re-running it.

The YAML and `platform_clone` both read `main` of this repo at build start. A push to `main` between the two reads can mix two commits in one build [VERIFY how Codefresh resolves the template revision]. The provenance records the commit that supplied the scripts.

## Parity map: GitHub Actions to Codefresh

| Legacy `build.yml` job | Codefresh step | Difference |
|---|---|---|
| `changes` | `prepare` | Same classifier (`detect-code-changes.sh --from-list -`), same fail-open rule. Master diffs `HEAD^1..HEAD` instead of `event.before..HEAD`. |
| Set Version (`2.4.<run>`) | `prepare` (`version.sh`) | `2.5.<first-parent height>`; `-ci.<sha7>` off master (ADR-C7) |
| `build-linux` (CI build, CRAP) | `build_sql` | A step-scoped SQL Server service container with `SQL_EXTERNAL=true` instead of Docker on the runner |
| `build-sqlite` | `build_sqlite` | — |
| `integration-build-arm` | — | Not run for the application repo (ADR-IR26); ARM builds are Enterprise-only in Codefresh (E22) |
| `code-analysis` | `code_analysis` | Same four commands |
| `qodana` | `qodana` | Docker image pinned to the baseline release instead of a floating tag; no SARIF upload to code scanning |
| `build-windows` | — | Not run for the application repo (ADR-IR26); Windows builds are incubating in Codefresh (E23) |
| `security-scan` (disabled) | `security_scan` | Runs, but advisory; the Gitleaks binary replaces the action |
| `acceptance-tests` | `acceptance` | Chromium baked into the image; 30-minute timeout as before |
| `acceptance-tests-arm` | — | Not run for the application repo (ADR-IR26) |
| `build-result` | `gate` | `gate.sh`; `security_scan` advisory |
| `docker-build-image-for-churchbulletin-ui` | `stage_images`, `ui_image` (release only) | Same `built/` extraction (F6). Tags `<VERSION>` and `sha-<sha7>`, locked, signed; no branch images |
| — | `worker_image`, `migrator_image`, `supply_chain` | New |
| `publish-octopus` | `package`, `octopus_*` (release only) | The user's Space Manager API key from `workorders-octopus` (the legacy path uses its own `OCTO_API_KEY`); the new space; only the Database and AcceptanceTests packages |
| `publish-github-packages` | — | Legacy path only |
| Test Reporter and artifacts | `${CF_VOLUME_PATH}/reports/` | TRX files are kept on the volume; publishing them for the ADR-C6 comparison is not built yet |

`build.yml` and `deploy.yml` sit in `.github/workflows/` of both repositories; the live legacy pipeline runs from `ClearMeasureLabs/bootcamp-palermo-workorders`. In the application repo GitHub Actions stays disabled (ADR-IR26), so neither workflow runs there and `codefresh/ci` is the merge gate. The images ship for `linux/amd64` only, so the ARM and Windows jobs have no deployment counterpart. `deploy.yml` has no Codefresh counterpart: Octopus and Argo CD replace it.

## Sleep and wake

The AKS clusters sleep by default; Octopus runbooks stop them at night and after 2 hours without jobs (`env-sleep`), and start them on the first job (`env-wake`). ADR-IR33 ("Sleep by default, wake on first job") and `docs/runbooks/sleep-and-wake.md` hold the details; the Codefresh part is small:

| Pipeline | Wakes | How |
|---|---|---|
| `workorders/release` | Nonprod (tdd, uat) | `wake_nonprod`, right after `prepare`, in parallel with the gates, only on master when `CODE_CHANGED=true`. It asks Octopus to run runbook `env-wake` of project `workorders-infrastructure` in `infra-nonprod` and does not wait. |
| `workorders/preview` (phase 6) | Nothing yet | A commented `wake_nonprod` step marks the need; the phase-6 decision picks its credential (see below) |
| `workorders/ci`, `workorders/ci-image`, `platform-env/env-checks` | Nothing | They never touch the application clusters |

Why early and non-blocking:
- AKS takes 5–10 minutes to start. The gates take longer, so the cluster is usually Running when the release reaches Octopus.
- `wake_nonprod` never fails the build: `fail_fast: false`, no `strict_fail_fast`, and every failure path exits 0 with a warning. A missed wake costs only time, because the first step of every Octopus deployment, `wake-environment`, deploys `platform-wake`, which runs `env-wake` and waits for it (ADR-IR33). That step is the guarantee; `wake_nonprod` is only a head start.
- `env-wake` is idempotent, so a wake request against a Running cluster returns within seconds.
- Codefresh never holds Azure rights and never runs `az aks start`. It only asks Octopus to run the runbook, with the Space Manager key of `workorders-octopus`. The key goes into a private header file, never onto a command line or into the log.

The request: `wake_nonprod` looks up the IDs of project `workorders-infrastructure` and environment `infra-nonprod`, then posts one run to `POST {OCTOPUS_URL}/api/spaces/{OCTOPUS_SPACE_ID}/projects/{projectId}/refs%2Fheads%2Fmain/runbooks/env-wake/run/v1`. The route carries a Git ref because the runbooks are config-as-code (<https://octopus.com/docs/runbooks/config-as-code-runbooks>). The body follows Octopus's `RunConfigAsCodeRunbook.ps1` example, which is marked early access [VERIFY].

Previews in phase 6 need the nonprod cluster awake, because Argo CD deploys them there. The wake cannot simply attach `workorders-octopus` to `workorders/preview`: a context reaches every step, and the preview's `build` step runs pull-request code. Options for the phase-6 decision: a separate wake-only pipeline with the context and no application checkout, triggered by the same pull request events, or an Octopus key that can only run `env-wake`.

## Boundary rules

Enforced by review and by `scripts/checks/tool-boundaries.sh` in this repo:
- No `deploy`, `approval`, `helm` or `launch-composition` steps. No `argocd`, `kubectl`, `helm install/upgrade` or `az aks` commands against any cluster.
- No Codefresh GitOps Runtime or Promotions objects.
- No `latest` tag anywhere. Release tags are `<VERSION>` and `sha-<sha7>`, locked in ACR.
- The only Octopus API key in any Codefresh pipeline is `OCTOPUS_API_KEY` from `workorders-octopus`, in `workorders/release` (user directive). The legacy `OCTO_API_KEY` stays with the legacy path until decommission.
- Codefresh reads repositories and posts statuses. It never commits or pushes, and it writes nothing to the application repo.
- Release credentials exist only in `workorders/release`. `workorders/ci` and `workorders/preview` get no release context and run on the runtime without cloud identity.
- `azure-runtime-provisioner` and `github-aisf-sample-apps-token` stay unattached.
- Octopus packages and release images are pushed only by `workorders/release`; nothing else creates Octopus releases.
- Codefresh never starts or stops a cluster. The only Octopus runbook run it requests is `env-wake` in `infra-nonprod`, from `wake_nonprod` in `release.yml` (sleep/wake contract).

## Running the scripts locally

From an application checkout (full history), with `ENV` pointing at a checkout of this repo:

```sh
S="$ENV/codefresh/workorders/scripts"
bash "$S/version.sh"                                   # unshallows or fails
bash "$S/changed-paths.sh" | bash .github/scripts/detect-code-changes.sh --from-list -
GATE_build_sql=success GATE_qodana=failure CODE_CHANGED=true \
  bash "$S/gate.sh" --advisory security_scan build_sql qodana
bash "$S/buildinfo.sh" --out /tmp/buildinfo.json --release-notes-out /tmp/notes.md
# After `. ./build.ps1; Build` and `Package-Everything` with the same BUILD_BUILDNUMBER:
bash "$S/stage-built.sh" --version "$BUILD_BUILDNUMBER"
```

## Decisions from the integration review

The contract gaps reported during implementation are decided in the design (§2.5):
1. **Release notes (ADR-IR23).** `release.yml` passes `RELEASE_NOTES_FILE`, because `octopusdeploy-create-release` 1.0.1 renders `RELEASE_NOTES` unescaped into YAML. `buildinfo.sh` writes the first line `app-commit: <sha>`; `consistency.sh` C16 checks both.
2. **Registry credentials for `supply_chain` (ADR-IR10).** `workorders-release` is a secret context with `ACR_TOKEN_NAME` and `ACR_TOKEN_PASSWORD`; the step writes a step-local Docker config from them.
3. **`PLATFORM_BOT_AUTHORS` (ADR-IR6).** `<platform-bots-author-regex>` is a §7.1 placeholder, set at bootstrap; the guard fails the step while it remains.
4. **Marketplace step images (ADR-IR18).** Accepted while releases reach TDD only; before the phase-3 exit the handoff moves to freestyle steps on the digest-pinned toolchain image with a checksum-pinned Octopus CLI.
5. **Account-wide registry integrations (ADR-IR19).** Pull-only `acr-platform-pull` for step images, digest-pinned step image, push integrations named only by reviewed YAML from `main`; the residual risk is recorded.
6. **UI base image.** The application repo's root `Dockerfile` uses `mcr.microsoft.com/dotnet/aspnet:10.0` without a digest. The SBOM records the resolved digest; WI-13 proposes the pin to the app team.
7. **Layout (ADR-D18).** Applied to §6, §7.5 and §7.7.

[VERIFY] before relying on them:
- `CF_OIDC_REQUEST_URL` and `CF_OIDC_REQUEST_TOKEN` inside freestyle steps (`supply-chain.sh` requests the `sigstore` audience itself).
- A git-clone step with `working_directory` clones exactly into that directory; later steps use `${{main_clone}}` and `${{platform_clone}}`, which name the checkout either way.
- The `steps.<name>.result` values (`gate.sh` treats anything but `success` as failure).
- The event name `pullrequest.labeled` and the format of `CF_PULL_REQUEST_LABELS`.
- Token permissions of `github-aisf-sample-apps` on the application repo: webhooks for the triggers and commit statuses for `codefresh/ci`, `codefresh/release` and `codefresh/preview`.
- Two parallel steps each with an `mssql` service container (separate compositions).
- Tag lock through the token's data plane (`az acr repository update`, metadata write).
- Cron timezone (UTC assumed); the hook's access to the volume; restart from a failed step.
- Runtime size: one build runs five .NET builds, Qodana and two SQL Server containers in parallel on `<cf-runtime-ci>`.
