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
| `specs/{ci,release,preview}.yml` | Specs: triggers, contexts, runtime, YAML location, concurrency; registered by `codefresh/register.sh` |
| `integrations.yaml` | The app-owned optional context `app-workorders-ci` (AI keys of the LLM tests), declared without values |
| `version.env` | `MAJOR=2`, `MINOR=5` |
| `scripts/version.sh` | `MAJOR.MINOR.<first-parent height>` on `master` (`RELEASE_BRANCH`), `…-ci.<sha7>` elsewhere |
| `scripts/changed-paths.sh` | Changed paths for docs-only detection (`master`: `HEAD^1..HEAD`; branches: merge base with `origin/master`) |
| `scripts/gate.sh` | `build-result` semantics over the step results |
| `scripts/trx-summary.ps1` | Markdown summary of every TRX file (TRX is the only test-result format; no JUnit) |
| `scripts/buildinfo.sh` | Octopus build information and the release notes file |
| `scripts/stage-built.sh` | Lean Docker contexts for the three images |
| `scripts/supply-chain.sh` | SBOM and provenance attestations, keyless; ACR tag lock |
| `containers/apps/workorders/worker/Dockerfile`, `containers/apps/workorders/db-migrator/{Dockerfile,migrate.sh}` | Worker and DbUp migrator images (the UI image keeps the app repo's root `Dockerfile`) |

## Pipelines

![Level 3: Codefresh projects, pipelines and their triggers](../../../design/diagrams/c4-3-codefresh-a.png)

*Level 3, Codefresh projects and pipelines (plan BASIC_1: one build at a time) and what starts each. App repos start `<app>/ci` on every branch but the release branch, `<app>/release` on it and `workorders/preview` on labelled same-repo pull requests; fork events are off. Each pipeline posts its `codefresh/*` status; `codefresh/ci` is the required check of master. The environment repo starts env-checks and ci-image-dotnet; crons start ci-image-dotnet weekly and conformance-arm, conformance-destructive and registry-retention once P1-13 enables them. conformance-arm pushes the sandbox commits and queues conformance; `codefresh/register.sh` creates or replaces every project, pipeline, context and integration by name.*

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

*Dynamic, the step graph of `workorders/ci`. Both clones feed `prepare` (`VERSION`, `CODE_CHANGED`); six gates run in two chains (`build_sql`, then `acceptance`; `code_analysis`, `build_sqlite`, `qodana`, `security_scan`), so at most two heavy steps share the build node; `gate` waits for every chain, prints the TRX summary and applies the build-result rules: a docs-only change passes with the gates skipped; otherwise each required gate must write its success marker, and `security_scan` is advisory. The build result is the required status `codefresh/ci`.*

![Dynamic: the step graph of workorders/release](../../../design/diagrams/dyn-release-pipeline.png)

*Dynamic, the step graph of `workorders/release`, the build of record. After `prepare`, `wake_nonprod` asks Octopus to run env-wake in `infra-nonprod` (it never waits or fails) while the gates run; a passing gate with code changes leads to `package` and `stage_images`; `image_reuse` picks either build, sign, attest and lock (`supply_chain`) or, on a rerun of the same commit, a check of the lock (`supply_chain_reuse`); both reach the Octopus handoff, which ends with the release `VERSION`. The pipeline never deploys.*

```text
main_clone ─┐
platform_clone ─┴─ prepare: VERSION, BUILD_BUILDNUMBER, CODE_CHANGED, IS_RELEASE, SQL password, worktrees
   chain A: build_sql (Build + CRAP, SQL Server in dind) ─ acceptance (Invoke-AcceptanceTests, SQL Server in dind)
   chain B: code_analysis ─ build_sqlite ─ qodana ─ security_scan (advisory)
   gate: trx-summary.ps1, then gate.sh (finished on all six)
```

The gates run in two sequential chains so that one build fits a `Standard_D4as_v6` builds node (Q40, V03). SQL Server runs as a step service on the step's network (`shared_host_network: true`), so the app's DbUp console reaches it as `localhost` and keeps its certificate rule (ADR-IR24). `~/.dotnet/tools` is on `PATH` and `DOTNET_ROLL_FORWARD=LatestMajor` lets crap4dotnet 0.1.1 (a .NET 8 tool) run on the .NET 10 SDK. TRX files stay on the build volume under `artifacts/<build id>/` (the newest 10 builds are kept) and are summarised in the log.

`workorders/release` adds `wake_nonprod` right after `prepare`, in parallel with the gates, then:

```text
gate ─ package ─ stage_images ─┬─ ui_image ───────┐
                               ├─ worker_image ───┼─ supply_chain ─ octopus_preflight ─ octopus_packages ─ octopus_build_info ─ octopus_release
                               └─ migrator_image ─┘
```

- Images: `apps/workorders/{ui-server,worker,db-migrator}`, tags `<VERSION>` and `sha-<sha7>`, signed keyless through the Codefresh OIDC provider and Fulcio (`cosign.sign`); `supply_chain` adds the SBOM and provenance attestations and locks the tags.
- Handoff, with the Octopus CLI of the step image (ADR-IR18): `octopus package upload` (`ChurchBulletin.AcceptanceTests`, overwrite mode ignore), `octopus build-information upload` (four package IDs), `octopus release create --project workorders --channel Default --version <VERSION> --package …` with one explicit `--package` per package (M3), `--git-ref refs/heads/main`, `--release-notes-file`, `--ignore-existing`.
- Re-runs mint the same `VERSION`; the locked tags reject a second push. After a failure past `supply_chain`, restart the build from the failed step [VERIFY].

## Sleep and wake

`wake_nonprod` asks Octopus to run runbook `env-wake` of project `platform-infrastructure` in `infra-nonprod` (config-as-code route `…/projects/<id>/refs%2Fheads%2Fmain/runbooks/env-wake/run/v1`) and does not wait. It never fails the build: a missed wake only costs time, because step 0 of every deployment wakes the cluster and waits (ADR-IR33). Codefresh never holds Azure rights and never starts or stops a cluster.

## Running the scripts locally

From an app checkout (full history), with `ENV` pointing at a checkout of this repo:

```sh
S="$ENV/codefresh/apps/workorders/scripts"
bash "$S/version.sh"
bash "$S/changed-paths.sh" | bash .github/scripts/detect-code-changes.sh --from-list -
GATE_build_sql=success GATE_qodana=failure CODE_CHANGED=true bash "$S/gate.sh" --advisory security_scan build_sql qodana
pwsh -NoProfile -File "$S/trx-summary.ps1" -Path build/test
bash "$S/buildinfo.sh" --out /tmp/buildinfo.json --release-notes-out /tmp/notes.md
bash "$S/stage-built.sh" --version "$BUILD_BUILDNUMBER"    # after Build and Package-Everything
```

## [VERIFY] before relying on them

- `CF_OIDC_REQUEST_URL` and `CF_OIDC_REQUEST_TOKEN` inside freestyle steps (`supply-chain.sh` requests the `sigstore` audience itself).
- The negative-lookahead branch filter; the `steps.<name>.result` values read by `gate.sh`.
- Step services with `shared_host_network: true` on the runner's dind.
- The tag lock with the `cf-apps-release` token (`metadata/write`, V04).
- Restart from a failed step; cron time zone (UTC assumed).
