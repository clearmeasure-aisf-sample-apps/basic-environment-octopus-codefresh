# Consistency notes

Cross-slice state of the environment repo after the integration review: every check and its outcome, every finding with its fix or its owner, and each integration-review queue item (`Q#`) with the design addendum that settles it (`ADR-IR#`, design §2.5). Earlier passes are summarised at the end.

| Field | Value |
|---|---|
| Pass | **Integration review pass.** All five packages complete (§11.6: 144 implementation files plus the design record). The environment repo holds every platform file; the application repo holds none (ADR-D18). |
| Runs | 2026-09-24T02:27Z and 02:32Z (same results) `validate-all.sh all` with `CI=true`, `PLATFORM_BOT_AUTHORS` set and `TF_VALIDATE=true`; gitleaks on the tree (repo config and default rules) and on the local history. |
| Root | The environment-repo root (a local copy that becomes the first commits on `main`) |
| Tools | yamllint 1.38.0; kustomize v5.8.1; kubeconform 0.8.0 (`-strict -ignore-missing-schemas`, built-in schemas plus the datreeio CRDs catalog); terraform 1.16.4; gitleaks 8.28.0 with `.gitleaks.toml`; mermaid 11 parser; python3 3.11 with PyYAML 6.0.1; shellcheck 0.11.0 |
| Overall | 8 of 8 sub-commands pass. `consistency`: 87 pass, 0 fail, 2 warn (both accepted, A1 and A2), 0 skip. 18 of 18 boundary rules and the bot-path audit pass. All three Terraform layers validate. No leaks. |

Severity scale:
- **High**: blocks bootstrap, a phase exit or the release path.
- **Medium**: breaks one procedure, weakens a control, or needs a decision before its phase.
- **Low**: naming, documentation or design-text alignment.

## Package completeness

| Package | Files / expected | Roots |
|---|---|---|
| codefresh-engineer | 21 / 21 | `codefresh/**`, `containers/**` |
| gitops-architect | 38 / 38 | `argocd/**`, `gitops/**` |
| octopus-architect | 31 / 31 | `.octopus/**`, `octopus/**` |
| sre-security | 38 / 38 | `terraform/**`, `policies/**`, `docs/runbooks/**`, `.gitleaks.toml` |
| pragmatist | 16 / 16 | `README.md`, `CODEOWNERS`, `.yamllint.yaml`, `contracts/**`, `scripts/**`, `docs/*.md`, `docs/walkthroughs/**` |

## Results by check

### `validate-all.sh all`

| Sub-command | Result | Detail |
|---|---|---|
| `yaml` | PASS | 55 files; 1 warning (line of 202 characters, `gitops/workorders/envs/prod/config/kustomization.yaml:22`, A3) |
| `kustomize` | PASS | 6 builds: `gitops/workorders/envs/{tdd,uat,prod}`, `gitops/workorders/previews`, `policies/kyverno/overlays/{nonprod,prod}` |
| `kubeconform` | PASS | 107 resources in 25 files, 107 valid, 0 invalid, 0 skipped (placeholders replaced by `ph-*` stand-ins in scratch copies) |
| `terraform` | PASS | `fmt -check -recursive` on `terraform/` and `octopus/terraform/`; `init -backend=false` and `validate` for all three layers |
| `mermaid` | PASS | 9 Markdown files with diagrams, design record included |
| `boundaries` | PASS | 18 of 18 rules, `codefresh/` and `containers/` included; bot-path audit on `main`: 1 commit audited, none by `platform-bots` |
| `consistency` | PASS | 87 pass, 0 fail, 2 warn, 0 skip (below) |
| `secrets` | PASS | gitleaks with `.gitleaks.toml`: no leaks, 1.40 MB; also clean with the default rules and on the local history |

### Terraform validate

| Layer | Result | Providers resolved |
|---|---|---|
| `terraform/foundation` | PASS | azurerm 5.6.0, azuread 3.9.0 |
| `terraform/environment` | PASS | azurerm 5.6.0, helm 3.3.0, kubernetes 3.2.1, random 3.9.1 |
| `octopus/terraform` | PASS | octopusdeploy 1.20.0 |

### `tool-boundaries.sh`

All PASS:
- **Codefresh:** TB01 forbidden step types; TB02 no argocd, kubectl, helm or `az aks` access to app clusters; TB03 no GitOps Runtime or Promotions; TB14 no Octopus API keys (`codefresh/` and `containers/`); TB16 no commits or pushes.
- **Argo CD:** TB04 no Image Updater; TB05 no syncWindows; TB09 Octopus annotations only on the three named Applications, and no tenant annotation.
- **Desired state and images:** TB06 no `latest` (including `containers/`).
- **Octopus:** TB07 no kubectl, Helm or Kubernetes steps; TB10 Trigger sync off; TB11 Codefresh is the only release creator; TB12 no deployment targets on app clusters.
- **Terraform:** TB08 no role assignments, locks or policy assignments in `terraform/environment`.
- **Stored credentials:** TB13a the provisioner stays out of `workorders`; TB13b stored Codefresh contexts stay unattached; TB13c stored variable sets are included nowhere.
- **Kyverno:** TB15 no `mutateDigest`.

### `consistency.sh`

| Check | What it compares | Pass | Fail | Warn |
|---|---|---|---|---|
| C01 | Contracts parse; all sections present | 1 | 0 | 0 |
| C02 | `argo.octopus.com/*` only on named Applications; slugs equal Octopus environments | 3 | 0 | 0 |
| C03 | Named Applications: project, single Git source, path, destination, automated prune and self-heal, PruneLast, retry, no finalizer | 3 | 0 | 0 |
| C04 | AppProjects per cluster, locked `default`, kind allow-lists, roles | 2 | 0 | 0 |
| C05 | Namespaces and labels per cluster | 2 | 0 | 0 |
| C06 | Root Application `platform-root`; bootstrap values (120 s, `octopus` account, admin off, RBAC policies) | 4 | 0 | 0 |
| C07 | Kustomize `images[].name` = registry + Octopus package ID; untagged base images; no unknown image or package names anywhere, `containers/` included | 7 | 0 | 0 |
| C08 | Pin files have the exact §7.6 shape | 3 | 0 | 0 |
| C09 | Rendered overlays: namespace, pinned images, `/alive` probes, identities, ESO waves, uat/prod redirects | 3 | 0 | 0 |
| C10 | Connection strings start with `Server=`; no `Data Source=` | 1 | 0 | 0 |
| C11 | Key Vault names against every ExternalSecret; secret names in Octopus and Terraform | 7 | 0 | 1 (A1) |
| C12 | Runbook scopes; `env-destroy` in `infra-nonprod` only; no other destroy | 8 | 0 | 0 |
| C13 | Twelve steps in order with scopes, packages, pools, containers and interlocks; no-target connectivity | 14 | 0 | 0 |
| C14 | Project variables against §7.2; no sensitive variable in Git; one toolchain image reference everywhere (ADR-IR11) | 3 | 0 | 0 |
| C15 | §7.2 names in `octopus/terraform`; stored objects looked up, never created | 2 | 0 | 0 |
| C16 | Codefresh specs and pipelines: runtimes, contexts, template (`main` of this repo), concurrency, steps, handoff with `RELEASE_NOTES_FILE`, env-checks sub-commands, `CI=true`, `PLATFORM_BOT_AUTHORS` | 10 | 0 | 0 |
| C17 | Environment config overlays: base, namespace, literals, no `images:`, `bluegreen` unreferenced, `RemotableBus__ApiUrl` | 3 | 0 | 0 |
| C18 | Kyverno identity, image scope and modes; add-on paths | 5 | 0 | 0 |
| C19 | Terraform layers: state keys, grants, bootstrap files that exist | 2 | 0 | 1 (A2) |
| C20 | Previews ApplicationSet (phase 6) | 1 | 0 | 0 |
| C21 | Placeholders outside §7 and the contracts | 0 | 0 | 0 |
| C22 | Runbook `TF_VAR_*` names declared in `terraform/environment` | 3 | 0 | 0 |
| **Total** | | **87** | **0** | **2** |

### Manual cross-slice probes

| # | Interface | Outcome |
|---|---|---|
| M01 | Every `#{...}` variable in `.octopus/**` against project, library-set, sensitive, prompted and output variables | Pass (53 names; `package.*`, `commit.*`, `build.*` are loop variables of the release-notes template) |
| M02 | Rendered kinds of each overlay against its AppProject allow-list | Pass (4 overlays) |
| M03 | Kyverno baseline against rendered workloads: resources, pinned tags, privileged, probes | Pass. The Worker is exempt from `require-probes` by label until WI-04. Preview images, the migrator included, get the ApplicationSet override `pr-{{.number}}-{{.head_sha}}`, which matches the `workorders/preview` tag `pr-<n>-<CF_REVISION>`. |
| M04 | Gateway registration names, environments and secret names against §7.2, §7.3 and §7.8 | Pass |
| M05 | Terraform seed of `argocd-repo-creds` against its ExternalSecret | Pass (`creationPolicy: Orphan`, `mergePolicy: Merge`); the seed now arrives from `ArgoCD.RepoReadCredential` |
| M06 | Octopus-issuer federated subjects in the foundation against account and feed subject keys | Pass |
| M07 | Foundation role matrix against §5.2 | Pass, with SQL DB Contributor in `uat` and `prod` (ADR-IR13) and PIM-eligible `secret-writers` (ADR-IR29) |
| M08 | Codefresh `${{...}}` references against contexts, exports and step outputs | Pass (5 pipelines) |
| M09 | Platform names in every Markdown doc against the contracts and the implementation | Pass: no stale names; no `app:.codefresh`, `app:containers`, `--app-repo` or staging path remains |
| M10 | `workorders/db-migrator` entrypoint and `/app/scripts` against the preview Job arguments | Pass |
| M11 | `configure-db-principals-<env>` user names and the `nServiceBus` schema against the UAMIs and migration `022` | Pass: idempotent on both sides |
| M12 | `build.ps1` (`BUILD_BUILDNUMBER` → `/p:Version`) against `verify-version` | Pass |
| M13 | `app-commit:` writer (`buildinfo.sh`, 40-hex, via `RELEASE_NOTES_FILE`) against the `report-commit-status` reader | Pass |
| M14 | Acceptance-step settings against the suite's configuration keys | Pass (`ApplicationBaseUrl`, `StartLocalServer`, `AcceptanceTests__AllowDestructiveReset`) |
| M15 | Channel package rules against process package references | Pass |
| M16 | Bootstrap copy of `platform-addons` against `projects.yaml` | Pass: identical in both clusters |
| M17 | Image users and read-only root filesystem against pod security contexts | Pass: `runAsNonRoot` (numeric `$APP_UID`), `/tmp` and `/app/.diagnostics` emptyDirs (ADR-IR31) |
| M18 | Chart-version placeholders across gitops, Terraform and the contracts | Pass: `<argo-cd-chart-version>` everywhere |
| M19 | Inline scripts: `report-commit-status` (GitHub App token) and `supply_chain` (Docker config from the context) | Pass: `bash -n` and shellcheck (only SC2050 on Octopus `#{...}` substitutions, expected) |

## Integration review queue: resolution

| Q# | Item | State | Fix and decision |
|---|---|---|---|
| 1 | `TF_VAR_*` names | Resolved | `TF_VAR_octopus_worker_registration_token`, `TF_VAR_argocd_repo_read_credential` in `env-{plan,apply,destroy}.ocl` (ADR-IR1); C22 passes |
| 2 | Kyverno PolicyExceptions off | Resolved | `features.policyExceptions.enabled: true`, `namespace: kyverno` in both add-ons (ADR-IR2); CEL kind [VERIFY] |
| 3 | Worker against `require-probes` | Resolved (already in files) | Match condition `worker-exempt-until-wi-04`, removed with WI-04 (ADR-IR3) |
| 4 | `policies/**` ownership | Resolved | Security owners alone: `CODEOWNERS`, §6.1, §6.2 (ADR-IR4) |
| 5 | Provider pins | Resolved | §7.10 and §11.4 say `azurerm ~> 5.6`, `azuread ~> 3.9` (ADR-IR5) |
| 6 | New placeholders; Docker feed | Resolved | Ratified in §7.1 and the contracts; feed `docker-hub` created in `octopus/terraform/feeds.tf`; preview attestor pins `<CF_PREVIEW_PIPELINE_ID>` (ADR-IR6); C21 clean |
| 7 | Per-UAMI client IDs | Resolved | `<client-id-of-<uami name>>` in gitops, add-ons and `terraform.tfvars.example` (ADR-IR7) |
| 8 | Preview migrator image | Resolved | `workorders-previews/db-migrator` built by `workorders/preview`, mapped by the ApplicationSet (ADR-IR8) |
| 9 | `octopus/terraform` state key | Resolved | `octopus-space.tfstate` (ADR-IR9) |
| 10 | Supply-chain registry credentials | Resolved | Secret context `workorders-release` with `ACR_TOKEN_NAME`, `ACR_TOKEN_PASSWORD`; step-local Docker config (ADR-IR10). Unblocks P1 |
| 11 | `StepImage.CiDotnet` home | Resolved | Per project, digest-pinned, equality checked by C14 (ADR-IR11) |
| 12 | Contract-file keys | Resolved | `promptedVariables`, `notationTokens`, `placeholderPatterns` ratified (ADR-IR12) |
| 13 | SQL DB Contributor in UAT | Resolved | Foundation default `["uat", "prod"]` (ADR-IR13) |
| 14 | Committed `{nonprod,prod}.tfvars` | Resolved | Accepted: the repo is private (R2); identifiers only, secrets via `TF_VAR_*` (ADR-IR14). Files are created at bootstrap step 2 |
| 15 | Bootstrap repo credential holder | Resolved | Read-only GitHub App; Octopus sensitive variable `ArgoCD.RepoReadCredential` plus the platform vaults (ADR-IR15) |
| 16 | Octopus System Manager | Superseded | No System Manager exists; the Space Manager key of `AISF-Service-Account` is the only credential and nothing needs system rights (ADR-IR32) |
| 17 | Approvers can override the freeze | Accepted risk | No custom roles (ADR-IR32): approvers hold Project Deployer on their environment; `sod-guard` and the override reason are the controls |
| 18 | Floating `octopuslabs/octopus-cli` | Decided, phased | Accepted for TDD-only phases; pinned freestyle steps before the P3 exit (ADR-IR18). Open: codefresh-engineer |
| 19 | Account-wide registry integrations | Resolved; residual risk accepted | Pull-only `acr-platform-pull`, digest-pinned step image, YAML only from `main` (ADR-IR19) |
| 20 | Worker chart roles forced to namespaced | Resolved | Ratified (ADR-IR20); upgrade behaviour [VERIFY] |
| 21 | `Octopus.ArgoCDUpdateImageTags` | Resolved | Adopted in the OCL, the contracts and §7.2 (ADR-IR21); export comparison [VERIFY] |
| 22 | Output variables keyed by step name | Resolved | §7.2 uses the name; `sod-guard` keeps the slug fallback (ADR-IR22) |
| 23 | `RELEASE_NOTES_FILE` | Resolved | §7.7, E18 and the contracts amended; C16 checks `RELEASE_NOTES_FILE` and the `app-commit:` writer (ADR-IR23) |
| 24 | Preview SQL certificate | Resolved | Trust option added to WI-07; no Service-name workaround (ADR-IR24) |
| 25 | `CreateNamespace` with no cluster-scoped kinds | Open [VERIFY], phase 6 | Fallback recorded (ADR-IR25). Owner: gitops-architect in the phase-6 spike |
| F (directive) | New app repo `20260923-001` | Resolved | Contracts `appRepo`, `GitHub.AppRepository`, OCI source label, the previews ApplicationSet, specs and clones, README, bootstrap, walkthroughs, runbooks, design §6, §7 |
| E, H | All-in-environment-repo layout | Resolved | ADR-D18 rewritten; §6, §7.5, §7.7, §11; check scripts drop `app:` and `--app-repo` and scan `containers/` |
| H | R3, R4, R5 status | Recorded | Design §10 statuses; `infra-nonprod` import documented in `docs/bootstrap.md` step 3 and `octopus/terraform/environments.tf` |
| I | Merge gate of the fork | Resolved | `codefresh/ci` required from day one; Actions stays off (ADR-IR26); contributor guidance in the README |
| I | R16 revisited | Resolved | Statuses-only GitHub App; `report-commit-status` mints an installation token (ADR-IR27) |

## Findings from earlier passes

| ID | Finding | State | Fix |
|---|---|---|---|
| F1 | Runbooks passed `TF_VAR_<worker-registration-token-variable>` | Resolved | Q1 |
| F2 | `RELEASE_NOTES_FILE` against §7.7 | Resolved | Q23 |
| F3 | Placeholders outside §7.1 | Resolved | Q6; `<name>`, `<object_id>` added to `notationTokens` |
| F4 | Per-UAMI client-ID placeholders spelled two ways | Resolved | Q7 |
| F5 | `StepImage.CiDotnet` in both projects | Resolved | Q11 |
| F6 | `octopus/terraform` backend key undecided | Resolved | Q9 |
| F7 | `policies/**` owned by both teams | Resolved | Q4 |
| F8 | Contract-file keys awaiting ratification | Resolved | Q12 |
| N1 | `db-restore-pitr` pool reset through `/_diagnostics/*`, which WI-02 removes | Resolved | The swap runs with the app at zero replicas; the reset step is gone (ADR-IR28; OCL, runbook doc, §7.2) |
| N2 | No human secret writer | Resolved | Entra group `secret-writers`, PIM-eligible Key Vault Secrets Officer (ADR-IR29; foundation, bootstrap, rotation runbook) |
| N3 | Bootstrap ordering: cluster OIDC issuer into the foundation; 24-hour group lag | Resolved | `docs/bootstrap.md` steps 2.5 and 5.5 |
| N4 | Image policies evaluated in the background without registry identity | Resolved | Background evaluation off for both image policies (ADR-IR30) |
| N5 | NServiceBus diagnostics under a read-only root | Resolved in files; [VERIFY] | `/app/.diagnostics` emptyDir (ADR-IR31); fatality proven in the phase-2 spike (gitops-architect) |
| N6 | Prod stays denied until the first prod deployment | Resolved | `docs/bootstrap.md` step 11.5 |
| N7 | `<argocd-chart-version>` spelling; unused `<kyverno-policies-chart-version>` | Resolved | Contracts use `<argo-cd-chart-version>`; the unused one is removed |
| N8 | Writers of `workorders-api-validation-key` and the OpenAI key | Resolved | §7.8 writer column, contracts `writer`, bootstrap step 5 |

## Accepted, no action

| ID | Finding | Owner | Evidence |
|---|---|---|---|
| A1 | No ExternalSecret for `argocd-sso-client-secret`: SSO uses federation (Q15 default; `entra.tf`) | gitops-architect | C11 warn |
| A2 | `terraform/environment` never names `environment-{class}.tfstate`: the runbooks pass `-backend-config=key=#{Terraform.StateKey}` | — | C19 warn |
| A3 | One line of 202 characters (a connection-string literal) | gitops-architect | yamllint warning |

## Open items and owners

Nothing below blocks P0. Each item has an owner and a phase.

| Item | Owner | When |
|---|---|---|
| Handoff on pinned freestyle steps (ADR-IR18) | codefresh-engineer | Before the P3 exit |
| Phase-2 spike [VERIFY] items: Argo CD action type and property keys (ADR-IR21), CEL `PolicyException` flags (ADR-IR2), `JsonEscape` in `EnvVariables` (Q21), agent upgrades under namespaced roles (ADR-IR20), NServiceBus diagnostics (ADR-IR31), token tag lock (Q6), the Codefresh OIDC `sub` | octopus-architect, sre-security, gitops-architect, codefresh-engineer | P2 |
| `CreateNamespace` without cluster-scoped kinds (ADR-IR25) | gitops-architect | P6 |
| Fork pull request shows `codefresh/ci` after a maintainer push (Q19) | codefresh-engineer | First external contribution |
| WI-01 to WI-13, including the WI-07 trust option and the WI-13 base-image pin | App team (R12) | Per §8 |
| User actions: R3 machine user or GitHub App; R4 variable-set scope and P2 retirement; R6, R7, R9, R10, R11, R15, R16, R18, R21, R22 (old bootcamp branch, proxy 403), R23, R24, R25, R26, R27 | User | Design §10 |

## Earlier passes (summary)

- **First pass (CN-01 to CN-16):** files staged, C16 and C18 green, `.gitleaks.toml` in use; CN-05 → Q1, CN-06 → Q15, CN-07 → Q14, CN-08 → Q13, CN-09 → Q11, CN-10 and CN-11 → Q6, CN-12 resolved in files (env-checks sets `CI=true` and guards `PLATFORM_BOT_AUTHORS`), CN-13 to CN-15 accepted (A1–A3), CN-16 → Q4. All resolved above.
- **Final cross-slice pass (before the review):** 7 of 8 sub-commands passed; consistency 82 pass, 4 fail (Q1 ×3, Q23), 21 warn. Every failure and warning is resolved above.

## Checker notes

- **Terraform validate.** `TF_VALIDATE=true` needs a short `TMPDIR` (provider plugins open Unix sockets with a path-length limit); CI runners use `/tmp`. A plugin cache (`TF_PLUGIN_CACHE_DIR`) avoids downloads.
- kubeconform validates scratch copies with `ph-*` stand-ins for placeholders; committed files are unchanged.
- C09 accepts `/ready` for readiness only (§7.9, WI-01).
- Not checkable from files: console actions, Octopus database values, runtime identity misuse ([tool-boundaries.md](tool-boundaries.md)).

## Reproduce

From the environment-repo root:

```bash
CI=true PLATFORM_BOT_AUTHORS='<platform-bots-author-regex>' \
  PATH="<tools>/bin:$PATH" MERMAID_VALIDATOR=<node script that parses mermaid blocks> \
  scripts/checks/validate-all.sh all
scripts/checks/tool-boundaries.sh                               # lint only
TF_VALIDATE=true TMPDIR=/tmp/tfv scripts/checks/validate-all.sh terraform   # downloads providers
gitleaks dir . --config .gitleaks.toml --redact
```
