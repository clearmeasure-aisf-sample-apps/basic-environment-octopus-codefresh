# Demo environment: reference

The design behind `SKILL.md`. Read only the section a situation needs.

## Layers and who creates them

"Operator" below is the operator identity of `operator-identity.md` (a Linux account with a GitHub machine user, the service principal `cm-ai-ops` and the Octopus service account `ai-ops`), or a person's own logins.

| Layer | What | Created by | Credential |
|---|---|---|---|
| 0 Seed | Resource groups, ACR, Terraform state account, every identity, its federated credentials and grants (`templates/system/bootstrap/seed.bicep`) | `new-demo-seed.ps1`, once | Operator, subscription Owner |
| 0b Octopus foothold | Space, service account, OIDC identities | `new-octopus-foothold.ps1`, once | Octopus administrator key |
| 1 Octopus configuration | Environments, lifecycle, Azure OIDC accounts, feeds, projects, processes, variables (`octopus/` Terraform, read from `system.json`) | Job `octopus-apply` of workflow `system`, every merge | GitHub OIDC → Octopus service account; `id-<slug>-octopus-config` for the state |
| 2 Environments | `infra/main.bicep` per environment, as deployment stack `stack-<slug>-<env>` | Octopus project `<slug>-system`, a release per merge | `id-<slug>-deploy-<tier>` (Octopus OIDC) |
| 3 App | Pin, migrate, update, verify | Octopus project `<slug>-<deployable>`, a release per green master build | Same deploy identity; GitHub token for the pin |

Only layers 0 and 0b run from the operator's machine. They exist because no identity can create itself. The rest comes from Git on the first push, and the first run is the same pipeline every later change goes through.

## Repositories

| Repository | Contents | Who writes |
|---|---|---|
| `<slug>-system` | `system.json`, `environments/<env>/versions.json`, `infra/`, `octopus/`, `scripts/`, `bootstrap/`, workflows `system`, `env-checks`, `drift` | People by pull request (`env-checks` required); Octopus writes only `versions.json`, straight to `main` |
| `<slug>-workorders` | The bootcamp app (snapshot, no history) with `build.yml` (CI) and `release.yml` (release candidate) | People by pull request (`Build result` required) |

**Snapshot, not fork.** An org can hold only one fork of an upstream, a fork's pull requests default to the upstream, and Actions starts disabled on a fork. The first commit records `bootcamp@<sha>`.

**Changes to the bootcamp's delivery files**, made by `new-app-repository.ps1`:
- jobs `docker-build-image-for-churchbulletin-ui` and `publish-octopus` removed (they used `AZURE_CREDENTIALS` and `OCTO_API_KEY`);
- step "Mark PR as ready for review" removed (it used `COPILOT_PAT`);
- `deploy.yml` and the bootcamp's `.octopus/` removed;
- `release.yml` added.

The CI jobs, `Build result`, and the version scheme `MAJOR.MINOR.<run>` stay as they are.

## Pipelines

| When | What runs | Result |
|---|---|---|
| Pull request to system | `env-checks`: `test-system.ps1`, Bicep build, `terraform fmt` and `validate`; `preview`: what-if per environment (doesn't block) | Required check, plus a preview in the job summary |
| Merge to system `main` (not a pin) | `system`: `octopus-apply`, then `system-release` (package `<slug>-system.1.0.<run>`, release, deployment to the first environment) | Octopus configured; first environment applied |
| Promotion of `<slug>-system` | "Apply environment", then "Verify environment" | Stack applied with deny settings; URLs verified |
| Push to app `master` | `Build`, then `Release`: image `<acr>/<slug>/<deployable>:<version>`, database package, release `<version>` of `<slug>-<deployable>` | First environment deploys automatically |
| Deployment of `<slug>-<deployable>` | "Pin version", "Migrate database", "Update deployable", "Verify deployable" | `versions.json` on `main` = what runs |
| Nightly | `drift`: what-if of `main` | Red run means an environment differs from Git |

**Two sources in one apply.**
- Templates come from the release package. A promotion applies exactly the commit already tested in the earlier environments.
- Versions come from `main`. They are the current desired state, written by the pin step.

That is why a promoted infrastructure release never reverts an app version.

**Loop guard.** The `system` workflow ignores pushes that change only `environments/*/versions.json`, so a pin commit never starts an infrastructure release.

## Identities (all user-assigned managed identities; no Entra app registrations, no stored Azure secrets)

| Identity | Group | Rights | Federated subject |
|---|---|---|---|
| `id-<slug>-plan` | nonprod | Reader on both groups | `repo:<org>/<slug>-system:environment:azure-read` |
| `id-<slug>-octopus-config` | nonprod | Storage Blob Data Contributor on the state account | `repo:<org>/<slug>-system:environment:octopus` |
| `id-<slug>-acr-push` | nonprod | AcrPush on the registry | `repo:<org>/<app-repo>:environment:release` |
| `id-<slug>-deploy-nonprod` | nonprod | Owner of the nonprod group | `space:<space-slug>:project:<project>:environment:<env>`, for both projects and each nonprod environment |
| `id-<slug>-deploy-prod` | prod | Owner of the prod group | Same, for prod environments |
| `id-<slug>-<env>-app` | the environment's tier | AcrPull (seed); Key Vault Secrets User (stack) | None (runtime only) |

The Octopus issuer is the Octopus URL **without** a trailing slash.

GitHub to Octopus authentication uses the service account's two OIDC identities, matching the same `environment:octopus` and `environment:release` subjects.

**The one stored secret** is `OCTOPUS_GITHUB_TOKEN`: the operator's `gh` token, handed to Octopus as `GitHub.Token`. The pin step uses it, and the main ruleset lets organization owners bypass it. Narrowing it to a GitHub App ("pin writer", Contents write on the system repository only) takes two browser clicks per App, so it is left as an upgrade.

## Naming

Everything derives from the slug, so parallel demos never collide.

| Object | Name |
|---|---|
| Repositories | `<slug>-system`, `<slug>-workorders` (override: `appRepository`) |
| Octopus | Projects `<slug>-system`, `<slug>-<deployable>`; group `<slug>`; lifecycle `<slug>-lifecycle`; accounts `azure-<slug>-<tier>`; service account `<slug>-github` |
| Azure seed | `acr<slug><6>`, `st<slug>tf<6>` |
| Azure per environment | `stack-<slug>-<env>`, `kv<slug><env><5>`, `sql-<slug>-<env>-<5>`, `sqldb-<slug>-<env>`, `cae-<slug>-<env>`, `ca-<slug>-<env>-<deployable>`; telemetry adds `log-…` and `appi-…` |

The `<6>` and `<5>` suffixes come from `uniqueString`, so a re-created demo gets the same names.

## Capabilities

A capability is:
- `infra/modules/<capability>.bicep`;
- one `if (contains(capabilities, '<capability>'))` module in `infra/main.bicep`;
- its name in an environment's `capabilities` in `system.json`.

`test-system.ps1` refuses a capability without a module. The template ships:
- `baseline` (always on): Key Vault, SQL free offer, Container Apps;
- `telemetry`: Log Analytics plus Application Insights. The connection string becomes `APPLICATIONINSIGHTS_CONNECTION_STRING`, which the app's OpenTelemetry export reads.

Add a new capability in two pull requests:
1. The module and the condition. This changes nothing anywhere yet.
2. Turn it on in one environment (`set-demo-capability.ps1`).

Each capability should also add a check to `scripts/verify-environment.ps1`, or a test of its own, that proves its behaviour in nonprod.

## Free tiers and limits

| Resource | Tier | Limit to watch |
|---|---|---|
| Azure SQL | Free offer: serverless General Purpose, auto-pause when the monthly allowance is used | 10 free databases per subscription, all in one region. One per environment, so a fleet of demos runs out quickly |
| Container Apps | Consumption, `minReplicas 0` | The monthly free grant is shared by the whole subscription. The first request after idle has a cold start, which verify tolerates for 10 minutes |
| Container Registry | Basic | Not free: a small daily charge per demo |
| Key Vault, identities, stacks | — | Vault names stay reserved for 7 days after deletion; teardown purges them |
| Deployment stacks | — | At most 5 excluded principals in the deny settings (the template uses 1) |
| GitHub Actions | Standard hosted runners, public repositories | Free minutes. Concurrency per org (20 jobs on the Free plan) limits parallel demos |
| Octopus | Hosted Ubuntu dynamic workers, `worker-tools` container | Task cap of the instance; Platform Hub and license limits for many spaces |

## Known limits of this template version

- **Database password on the command line.** The bootcamp's database tool takes it as a positional argument (`DatabaseOptions`), so `migrate-database.ps1` passes it that way inside the single-use worker container. A "secretless" capability (Entra authentication for SQL and the migrator) removes it; that needs a change in the app repository.
- **The app uses the SQL admin login.** Replace it with a contained user per environment when "secretless" lands.
- **No acceptance tests yet.** The bootcamp's Playwright suite ran from `deploy.yml`, which is removed. Bringing it back as an Octopus step in TDD is a progression step.
- **One deployable.** `system.json` and `octopus/` support several. Adding the Worker means a second app repository, and the seed needs its repository in `appRepositories` (re-run `new-demo-seed.ps1`).

## Verify on the first live run

None of these could be exercised without live services when the template was written. Confirm each one on the first live run, and fix the template when one does not hold:

1. `OctopusDeploy/login@v2` sets `OCTOPUS_ACCESS_TOKEN`, and `push-package-action@v3` and `create-release-action@v3` pick it up together with `OCTOPUS_SPACE` (a space ID).
2. The Octopus Terraform provider (1.20.0) accepts the OIDC access token for every resource in `octopus/`, and a Space Manager service account may create Azure OIDC accounts and the Docker Hub feed.
3. `GET /api/serviceaccounts/{id}/oidcidentities/v1` returns `ExternalId`; it is the `service_account_id` of the login action. The OIDC identities are created without an audience; confirm that Octopus accepts the token the login action requests, or set the audience it expects.
4. A new space on Octopus Cloud has the dynamic pool "Hosted Ubuntu".
5. The image `octopusdeploy/worker-tools:6.6.5-ubuntu.24.04` has Azure CLI 2.61 or later (`az stack`) and installs Bicep on first use.
6. The deny settings (`denyWriteAndDelete`, deploy identity excluded) still allow Container Apps revisions, SQL auto-pause, and `az containerapp update` by the deploy identity.
7. `id-<slug>-plan` with Reader can run `az deployment group what-if`. If not, grant it the `Microsoft.Resources/deployments/whatIf/action` and `validate/action` permissions through a custom role in the seed.
8. The bootcamp's `Build` passes in a fresh repository without its secrets: the AI settings are optional, and Qodana runs without a token.
9. The ruleset bypass for organization owners lets the pin step's contents API commit to `main`.

Added with the operator identity (`operator-identity.md`):

10. `az login --service-principal --password @<file>` reads the secret from the file (Azure CLI's `@file` syntax), so it never appears in an argument.
11. A service principal that owns its own app registration and holds Graph `Application.ReadWrite.OwnedBy` can call `addPassword` and `removePassword` on it (`new-operator-identity.ps1` ends with that test).
12. `systemd-creds encrypt --user` and `decrypt --user` work in a `sudo -iu aiops` shell (the varlink service identifies the caller without a desktop session).
13. An Octopus service account in the team Octopus Managers (System Manager) can create a space, a service account and its OIDC identities, and as a Space Manager of the new space can read deployments and deploy releases.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `octopus-apply`: login fails | OIDC subject mismatch | The job's environment must be `octopus`, and the identity's subject must match exactly (`new-octopus-foothold.ps1` prints it) |
| `octopus-apply`: state 403 | Role assignment still propagating | Re-run the job after a few minutes |
| "Apply environment": `AADSTS70021`, no matching federated identity | Octopus subject differs from the seed's | Compare `space:<space-slug>:project:<project>:environment:<env>` with the identity's credentials. Re-run `new-demo-seed.ps1` after a space or project rename |
| "Apply environment": Key Vault reference error on the container app | Runtime identity's role not applied yet | The script retries. Redeploy the release once if it still fails |
| "Update deployable": container app missing | The environment's system release isn't deployed yet | Promote `<slug>-system` to that environment first |
| SQL free offer refused | 10 free databases already, or a different region | Delete old demos (`remove-demo-environment.ps1 -Delete`), or use the region of the existing free databases |
| Teardown: resource group delete denied | A stack's deny assignment | Delete the stacks first; the script does this, so re-run it |
