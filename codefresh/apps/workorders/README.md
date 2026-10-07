# Codefresh pipelines of app #1 (`workorders`)

Codefresh **builds**. It runs the Linux gates, mints the version, builds, signs and attests the images, pushes the Octopus package and build information and creates the Octopus release. It never deploys, approves, waits for a deployment or touches a cluster (handshake M4). Octopus releases and promotes; Argo CD reconciles. Contracts: `design/platform-design.md` §7.0 "Codefresh" and "Registry and supply chain", §7.7, ADR-C6, ADR-C7, ADR-IR34 §11.7.1 item 1.

`codefresh/ci` is the single required status on the app repo. GitHub Actions stays disabled there (ADR-IR26). Pull requests go to `clearmeasure-aisf-sample-apps/20260923-001`, base `master`, never to the upstream `ClearMeasureLabs/bootcamp-palermo-workorders` (select the base repository explicitly, or run `gh repo set-default clearmeasure-aisf-sample-apps/20260923-001` first).

## Two repositories

| Repository | Role |
|---|---|
| `clearmeasure-aisf-sample-apps/20260923-001` (app repo, default branch `master`) | What gets built and tested. Its pushes trigger `workorders/ci` and `workorders/release`; labelled pull requests trigger `workorders/preview`. It holds no platform file. |
| `clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh` (this repo, `main`) | Everything that defines the build: specs, pipeline YAML, scripts, Dockerfiles, the version prefix |

Every spec loads its YAML from `main` of this repo (`specTemplate.revision: main`). Each pipeline clones:
- `main_clone`: the app repo at the triggering commit, full history, into `${CF_VOLUME_PATH}/app`, the default working directory;
- `platform_clone`: this repo at `main` (depth 1), for `${{CF_VOLUME_PATH}}/platform/codefresh/apps/workorders/scripts/*` and `containers/apps/workorders/*`.

Branch authors of the app repo cannot change the YAML, the scripts or the Dockerfiles; those change only through reviewed pull requests here (CODEOWNERS, the `main` ruleset, `codefresh/env-checks`). Branch code still runs in the gates (`build.ps1`, the tests), so `workorders/ci` carries only the optional `app-workorders-ci` context, and the release credentials exist only in `workorders/release`, which triggers only on `master`.

## Layout

| Path | Purpose |
|---|---|
| `pipelines/ci.yml` | `workorders/ci`: the gates for branch pushes |
| `pipelines/release.yml` | `workorders/release`: the gates, then packages, images, supply chain and the Octopus handoff |
| `pipelines/preview.yml` | `workorders/preview` (phase 6): preview images under `apps-previews/workorders/*` |
| `specs/{ci,release,preview}.yml` | Specs: triggers, contexts, runtime, YAML location, concurrency; registered by `codefresh/register.ps1` |
| `integrations.yaml` | The app-owned optional context `app-workorders-ci` (AI keys of the LLM tests), declared without values |
| `version.env` | `MAJOR=2`, `MINOR=5` |
| `scripts/prepare.ps1` | Step `prepare`: `VERSION`, `CODE_CHANGED`, the SQL password, `ARTIFACTS_DIR`, the gate worktrees |
| `scripts/version.ps1` | `MAJOR.MINOR.<first-parent height>` on `master` (`RELEASE_BRANCH`), `…-ci.<sha7>` elsewhere |
| `scripts/changed-paths.ps1` | Changed paths for docs-only detection (`master`: `HEAD^1..HEAD`; branches: merge base with `origin/master`) |
| `scripts/preview-guard.ps1` | Step `guard` of `workorders/preview`: the `preview` label and the same-repository head |
| `scripts/gate.ps1` | `build-result` semantics over the step results |
| `scripts/trx-summary.ps1` | Markdown summary of every TRX file (TRX is the only test-result format; no JUnit) |
| `scripts/security-scan.ps1` | Step `security_scan` (advisory): Gitleaks, the NuGet vulnerability and deprecation reports |
| `scripts/buildinfo.ps1` | Octopus build information and the release notes file |
| `scripts/release-notes.ps1` | Step `octopus_release`: appends the CI summary (commit, PR, builds, per-suite TRX counts of this build, image digests) to the release notes |
| `scripts/stage-built.ps1` | Lean Docker contexts for the three images |
| `scripts/build-facts.ps1` | Step `image_reuse`: `build-facts.json`, the record of the build, into `built/` of the UI image context (the app answers it at `/_build`) |
| `scripts/supply-chain.ps1` | SBOM and provenance attestations, keyless; ACR tag lock; the reuse check of a re-run |
| `scripts/supply-chain-step.ps1` | Steps `image_reuse`, `supply_chain`, `supply_chain_reuse`: the registry token as a step-local Docker config, then `supply-chain.ps1` |
| `scripts/wake-nonprod.ps1` | Step `wake_nonprod`: runbook `env-wake` in `infra-nonprod`, fire-and-forget; every path exits 0 |
| `scripts/octopus-preflight.ps1` | Step `octopus_preflight`: context `platform-octopus` complete, not a placeholder, https; the Octopus CLI answers; no network call |
| `containers/apps/workorders/worker/Dockerfile`, `containers/apps/workorders/db-migrator/{Dockerfile,migrate.sh}` | Worker and DbUp migrator images (the UI image keeps the app repo's root `Dockerfile`) |

## Pipelines

![Level 3: Codefresh projects, pipelines and their triggers](../../../design/diagrams/c4-3-codefresh-a.png)

*Level 3, Codefresh projects and pipelines (plan BASIC_1: one build at a time) and what starts each. App repos start `<app>/ci` on every branch but the release branch, `<app>/release` on it and `workorders/preview` on labelled same-repo pull requests; fork events are off. Each pipeline posts its `codefresh/*` status; `codefresh/ci` is the required check of master. The environment repo starts env-checks and ci-image-dotnet; crons start ci-image-dotnet weekly and conformance-arm, conformance-destructive and registry-retention once P1-13 enables them. conformance-arm pushes the sandbox commits and queues conformance; `codefresh/register.ps1` creates or replaces every project, pipeline, context and integration by name.*

| Pipeline | Trigger | Contexts | Registry integration | Status |
|---|---|---|---|---|
| `workorders/ci` | App repo `push.heads`, `/^(?!master$).+/`, forks off; a newer build cancels older ones of the branch | `app-workorders-ci` (optional) | none (step images through `acr-platform-pull`) | `codefresh/ci` (required) |
| `workorders/release` | App repo `push.heads`, `/^master$/`; concurrency 1 | `app-workorders-ci` (optional), `platform-registry`, `platform-octopus` | `acr-apps-release` | `codefresh/release` |
| `workorders/preview` (phase 6) | Labelled same-repository pull requests; forks off | none | `acr-apps-preview` | `codefresh/preview` |

Runtime: `aks-platform-build/codefresh` (`<cf-runtime>`, the account default) for all three. Step images: `acrplatformi3aldz.azurecr.io/platform/ci-dotnet:20260924.2333-f91765f@sha256:1fd09acf036affd0a1a7676ddb57117098f809a1286674343985c1dab4e72d67`, pulled with `registry_context: acr-platform-pull` (ADR-IR19), built by `platform-env/ci-image-dotnet`.

Contexts (values never in Git; `codefresh/platform/integrations.yaml`):
- `platform-octopus`: `OCTOPUS_URL`, `OCTOPUS_SPACE_ID`, `OCTOPUS_API_KEY` (the Space Manager key, ADR-IR32), for `wake_nonprod` and the handoff.
- `platform-registry`: `ACR_REGISTRY`, `ACR_TOKEN_NAME=cf-apps-release`, `ACR_TOKEN_PASSWORD`, for `supply_chain` (ADR-IR10).
- `app-workorders-ci` (optional): `AI_OPENAI_APIKEY`, `AI_OPENAI_URL`, `AI_OPENAI_MODEL`, mapped by the gates to the app's `AI_OpenAI_*` settings. Without it the `[LlmTest]` tests are skipped, not failed.

The SQL Server password of the gates is minted per build in `prepare` (`cf_export --mask`); no context holds it.

## Step flow

![Dynamic: the step graph of workorders/ci](../../../design/diagrams/dyn-ci-pipeline.png)

*Dynamic, the step graph of `workorders/ci`. Both clones feed `prepare` (`VERSION`, `CODE_CHANGED`); six gates run in two chains (`acceptance` alone; `build_sql`, then `code_analysis`, `build_sqlite`) with `qodana` and the light, advisory `security_scan` beside them, so at most two SQL Servers share the build node; `gate` waits for every chain and for `qodana`, prints the TRX summary and applies the build-result rules: a docs-only change passes with the gates skipped; otherwise each required gate must write its success marker, and `security_scan` is advisory. The build result is the required status `codefresh/ci`.*

![Dynamic: the step graph of workorders/release](../../../design/diagrams/dyn-release-pipeline.png)

*Dynamic, the step graph of `workorders/release`, the build of record. After `prepare`, `wake_nonprod` asks Octopus to run env-wake in `infra-nonprod` (it never waits or fails) while the gates run; `package` and `stage_images` follow `build_sql`, beside the other gates, and write only to the build volume; a passing gate with code changes leads to `image_reuse`, which picks either build, sign, attest and lock (`supply_chain`) or, on a rerun of the same commit, a check of the lock (`supply_chain_reuse`); both reach the Octopus handoff, which ends with the release `VERSION`. The pipeline never deploys.*

```text
main_clone ─┐
platform_clone ─┴─ prepare: VERSION, BUILD_BUILDNUMBER, CODE_CHANGED, IS_RELEASE, ARTIFACTS_DIR, SQL password, worktrees
   chain A: acceptance (Invoke-AcceptanceTests, SQL Server in dind on port 1434)
   chain B: build_sql (Build + CRAP, SQL Server in dind on 1433) ─ code_analysis ─ build_sqlite
   beside:  qodana (qodana_result records a successful qodana); security_scan (advisory, light)
   gate: once build_sqlite, qodana_result, acceptance and security_scan have finished: trx-summary.ps1, then gate.ps1 over the six gates
```

The gates run in two sequential chains (14.0 and 9.8 min measured) with `qodana` (5.0 min) beside them, so that one build fits a `Standard_D4as_v6` builds node (Q40, V03) with at most two SQL Servers; for the first five minutes `acceptance`, `build_sql` and `qodana` share the node; `docs/runbooks/build-duration.md` has the timings and the measurement method. SQL Server runs as a step service on the step's network (`shared_host_network: true`), so the app's DbUp console reaches it as `localhost` and keeps its certificate rule (ADR-IR24); `acceptance` moves its server to port 1434 (`MSSQL_TCP_PORT`, `SQL_SERVER_HOST=localhost,1434`) because `build_sql`'s server holds 1433 at the same time, and both servers are capped at 2 GiB (`MSSQL_MEMORY_LIMIT_MB`). `~/.dotnet/tools` is on `PATH` and `DOTNET_ROLL_FORWARD=LatestMajor` lets crap4dotnet 0.1.1 (a .NET 8 tool) run on the .NET 10 SDK. TRX files stay on the build volume under `artifacts/<build id>/` (the newest 10 builds are kept) and are summarised in the log.

`workorders/release` adds `wake_nonprod` right after `prepare`, in parallel with the gates; `package` and `stage_images` right after `build_sql`, beside the other gates (they only write to the build volume); then, after a passing gate:

```text
build_sql ─ package ─ stage_images ─┐
gate ───────────────────────────────┴─ image_reuse ─┬─ new VERSION: ui_image, worker_image, migrator_image ─ supply_chain ─┬─ octopus_preflight ─ octopus_packages ─ octopus_build_info ─ octopus_release
                                                    └─ rerun, tags locked (IMAGES_REUSED=true): supply_chain_reuse ──────────┘
```

- Images: `apps/workorders/{ui-server,worker,db-migrator}`, tags `<VERSION>` and `sha-<sha7>`, signed keyless through the Codefresh OIDC provider and Fulcio (`cosign.sign`); `supply_chain` adds the SBOM and provenance attestations and locks the tags.
- Handoff, with the Octopus CLI of the step image (ADR-IR18): `octopus package upload` (`ChurchBulletin.AcceptanceTests`, overwrite mode ignore), `octopus build-information upload` (four package IDs), `octopus release create --project workorders --channel Default --version <VERSION> --package …` with one explicit `--package` per package (M3), `--git-ref refs/heads/main`, `--release-notes-file`, `--ignore-existing`. Before the release, `release-notes.ps1` appends a `### CI summary` to the notes: counts only from this build's TRX files; gates that exited early on `CI_TREE_VERIFIED` are named with a link to the `codefresh/ci` build instead. Octopus releases take no file attachments, so the TRX files stay under `artifacts/<build id>/` on the pipeline volume.
- Build facts: before the image builds, `image_reuse` runs `build-facts.ps1`, which writes `build-facts.json` into `built/` of the UI context; the app repository's Dockerfile copies `built/` to `/app`, so the released image carries the record and the deployed app answers it at `GET /_build` (see [Build facts](#build-facts)).
- Re-runs mint the same `VERSION`, and the locked tags reject a second push. `image_reuse` checks the tags first: when every tag is locked, the image builds and `supply_chain` are skipped and `supply_chain_reuse` confirms the lock, so the re-run reaches the handoff (CAP-CF-014); a mixed state (some tags locked, others not) fails the build.

## Build facts

The released UI image carries `build-facts.json`, the record of the build it was released from, and the deployed app answers it at `GET /_build` (anonymous, read-only, the app's own CORS policy, as `/_healthcheck`). The health dashboard's "Code" card reads it (`buildPath` of its topology). `scripts/build-facts.ps1` writes it; each fact comes from the place of this build that already has it, and a fact this build does not have is `null`, never an estimate.

| Fact | Read from |
|---|---|
| `version` | `VERSION` (`prepare.ps1`), the value `build.ps1` stamps into the assemblies as `BUILD_BUILDNUMBER`. The app answers the version of its own assembly and ignores a record that names another |
| `commit`, `commitUrl` | `CF_REVISION` (else `HEAD`), `CF_REPO_OWNER`/`CF_REPO_NAME` |
| `builtAt` | The time the record is written (UTC), just before the image build |
| `buildUrl` | `CF_BUILD_URL`, the Codefresh release build |
| `code` | The tracked files of the application checkout (`git ls-files`): non-blank lines and files per language, by extension; generated, vendored and minified files and Markdown are not counted. No build output holds this number, so the script counts it |
| `tests` | Tests that ran (passed or failed) per suite, from this build's TRX files through `trx-summary.ps1 -PassThru` (the reader of the gate summary and the release notes): `unit` and `integration` from `build_sql`, `acceptance` from the `acceptance` gate |
| `coverage`, `complexity` | The Cobertura files coverlet writes for the unit and integration runs of `build_sql` (`artifacts/<build id>/build_sql/test/**/coverage.cobertura.xml`), merged per line; the cyclomatic complexity per method is in the same files |
| `crap` | The application's own CRAP audit of `build_sql` (`build_sql/crap-metrics/crap-by-file.json`, `crap-production-violations.json`): worst production score, the gate's threshold, production methods over it |
| `analysis.qodanaProblems` | The results of this build's Qodana scan (`artifacts/<build id>/qodana/qodana.sarif.json`) that are new or unchanged against the baseline |

What is `null` in a usual release, and why:
- `tests.acceptance`: the release does not run the acceptance suite unless `RELEASE_ACCEPTANCE=true` (`codefresh/ci` ran it on the same tree, and the tdd deployment runs it against the deployed app).
- `analysis`: with `CI_TREE_VERIFIED=true` the release does not re-run Qodana. The `codefresh/ci` build that passed it is another pipeline with another volume, and the committed baseline is not a scan of this commit, so the release has no count to record. `RELEASE_FULL_GATES=true` runs the scan in the release and fills it.

Rules that keep the step harmless:
- It runs in `image_reuse`, after the gate has passed and `stage_images` has staged the context, and before the image builds; the image builds depend on `image_reuse` only (CAP-CF-014), so the record needs no step of its own.
- It never fails the build (`|| echo`): without the file the app answers its version and nulls. An input that cannot be read costs its own section only.
- `stage-built.ps1` recreates the UI context in every build, so no record of an older build stays behind; a re-run that reuses the locked images builds none, and the released image keeps the record of its first run.
- Only `workorders/release` writes it. `workorders/ci` builds no image; a preview image has no record and its app answers the preview's version alone.

Locally, from an app checkout after `. ./build.ps1; Build` and the CRAP audit (the artifact folder is laid out as the pipeline's):

```sh
mkdir -p "$A/build_sql" && cp -R build/test crap-metrics "$A/build_sql/"
VERSION=2.5.0 ARTIFACTS_DIR="$A" pwsh -NoProfile -File "$S/build-facts.ps1" -Out "$A/build-facts.json"
```

## Sleep and wake

`wake_nonprod` asks Octopus to run runbook `env-wake` of project `platform-infrastructure` in `infra-nonprod` (config-as-code route `…/projects/<id>/refs%2Fheads%2Fmain/runbooks/env-wake/run/v1`) and does not wait. It never fails the build: a missed wake only costs time, because step 0 of every deployment wakes the cluster and waits (ADR-IR33). Codefresh never holds Azure rights and never starts or stops a cluster.

## Running the scripts locally

From an app checkout (full history), with `ENV` pointing at a checkout of this repo:

```sh
S="$ENV/codefresh/apps/workorders/scripts"
pwsh -NoProfile -File "$S/version.ps1"
pwsh -NoProfile -File "$S/changed-paths.ps1" | bash .github/scripts/detect-code-changes.sh --from-list -
GATE_build_sql=success GATE_qodana=failure CODE_CHANGED=true pwsh -NoProfile -File "$S/gate.ps1" -Advisory security_scan build_sql qodana
pwsh -NoProfile -File "$S/trx-summary.ps1" -Path build/test
pwsh -NoProfile -File "$S/buildinfo.ps1" -Out /tmp/buildinfo.json -ReleaseNotesOut /tmp/notes.md
pwsh -NoProfile -File "$S/stage-built.ps1" -Version "$BUILD_BUILDNUMBER"    # after Build and Package-Everything
```

## Starting and re-running `workorders/release`

- **Start it from the git trigger only.** A push to master does it. To re-run a commit, `POST /api/pipelines/run/<pipeline id>` with `{"trigger": "master-push", "branch": "master", "sha": "<commit>"}`. A run started without git context (the UI or API "run" with no trigger, `triggerType: MANUAL`) has no `CF_REVISION` or `CF_BRANCH`; the first step, `verify_trigger`, fails it at once with that message instead of `main_clone` failing with `pathspec ... did not match`.
- **`codefresh/release` on a re-run.** Codefresh posts the trigger's `commitStatusTitle` status only for builds that the GitHub webhook started (`webhookTriggered: true` on the build record). A trigger-API re-run has `webhookTriggered: false`, so the commit gets no `codefresh/release` status even though the build succeeds. A pipeline step could post it only with a GitHub token, and the release pipeline holds none on purpose (contexts: `app-workorders-ci`, `platform-registry`, `platform-octopus`; a statuses-only GitHub App is pending, R16). Where a status is needed after a re-run, the token holder posts it: `POST /repos/clearmeasure-aisf-sample-apps/20260923-001/statuses/<sha>` with `{"state": "success", "context": "codefresh/release", "target_url": "<build url>"}`.
- **A build that is terminated a second after it was created** (status `terminated`, no `started`) was not cancelled by concurrency: `concurrency: 1` queues. Read `terminationRequest` in `GET /api/builds/<id>`. On 2026-09-29 build `6abb3274...` carried `git-rate-limit-exceeded-error`: the build manager fetches this pipeline YAML from `main` of the environment repo when it elects a build, and GitHub rate-limited that fetch. No spec setting prevents it; re-run the commit through the trigger API as above.

## [VERIFY] before relying on them

- `CF_OIDC_REQUEST_URL` and `CF_OIDC_REQUEST_TOKEN` inside freestyle steps (`supply-chain.ps1` requests the `sigstore` audience itself).
- The negative-lookahead branch filter; the `steps.<name>.result` values read by `gate.ps1`.
- Step services with `shared_host_network: true` on the runner's dind.
- The tag lock with the `cf-apps-release` token (`metadata/write`, V04).
- Restart from a failed step; cron time zone (UTC assumed).
