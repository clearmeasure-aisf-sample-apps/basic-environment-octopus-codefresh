# Consistency notes

Cross-package state of the environment repo after the ADR-IR34 multi-app pass: what the checks now cover, their results on the tree, and every open finding with its owner (design §11.10). Earlier passes (the integration review `ADR-IR1` to `ADR-IR32`, the sleep and wake passes of ADR-IR33) are recorded in this file's Git history and in design §2.5.

| Field | Value |
|---|---|
| Pass | **ADR-IR34 multi-app pass**, package `pragmatist` (§11.7.5), run while the `codefresh-engineer`, `octopus-architect`, `gitops-architect` and `sre-security` packages were still landing |
| Runs | 2026-09-24T07:10Z and 07:19Z `CI=true validate-all.sh all` with `PLATFORM_BOT_AUTHORS` set to a placeholder identity; then each changed check and `dotnet test tests/Platform.Conformance.sln --filter TestCategory=Offline` again at 07:25Z |
| Root | The environment-repo root (a local copy that becomes the first commits on `main`) |
| Tools | yamllint 1.38.0; kustomize v5.8.1; kubeconform (source build, `-strict -ignore-missing-schemas`); terraform 1.16.4; gitleaks 8.28.0 with `.gitleaks.toml`; the mermaid 11 parser; python3 3.11 with PyYAML 6.0.3; shellcheck 0.11.0; .NET SDK 10.0.401 (`global.json` 10.0.100, roll forward) |
| Overall | 7 of 10 sub-commands pass: `yaml` (warnings only), `kustomize`, `kubeconform`, `terraform`, `mermaid`, `secrets`, `onboarding`. `consistency` (111 pass, 1 fail, 1 warn) and `boundaries` (24 rules, 1 fail) fail on two lines of other packages (K2, K13). `dotnet-offline`: 216 of 218 offline tests pass, and the onboarding tool's 86; the two failures are CAP-KIT-005 and CAP-KIT-006, which wrap the two scripts |

Severity scale:
- **High**: blocks bootstrap, a phase exit or the release path.
- **Medium**: breaks one procedure, weakens a control, or needs a decision before its phase.
- **Low**: naming, documentation or design-text alignment.

## What changed in the checks

| Check | Change (ADR-IR34) |
|---|---|
| Descriptors | New `apps/schema.json` (JSON Schema 2020-12, `additionalProperties: false`) and the onboarding tool's cross-descriptor rules. `consistency.sh` C24 keeps a basic descriptor check for trees without .NET |
| C07, C08 | Moved to `Platform.Onboarding check`: scaffold completeness per descriptor, pin shape (declared images, `newTag`, no digest or `newName`), Helm charts vendored in the app folder, cross-app references, the blast radius of a pull request |
| C09 | Renders every app overlay with kustomize: namespaces `<app>-*` only, no cluster-scoped or denied kinds, ExternalSecrets on store `<app>-<env>`, images under `apps/<app>/` |
| C10 | Database endpoints: a foreign namespace fails; a host other than `db` warns |
| C13, C14, C16, C17 | Retired: they checked app #1's process, variables, Codefresh steps and configuration, which the app now owns |
| C18 | Kyverno needs `Audit` in nonprod and `Deny` or `Enforce` in prod |
| C19 | The layers `foundation`, `build`, `tier`, `apps/tier`, `apps/grants`; no Azure SQL; `sha1(` and `disk-` in `terraform/apps`; state keys warn only (they come from backend configuration) |
| C20 | Retired paths of §11.8, retired names and display names outside the app-scoped paths; lines marked `name-lint: allow` are migration records. Findings are grouped by owning package |
| C21 | §7.0 placeholders, the retired ones fail; XML project files are skipped |
| C23 | Step 0 of every app process that touches a cluster deploys `platform-wake` (condition Always); in-cluster app runbooks wait with `Wake.WaitMinutes`; `platform-wake` reads only `PlatformWake.*` |
| C25 | New: the Codefresh handshake. Fork events off; contexts in their lane (`platform-octopus` on release pipelines only); M1 image paths; M3 for both the typed step and the `octopus release create` command (explicit `--package`, no default package version), for apps and starters |
| TB08 | Covers `terraform/tier` and `terraform/apps/tier` |
| TB09 | Octopus annotations only in `gitops/platform/tenant/` |
| TB14, TB16, TB18 | Conformance exception: the `platform-env/conformance*` pipelines and the scripts only they run (`conformance-*.sh`, `sandbox-git.sh`, `octopus-runbook.sh`) |
| TB17, TB19, TB20 | Renamed to `platform-infrastructure`; `PlatformWake.*` only in `platform-wake` |
| TB21 | New, trust boundary TB2: one runtime `aks-platform-build/codefresh` in every spec; no grant or federated credential in `terraform/build`; no cloud identity in the runner values, app pipelines or starters |
| TB22 | New, name lint: no platform file names an app outside the app-scoped paths. Exempt: the fixture `sandbox`, test fixtures in `tests/` folders (Kyverno CLI, `terraform test`), labelled examples, `moved` blocks and `name-lint: allow` lines |
| `validate-all.sh` | New sub-commands `onboarding` (the tool's `check`, with `--base origin/<main>` in CI) and `dotnet-offline` (`dotnet test tests/Platform.Conformance.sln --filter TestCategory=Offline`, TRX); yamllint runs on root-relative paths so the `ignore` patterns apply; kustomize targets cover the app overlays, previews, platform kustomizations and Kyverno overlays |
| `.yamllint.yaml` | Helm templates and build output ignored; implicit octal values warn (the Kubernetes file-mode idiom) |

Negative fixtures, run on scratch copies: a `--package-version` added to app #1's `octopus release create` and the `--package` flags removed from a starter both failed C25; an app file naming another app's store failed `check`, and the same text in a comment or on a `name-lint: allow` line passed.

## Findings

| ID | Finding | Severity | Owner | State |
|---|---|---|---|---|
| K1 | The legacy paths `codefresh/workorders/`, `codefresh/specs/`, `codefresh/pipelines/`, `codefresh/images/`, `codefresh/preview/` and `containers/workorders/` failed C20, C21, C25, TB14, TB18, TB21 and TB22 | High | codefresh-engineer | Resolved during the pass: moved (§11.8) |
| K2 | `codefresh/platform/integrations.yaml` (lines 78–82) lists the superseded objects `workorders/ci-image`, `workorders-octopus`, `workorders-ci` and `workorders-release` for `--prune`: C20 and TB22 fail | Medium | codefresh-engineer | Open: mark those lines `# name-lint: allow` (migration records) |
| K3 | `tests/Platform.Conformance.Tests/Azure/` did not compile, so no offline test could build | High | sre-security | Resolved during the pass |
| K4 | `CatalogueConsistencyTests` found Octopus tests without catalogue entries | Medium | octopus-architect | Resolved during the pass: `octopus.yaml` landed; the consistency tests pass |
| K5 | `catalogue/capabilities.d/azure.yaml` named the retired runbook `db-restore-pitr`; the nonprod Kyverno overlay lacked an explicit `mutateDigest: false` (TB15) | Low | sre-security | Resolved during the pass |
| K6 | Harness tests and `tests/platform.settings.json` still use `aks-workorders-*`, `rg-workorders-*` and `Work Orders` | Medium | harness engineer | Open (WARN): the settings must name `aks-platform-*` and `rg-platform-*` before the live suite runs |
| K7 | `defaultMode: 0440` in `gitops/platform/components/db/mssql-2022-express/{db,db-init}.yaml` | Low | gitops-architect | Accepted as a warning: Kubernetes reads it as octal; `288` is the portable spelling |
| K8 | `docs/capabilities.md` renders four fragments (53 capabilities); the Codefresh and GitOps fragments are not written yet, and `env-checks` fails on a stale render | Medium | main loop | Open: re-render with `dotnet run --project tests/Platform.Conformance.Report -- render-catalogue` after every package lands |
| K9 | CAP-KIT-005 and CAP-KIT-006 fail until K2 and K13 close; they wrap `consistency.sh` and `tool-boundaries.sh` | High | codefresh-engineer, octopus-architect | Open |
| K10 | The end-to-end pass merges its own pull request on `master` of `20260923-001`, whose protection requires one review (design §6.3) | Medium | main loop | Open [VERIFY]: a bypass for the operator's token, or an approval from a second account, at P1-12 |
| K11 | `apps/sandbox.yaml` names the repository `<sandbox-app-repo>` until the repository exists | Low | main loop | By design: replaced at P1-11 |
| K12 | The tool projects `tools/Platform.Onboarding` and `tools/Platform.Onboarding.Tests` are not in `tests/Platform.Conformance.sln`; `validate-all.sh dotnet-offline` runs the tool's tests separately until they are | Low | harness engineer | Open (§11.7.6) |
| K13 | `octopus/terraform/apps.tf`, check `descriptors_found`, requires the descriptors `workorders` and `sandbox` by name: a platform file that names app #1 (TB22), and retiring app #1 would break the apply | Medium | octopus-architect | Open: assert that the `moved` targets exist, mark the lines `# name-lint: allow` as a migration record, or require only the fixture |

## Decisions of this pass

| Topic | Decision |
|---|---|
| Platform vault keys | `appinsights-connection-string` (always) and `azure-client-id` (with a workload identity) are platform keys in every app vault, beside the three `db-*` passwords; descriptor secrets may not use these names. `terraform/apps/tier` and app #1's ExternalSecret use the same names |
| Descriptor `repositories[].name` | A repository of `clearmeasure-aisf-sample-apps`, or a `<placeholder>` until the repository exists (the sandbox before P1-11) |
| Blast radius | Only a change that adds or deletes a descriptor is an onboarding or retirement change and must stay inside that app's paths; other mixed changes warn and are reviewed as platform changes |
| Starter tokens | `<app>` or `__APP__`, `<app-repo>`, `<app-branch>`, `<project>`, `<namespace>`, and the path segments `<env>` and `<deployable>` (each with its `__X__` form); a starter's `README.md` is not copied; a text file two Octopus starters share is appended |
| Database folder | When a starter has no `db` folder, the kit writes `gitops/apps/<app>/envs/<env>/db/kustomization.yaml` from the engine component and the Key Vault credentials component |
| Cross-app references | Checked in code and manifests; whole-line comments and `name-lint: allow` lines are prose, as in TB22 |
| The end-to-end test | `[Explicit]`, category Prod; the repository comes from `PLATFORM_E2E_REPO` (default: the first repository of the app's descriptor), and any owner other than `clearmeasure-aisf-sample-apps` fails before a request is made |
