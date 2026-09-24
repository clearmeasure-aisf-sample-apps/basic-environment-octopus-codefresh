# Memo: multi-app Codefresh (codefresh-engineer), revised for "projects vary" (§9, §10)

Scope: Codefresh. Apps have their own architectures and 1..n freely differing Codefresh projects; the platform fixes only the boundary handshake, and no platform secret enters an app pipeline. Tool lanes unchanged.

## 0. The only mandatory elements

Only these are mandatory; everything else is the app's choice.

| # | Element |
|---|---|
| M1 | Every releasable OCI artifact (image, or Helm chart) is pushed to `<acr>/apps/<app>/…` with a final SemVer tag, using the app's own push token |
| M2 | Every artifact carries a keyless cosign signature from a release pipeline of that app, plus an SBOM attestation |
| M3 | The handoff: per Octopus project, a signed release manifest pushed LAST to `<acr>/apps/<app>/manifests/<octopus-project>:<VERSION>` |
| M4 | Never deploy: no `kubectl`, Helm, Argo CD or `az`, no GitOps writes. Guaranteed by construction, because no credential for them is reachable (section 4) |

The platform owns what grants capability (specs, tokens, signer policies), never the YAML.

## 1. Starters: shape-independent, scaffold then own

Starters (`codefresh/templates/<starter>/`) follow architecture shapes; language starters are filled-in examples.

| Starter | Shape |
|---|---|
| `minimal` | One image built from the repo's Dockerfile, one test command, the handshake. Any language |
| `multi-image` | N services, one build step per service (the example shows two), one manifest listing all digests |
| `helm-chart` | Images plus a chart pushed as an OCI artifact under `apps/<app>/charts/`, signed like an image |
| `dotnet-buildps1`, `node`, `java` | Language examples; `dotnet-buildps1` is app #1's current pipelines, an example, not a reference |

What "scaffold then own" means in practice:
- `scripts/apps/scaffold --app <app> --project <project> --starter <starter>` copies the starter into the YAML location (section 5), replacing only identity placeholders and recording the starter commit in `.scaffold`.
- Nothing regenerates, compares or constrains the YAML afterwards; `scaffold --diff` offers opt-in updates.

## 2. Optional building blocks

| Form | Role |
|---|---|
| Account-level custom step types (`codefresh create step-type -f`), pinned `type: <account>/<step>:<version>` [VERIFY private step types] | `publish-artifact` (push with the app token, keyless sign, SBOM attestation) and `release-manifest` (write, push last, sign). The starters use them; upgrades are explicit version bumps; publishing needs an account admin |
| Scripts (`version.sh`, `gate.sh`, `supply-chain.sh`, …) | Copied at scaffold time; apps may fork them |

App pipelines stop cloning the platform repo, which needs platform credentials (section 4).

## 3. Handshake and Octopus: manifest plus feed trigger

The manifest is a `FROM scratch` image holding only `release.json`: `schema: 1`, `app`, `octopusProject`, `version`, `commit`, `repo`, `buildUrl`, `artifacts: {<repository>: <digest>}`.

**How Octopus turns it into a release** (the octopus-architect implements this; per Octopus project, 1..n per app):
1. **Feed.** A shared feed, `acr-apps`, of type Azure Container Registry (or Docker Container Registry). It pulls with the platform's ACR-pull identity over OIDC. Octopus holds this; no app sees it [VERIFY feed type with triggers].
2. **Process.** The first step, `verify-release` (a shared step template, mandatory in app Octopus projects because M2 and M3 depend on it), references `apps/<app>/manifests/<project>`. It:
   verifies signatures and SBOM attestations of the manifest and every listed artifact against the app's release-pipeline identity, and checks that each `<repository>:<VERSION>` resolves to the manifest digest. Later steps reference the artifact packages.
3. **Trigger.** An external feed trigger, `release-manifest`, on channel `Default`, with the manifest as its only source. The docs advise triggering on the package pushed last and evaluate triggers every three minutes; config-as-code projects work from the default branch (<https://octopus.com/docs/projects/project-triggers/external-feed-triggers>).
4. **Channel rules.** Pre-release rule `^$` on every package: only final SemVer counts. Non-SemVer tags such as `sha-*` are ignored.
5. **Release number.** Release versioning is set to "use the version number from an included package", the manifest, so the release number equals `VERSION`.
6. **Several artifacts.** The trigger creates the release with the latest version of every referenced package. Pushing all artifacts before the manifest, with ever-growing versions and release concurrency 1, makes "latest" equal `VERSION`; `verify-release` rejects any mismatch.
7. **Idempotence.** Octopus never reuses a package version for a second release, so re-runs are harmless.

**Costs:** no build information (the manifest's `commit` replaces `app-commit:`); up to three minutes of latency; nupkg payloads become images; the Codefresh early wake is gone (wake stays in Octopus).

**Enforcement, by capability and admission:**

| Guarantee | Mechanism |
|---|---|
| M1, no cross-app pushes | A per-app ACR token (scope map `apps/<app>/*`: content write, plus metadata write for tag locks), held only in the context `app-<app>-registry`. The platform-owned spec attaches it only to the app's release and preview pipelines. There are no push registry integrations: integrations are account-wide, and any YAML may name one (ADR-IR19). Pushes use the step types or the `push` step's `credentials` [VERIFY]. |
| M2 | A generated per-app Kyverno policy: `<acr>/apps/<app>/*` needs a signature from issuer `https://oidc.codefresh.io` whose subject matches the app's declared release pipelines [VERIFY SAN format]. Enforce in prod; nonprod relies on `verify-release`. |
| M3 | A release exists only through the trigger and `verify-release`. |
| M4 | No reachable credential (section 4). |

This reverses the earlier shared-token recommendation: with a shared `apps/*` token, app A could push a higher `apps/B/manifests/…` tag and create B's release.

## 4. Zero platform secrets in app pipelines: confirmation

With manifest plus trigger, an app pipeline reaches no platform secret. Each reachable item is either app-owned or the platform removes it:

| Reachable item | Verdict |
|---|---|
| Octopus key | None; the handoff is keyless. The context `workorders-octopus` is deleted. |
| Contexts | Only `app-<app>-registry` (the app's own token, confined to `apps/<app>/*`) and the optional app-owned `app-<app>-ci`, for example the app's own AI key. The shared `workorders-ci` and `platform-ai` go away; the CI database password is generated per build (`openssl rand`, `cf_export --mask`). |
| Registry integration | `acr-platform-pull`, pull-only on `platform/*` toolchain images; injection into steps is [VERIFY]. |
| Git integration | **Gap.** `github-aisf-sample-apps` is an org-wide read/write PAT (§5.2). If Codefresh leaves it in the clone's remote URL, app YAML can read it [VERIFY]. Fix: a per-app, read-only Git integration (a fine-grained PAT or GitHub App installation on that app repo only) for app pipelines' triggers and clones. The org PAT stays with platform pipelines. |
| Runtime | No cloud identity. |
| Codefresh permissions | Students change YAML through Git only; attaching contexts and editing specs is platform-only [VERIFY ABAC or team permissions]. |

With the Git gap closed: zero platform secrets in any app context or pipeline.

## 5. Where app pipeline YAML lives

| Location | When |
|---|---|
| **Default for new apps: the app repo's `.codefresh/`**, specs loading the release YAML from the default branch | Apps are app-owned and student-edited. With zero platform secrets, the app repo is safe, students need no access to the private platform repo, and pipelines travel with the code |
| Platform repo `codefresh/apps/<app>/` | App #1 now (ADR-D18: its repo stays untouched), and apps whose repo must stay pristine, such as upstream mirrors |

Specs, descriptors, tokens and signer policies always stay in the platform repo. For app-repo YAML, `platform-env/app-lint` posts `platform/handshake-lint`. The same default is recommended for Octopus config-as-code.

## 6. Boundary lint: the handshake only

The lint drops step-name and structure checks. It checks:
- **Deny list in app YAML:**
  - step types `deploy`, `approval`, `helm`, `launch-composition`;
  - `kubectl`, `helm install|upgrade`, `argocd`, `az`;
  - `git push|commit`;
  - Octopus keys or API routes;
  - push registry integrations;
  - artifact paths outside `apps/<app>/`.
- **Spec invariants** (`render --check`):
  - triggers target the app's repos;
  - the runtime is `<cf-runtime>`;
  - contexts are a subset of `app-<app>-*`;
  - only declared release pipelines hold `app-<app>-registry`;
  - no platform context appears.

Signatures, SBOMs and manifest order are verified by `verify-release` and Kyverno, not by lint.

## 7. Generator and registration

`apps/<app>.yaml` declares:
- `name`, `repos`;
- the Codefresh projects (1..n), each with its pipelines, and which of them are release pipelines and which Octopus project each release feeds;
- `yamlLocation`, `ciProfile`, `preview`.

Tools:
- `scripts/apps/render` validates the descriptors against `apps/schema.json` and writes the specs, the Kyverno inputs and the token list. `--check` runs in `env-checks`.
- `scripts/apps/scaffold` performs the one-time copy.
- `codefresh/register.sh --preview|--full [--app]` (from `register-preview.sh`) creates or replaces by name, keeping IDs and signer subjects; it prunes only with `--prune` and never handles secrets. Terraform `for_each` creates the tokens.

## 8. Class-scale concurrency

App #1's full build needs roughly 12–16 GiB [VERIFY]. Thirty students at once exceed the account's concurrent-build limit [VERIFY value] and the external runner; excess builds queue.

Controls:
- `ciProfile` sets `runtimeEnvironment` cpu, memory and `dindStorage` [VERIFY plan support]; starters default to sequential lean gates.
- `terminationPolicy branch/onCreate` and `branchConcurrency: 1` on CI; `concurrency: 1` on release pipelines.
- Caches are capped; the runner owner autoscales from zero.

## 9. Migration of live Codefresh objects

| Object | Action |
|---|---|
| Project `workorders`; `workorders/ci`, `/release`, `/preview` | Keep. Their YAML moves to `codefresh/apps/workorders/`; the specs are replaced in place, so IDs stay; the handoff switches to the manifest |
| `workorders/ci-image` | Recreate as `platform-env/ci-image-dotnet`, then delete |
| `platform-env`, `platform-env/env-checks` | Keep; add `render --check` |
| Context `workorders-octopus` | Delete once app #1's trigger works. `platform-octopus` is never created in Codefresh |
| (new) `app-workorders-registry`, per-app Git integration | Create |

## 10. Naming contracts

| Object | Name |
|---|---|
| Projects | `<app>` or `<app>-<part>`; release pipelines declared in the descriptor |
| Artifacts | `<acr>/apps/<app>/<component>:<VERSION>`, `<acr>/apps/<app>/charts/<chart>:<VERSION>` |
| Manifests | `<acr>/apps/<app>/manifests/<octopus-project>:<VERSION>` |
| Previews | `<acr>/apps/<app>/preview/<component>:pr-<n>-<sha>` |
| Contexts | `app-<app>-registry`, `app-<app>-ci` |
| Integrations | `acr-platform-pull`; `github-app-<app>` (per-app Git) |
| Octopus | Feed `acr-apps`, trigger `release-manifest`, step template `verify-release` |
| Paths | `codefresh/templates/<starter>/`; `codefresh/apps/<app>/specs/` |
| Statuses | `codefresh/*`, `platform/handshake-lint` |

## 11. Decisions for the chief architect

1. Adopt manifest plus trigger (reversing TB11) and its costs.
2. Per-app ACR tokens and read-only Git integrations; token limits [VERIFY].
3. The student permission model in Codefresh [VERIFY ABAC].
4. YAML default: the app repo for new apps.
5. Admin-published, version-pinned step types.
6. One runtime: release builds skip the layer cache.
7. The Kyverno SAN format [VERIFY].
