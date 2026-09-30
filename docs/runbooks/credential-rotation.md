# Credential rotation

Rotation of every stored credential of the platform: the provisioner secret, the conformance principal's secret, the
Codefresh runner token, the shared ACR tokens, the GitHub PAT, the Octopus Space Manager key, the database passwords,
the Argo CD token and the app keys. Federated identities (the Octopus OIDC accounts, workload identity, keyless image
signing) have no stored secret and are not rotated.

Contracts: §7.0 (identities, registry tokens, Codefresh contexts, vault keys), ADR-IR34 (decisions 3, 5, 8, 16, 24,
25; P1-05), ADR-IR32 (one Octopus key), ADR-IR33 (sleep and wake), ADR-IR15 and R11 (the Argo CD repository
credential), R23 (90-day rotation).

![Level 3: credentials outside Azure and where they are held](../../design/diagrams/c4-3-identities-b.png)

*Level 3, the credentials outside Azure: where each is held and what it reaches. One Space Manager key sits in the Codefresh context `platform-octopus`, in the library set `Platform Automation`, in the step-scoped `Platform.OctopusApiKey`, and in both gateways through the platform vault. One org PAT backs the Octopus Git credential, the Codefresh Git integration, the `platform-conformance` context and the interim Argo CD repository credential (until R11). The Codefresh Git integration moves to a GitHub App (section 2a); the diagram is re-rendered when PlantUML is available. Five repository-scoped ACR tokens live only in Codefresh integrations and contexts.*

![Level 3: app secrets from vault to pod](../../design/diagrams/c4-3-secrets-a.png)

*Level 3, app secrets. apps-apply runs `terraform/apps/tier` as `id-platform-lifecycle-<tier>` and writes the generated SQL passwords and app keys into `kv-<app>-<e>-<hash4>` as write-only values; platform-operators replace the stand-ins; rotate-db-passwords rotates `<app>_migrator`, `<app>_app` and `sa`. ESO reads the vault only through the ClusterSecretStore `<app>-<env>` as `id-eso-platform-<tier>` (workload identity federation) and syncs, hourly, the Secrets used by `db`, `db-init`, `db-migrate` and the workloads; platform-backup gets its own copy of the sa password. The optional tdd step Read deployment secrets reads the same vault as `id-<app>-<env>-deploy`; store conditions refuse other apps' namespaces, and `app-<app>` denies every SecretStore kind.*

![Level 3: platform and pipeline secrets](../../design/diagrams/c4-3-secrets-b.png)

*Level 3, platform and pipeline secrets. The platform vault `<kv-platform-<tier>>`, seeded by platform-operators after env-apply, holds the repository credential and the gateway's two tokens; ESO syncs them through the ClusterSecretStore `platform-keyvault`, which admits only argocd and octopus-argocd-gateway; `terraform/tier` seeds `argocd-repo-creds` once so the first sync can read the repository. Pipeline secrets stay in their tools (names only here): Codefresh secret contexts and registry integrations, Octopus sensitive variables, the stored Git credential for pin commits. One Space Manager key sits in four places (ADR-IR32), an accepted residual risk (decision 15).*

## Roles

| Role | Who | Rights |
|---|---|---|
| Rotator | Group `platform-operators` (the user; ADR-IR34 decision 25) | Key Vault Secrets Officer on `rg-platform-<tier>-aks` and `rg-platform-<tier>-apps` of both tiers; AKS RBAC Cluster Admin on the three cluster groups; Space Manager in `<octopus-space>`; Codefresh account admin |
| Provisioner owner | The user, who holds the provisioner's secret | Application credentials of `sp-automation-mvp-sub` and of `sp-platform-conformance` (owned by the provisioner) |
| Org owner of `clearmeasure-aisf-sample-apps` | GitHub | Fine-grained tokens, GitHub Apps |

Under the single-operator model (§13) the user holds every role; a rotation still follows create, switch, verify,
revoke, and the change record names what was rotated.

## Rules for every rotation

- Create the new credential, switch every consumer, verify, then revoke the old one. Never revoke first.
- Secret values never go into tickets, chat, shell history or command arguments. Write Key Vault secrets from a file
  that is deleted afterwards:

  ```bash
  az keyvault secret set --vault-name <vault> --name <secret-name> --file ./value.txt --encoding utf-8
  shred -u ./value.txt
  ```

- Expiry of every new credential: 90 days or less.
- Terraform never reverts a rotated secret: `terraform/apps/tier` writes every generated value once, as a write-only
  value (`value_wo_version = 1`), and writes stand-ins that operators replace.
- A consumer inside a cluster is verified only while its tier is awake: run `env-wake` first and keep the tier awake
  until the verification ends (`sleep-and-wake.md`). A Secret rotated during a sleep changes only after the next wake.

## Schedule

| Credential | Stored in | Cadence | Section |
|---|---|---|---|
| Provisioner `sp-automation-mvp-sub` client secret | The user's operator sessions only (`terraform/foundation`, `terraform/build`, `terraform/apps/grants`). Octopus account `Azure Runtime Provisioner` is used by no project (ADR-IR34 decision 3) | 90 days | [1](#1-provisioner-and-conformance-secrets) |
| `sp-platform-conformance` client secret | Codefresh context `platform-conformance` (`AZURE_CLIENT_SECRET`) | 90 days | [1](#1-provisioner-and-conformance-secrets) |
| GitHub fine-grained PAT, org `clearmeasure-aisf-sample-apps` | Octopus Git credential `GitHub clearmeasure-aisf-sample-apps` (the pin commits of the Argo CD image-tag step; the project Git settings use the GitHub App connection, [2b](#2b-octopus-git-access-on-the-github-app-connection)); contexts `github-aisf-sample-apps-token` and `platform-conformance` (`GITHUB_TOKEN`); interim Argo CD repository credential `argocd-repo-read-credential` in both platform vaults and `ArgoCD.RepoReadCredential` (until R11); Octopus variable set `GitHub AISF Sample Apps` (stored, included nowhere) | 90 days | [2](#2-github-pat-and-the-argo-cd-repository-credential) |
| Codefresh Git integration `github-aisf-sample-apps` (GitHub App, no PAT) | Codefresh Account settings > Integrations > Git; the App installation on the org. No stored secret | Not rotated; the one-time owner-only switch | [2a](#2a-codefresh-git-integration-on-the-github-app) |
| Shared ACR tokens `cf-apps-release`, `cf-apps-preview`, `cf-platform-ci`, `cf-platform-pull`, `cf-platform-retention` | Codefresh registry integrations `acr-apps-release`, `acr-apps-preview`, `acr-platform-ci`, `acr-platform-pull`; contexts `platform-registry` (`ACR_TOKEN_PASSWORD` of `cf-apps-release`) and `platform-registry-retention` | 90 days (token expiry) | [3](#3-shared-acr-tokens) |
| Database passwords `db-sa-password`, `db-migrator-password`, `db-app-password` | App vaults `kv-<app>-<e>-<hash4>`; SQL logins `sa`, `<app>_migrator`, `<app>_app` in the app's database pod | Monthly: runbook `rotate-db-passwords` (run by hand; no trigger), all three logins, `sa` last | [4](#4-database-passwords) |
| Argo CD account `octopus` API token | `argocd-octopus-gateway-token` in `<kv-platform-<tier>>` | 90 days | [5](#5-argo-cd-token) |
| Argo CD local UI account `jeffrey` password (interim until SSO) | `argocd-user-jeffrey-password`, `-bcrypt`, `-mtime` in `<kv-platform-<tier>>` (one password per tier) | 90 days, on any suspicion | [10](#10-argo-cd-ui-account-jeffrey) |
| Octopus API key of `AISF-Service-Account` (Space Manager; the only Octopus credential, ADR-IR32) | Codefresh context `platform-octopus` (`OCTOPUS_API_KEY`); `PlatformWake.OctopusApiKey` in library set `Platform Automation` and the step-scoped key of `platform-infrastructure` (both from `TF_VAR_platform_octopus_api_key`, `octopus/terraform`); `octopus-gateway-registration-token` in both platform vaults | 90 days, on any suspicion | [6](#6-octopus-api-key) |
| Codefresh runner token | Secret in namespace `codefresh` of `aks-platform-build`, referenced by `global.codefreshTokenSecretKeyRef` (`codefresh/runner/values.yaml`) | 90 days, and when the runtime is re-registered | [7](#7-codefresh-runner-token) |
| Codefresh API key of the conformance runs (only when `CF_API_KEY` is unavailable, Q49) | Context `platform-conformance` (`CODEFRESH_API_KEY`) | 90 days | [7](#7-codefresh-runner-token) |
| Octopus worker registration token | Octopus sensitive variable `Octopus.WorkerRegistrationToken` | Per worker install; never reused | [8](#8-other-platform-secrets) |
| Statuses-only GitHub App private key (R16) | Octopus sensitive variable `GitHub.StatusAppPrivateKey` (app #1's project) | Yearly | [8](#8-other-platform-secrets) |
| App keys of a descriptor's `secrets[]` (for `workorders`: `ai-openai-apikey`, `api-validation-key`) | The app's vaults | 90 days; once after the first `apps-apply` for keys without `generate` | [9](#9-app-keys) |

## Preconditions

- A change record `<change-id>`; for prod, outside `prod-weekend-freeze`.
- The rotator knows every consumer of the credential (Schedule table). A consumer missing from the table is a
  finding for the security owners.
- No release build, deployment or runbook of the affected consumers is running.

## Steps

### 1. Provisioner and conformance secrets

The provisioner is operator-run only; nothing automated holds its secret.

1. Add a new client secret to the provisioner's app registration (expiry 90 days or less), keeping the old one.
2. Use it in the next operator session: `az login --service-principal` with the new secret, then
   `terraform -chdir=terraform/foundation plan`, which must show no changes.
3. Delete the old client secret.

`sp-platform-conformance` (owned by the provisioner):

1. As the provisioner, create a new client secret for the app registration with a 90-day expiry, straight to a file:

   ```bash
   az ad app credential reset --id <client-id-of-sp-platform-conformance> --append --years 0.25 \
     --display-name "conformance-$(date +%Y%m%d)" --query password --output tsv > ./value.txt
   ```

2. Update `AZURE_CLIENT_SECRET` in Codefresh context `platform-conformance` from the file, then `shred -u ./value.txt`.
3. Verify: run `platform-env/conformance` with `TEST_FILTER=TestCategory=Live&FullyQualifiedName~Azure.ClusterAuthTests`;
   it must pass, not report Inconclusive.
4. Delete the old credential: `az ad app credential delete --id <client-id-of-sp-platform-conformance> --key-id <old-key-id>`.

### 2. GitHub PAT and the Argo CD repository credential

The environment repository is public (#47), so a clone needs no credential and the Argo CD repository credential below
is optional (it avoids unauthenticated rate limits). The PAT itself stays a live secret for Octopus and Codefresh: a
PAT that was ever committed to this repository must be treated as leaked and rotated here, whatever its history scan
says (`docs/owner/public-repo-checklist.md`).

1. The org owner creates a fine-grained token with the same permissions and a 90-day expiry (R3: restrict it to the
   environment repository and `<sandbox-app-repo>`, or move to a GitHub App).
2. Update, in this order:
   - Octopus Git credential `GitHub clearmeasure-aisf-sample-apps`. It commits the pins of the Argo CD image-tag step;
     the version-control connection of `platform-infrastructure`, `platform-wake` and every app project uses the GitHub
     App connection and needs no rotation (see [2b](#2b-octopus-git-access-on-the-github-app-connection)).
   - Not the Codefresh Git integration `github-aisf-sample-apps`: it is a GitHub App and holds no PAT (see
     [2a](#2a-codefresh-git-integration-on-the-github-app)).
   - `GITHUB_TOKEN` in contexts `platform-conformance` and `github-aisf-sample-apps-token`.
   - Until R11, the interim Argo CD credential: write `argocd-repo-read-credential` (the JSON object of
     `argocd/clusters/<tier>/platform-secrets.yaml`) into `<kv-platform-nonprod>` and `<kv-platform-prod>`, and the
     same JSON into `ArgoCD.RepoReadCredential` (project `platform-infrastructure`). With each tier awake, force-sync
     ExternalSecret `argocd-repo-creds` in namespace `argocd` and confirm Argo CD shows the repository connected:

     ```bash
     kubectl annotate externalsecret -n argocd argocd-repo-creds force-sync=$(date +%s) --overwrite
     ```

3. Run one deployment to `tdd`: the image-tag step must commit the pin.
4. The org owner revokes the old token.

### 2a. Codefresh Git integration on the GitHub App

The Codefresh Git integration `github-aisf-sample-apps` is the GitHub App "Codefresh Github App", not the PAT (issue
#46). It is declared in `codefresh/platform/integrations.yaml` (`gitIntegrations:`, kind `codefresh-github-app`, no
value) and only verified by `codefresh/register.ps1 --full`, which reports it as pending, ok or a WARN when it is still a
token integration; it is never created, replaced or deleted by a script. The name stays because every clone step
(`git:`), trigger and specTemplate (`context:`) and the commit statuses name it; the repositories are public, so an
anonymous clone would work, but they still go through the integration. No pipeline YAML changes. Owner-only sequence:

1. Install the Codefresh GitHub App on org `clearmeasure-aisf-sample-apps` for the environment repository and
   `20260923-001` (webhooks and statuses need both).
2. Keep the PAT valid. In Codefresh (Account settings > Integrations > Git), rename or delete the old token
   integration `github-aisf-sample-apps`, then create the App integration under the same name in one sitting: until it
   exists every pipeline and trigger fails.
3. Prove it: push an empty branch to the environment repository and confirm `codefresh/env-checks` starts and goes green;
   `pwsh codefresh/register.ps1 --full` then reports `git integration github-aisf-sample-apps: ok (GitHub App)` (the
   expected `git.github-app` type is [VERIFY]: a different type is a WARN naming the actual one).
4. Only then remove the PAT's use in Codefresh.

The PAT stays for the Octopus Git credential, `GITHUB_TOKEN` in contexts `platform-conformance` and
`github-aisf-sample-apps-token`, and the interim Argo CD repository credential until R11 (section 2). Nothing here is
done by any script.

### 2b. Octopus Git access on the GitHub App connection

Since #42 the four config-as-code projects (`workorders`, `sandbox`, `platform-infrastructure`, `platform-wake`) reach the
environment repository through the Octopus GitHub App connection, not the stored PAT credential. Nothing to rotate: the
App holds no PAT and Octopus mints short-lived tokens. `octopus/terraform` sets it (provider 1.20.0 block
`git_github_app_persistence_settings`, variable `octopus_github_app_connection_id`); the owner-only steps, the
`main-protection` bypass actor and the rollback are in
[octopus-github-app-git.md](octopus-github-app-git.md). The stored credential remains only for the Argo CD image-tag
step's pin commits (issue #58 retires it once Octopus supports the connection there).

### 3. Shared ACR tokens

Each token has two password slots, so a rotation never interrupts pushes. The tokens are created by CLI from the
foundation's scope maps (P1-05), never in Terraform state.

1. Generate the unused slot with an expiry (the command prints the password once; run it in a private terminal and
   write it straight to a file):

   ```bash
   az acr token credential generate --registry <acr-name> --name cf-apps-release --password2 \
     --expiration-in-days 90 --query "passwords[0].value" --output tsv > ./value.txt
   ```

2. Update every consumer of that token with the new slot, in one change:

   | Token | Consumers |
   |---|---|
   | `cf-apps-release` | Registry integration `acr-apps-release`; context `platform-registry` (`ACR_TOKEN_PASSWORD`) |
   | `cf-apps-preview` | Registry integration `acr-apps-preview` |
   | `cf-platform-ci` | Registry integration `acr-platform-ci` |
   | `cf-platform-pull` | Registry integration `acr-platform-pull` |
   | `cf-platform-retention` | Context `platform-registry-retention` |

3. Verify: the next release build of any app pushes, signs and locks its tags (`cf-apps-release`, CAP-CF-006 and
   CAP-CF-007); `platform-env/ci-image-dotnet` pushes (`cf-platform-ci`); any CI build pulls its step image
   (`cf-platform-pull`); `platform-env/registry-retention` with `RETENTION_DRY_RUN=true` lists
   (`cf-platform-retention`, CAP-CF-010).
4. Delete the old slot, then `shred -u ./value.txt`:

   ```bash
   az acr token credential delete --registry <acr-name> --name cf-apps-release --password1
   ```

5. Alternate the slots on each rotation.

### 4. Database passwords

Runbook `rotate-db-passwords` (project `platform-infrastructure`, prompted `App.Name`, in `infra-nonprod` or
`infra-prod`) rotates the three logins of one app in each app-environment of the tier, as the tier's lifecycle identity
through kubectl (CAP-AZ-011 proves it on the sandbox every week):

1. It checks that `sa` logs in to pod `db-0` with the password of Secret `db-sa` mounted in the pod (what its probes
   use); otherwise nothing is rotated.
2. `<app>_migrator`, then `<app>_app`: the new password goes into the vault key first, then `ALTER LOGIN` runs in
   `db-0` as `sa`, the statement on standard input. When `ALTER LOGIN` fails, the previous password goes back into the
   vault, so the vault always holds the password that works.
3. It force-syncs ExternalSecrets `db-migrator` and `db-app`, then restarts the app's own Deployments by name with
   `kubectl rollout restart` in their namespace and waits for each rollout (10 minutes). The names come from
   `status.resources` of the app's workload Applications (labels `platform/app`, `environment`,
   `platform/role=workload`); no other Deployment is restarted. The restart does not go through Argo CD: its restart
   action needs an Argo CD account allowed to run it and API access from the workers, which the platform does not have
   (account `octopus` is read-only, section 5). Argo CD keeps the restart: the annotation
   `kubectl.kubernetes.io/restartedAt` is not in Git, so the Application stays Synced and self-heal leaves it (Argo
   CD's own restart action sets the same annotation).
4. `sa` last, because every change above runs as `sa`: the same order with `db-sa-password`, then a force-sync of every
   ExternalSecret that reads it: `db-sa` (mounted by the database StatefulSet for its probes and `db-init`) and, in
   uat and prod, `db-sa-<app>-<env>` in `platform-backup` (backup Jobs). An annotation on pod `db-0` makes the
   kubelet remount `db-sa` at once instead of at its next sync. The run fails unless `sa` then logs in with the
   mounted password, as the probes do, within 3 minutes.

Manual procedure when the runbook fails, as `platform-operators` with the tier awake, one login at a time and `sa`
last:

1. Generate a password of at least 32 characters (upper, lower, digits, and only `-`, `_` or `.` as symbols) into
   `./value.txt`.
2. Change the login without echoing the password: copy a script into the database pod and run it with `sqlcmd -i`
   (for `workorders` in `tdd`, pod `db-0` of namespace `workorders-tdd`):
   `ALTER LOGIN [workorders_app] WITH PASSWORD = '<generated>';`
   For `sa`, use the current `sa` password from Secret `db-sa`.
3. Write the vault key (`db-app-password`, `db-migrator-password` or `db-sa-password`) from the file (rules above).
4. Force-sync every ExternalSecret that reads the key (`db-migrator`, `db-app` or `db-sa` in `<app>-<env>`; for
   `db-sa-password` also `db-sa-<app>-<env>` in `platform-backup`). For `<app>_migrator` and `<app>_app`, restart the
   app's own Deployments by name as the runbook does, each with
   `kubectl -n <app>-<env> rollout restart deployment.apps/<name>`; the names are in `status.resources` of
   `kubectl get applications.argoproj.io -n argocd -l platform/app=<app>,environment=<env>,platform/role=workload -o json`.
   For `sa`, annotate pod `db-0` so that it remounts `db-sa`.
5. Verify: the app's health endpoint, and for the migrator login the next deployment's PreSync Job `db-migrate`.

A restored backup carries the passwords of its time: run the rotation again after a restore
(`database-backup-and-restore.md`).

### 5. Argo CD token

Account `octopus` is read-only (`applications get`, `logs get`, `clusters get`). Token IDs carry the date.

1. With the tier awake, sign in to `argocd-<tier>` through SSO as a platform owner.
2. Create the new token with a 90-day expiry, written straight to a file:

   ```bash
   argocd account generate-token --account octopus --id octopus-$(date +%Y%m%d) --expires-in 2160h > ./value.txt
   ```

3. Write `argocd-octopus-gateway-token` into `<kv-platform-<tier>>` from the file.
4. Nonprod only: keep the signing key and the token list of `octopus` in the vault too, so a rebuild keeps the token
   valid (ExternalSecret `argocd-secret-persisted` merges them back into `argocd-secret`):

   ```bash
   kubectl -n argocd get secret argocd-secret -o jsonpath='{.data.server\.secretkey}' | base64 -d > ./secretkey.txt
   kubectl -n argocd get secret argocd-secret -o jsonpath='{.data.accounts\.octopus\.tokens}' | base64 -d > ./tokens.txt
   az keyvault secret set --vault-name <kv-platform-nonprod> --name argocd-server-secretkey --file ./secretkey.txt --encoding utf-8
   az keyvault secret set --vault-name <kv-platform-nonprod> --name argocd-octopus-tokens --file ./tokens.txt --encoding utf-8
   shred -u ./secretkey.txt ./tokens.txt ./value.txt
   ```

   Do this on every rotation: the ExternalSecret refreshes hourly and would otherwise put back the old token list.
   After a rebuild the vault's key wins over the new instance's, so every token the vault lists stays valid.
5. Force-sync ExternalSecret `argocd-octopus-token` in namespace `octopus-argocd-gateway` (nonprod: also
   `argocd-secret-persisted` in `argocd`), and confirm in Octopus that instance `argocd-<tier>` reports healthy and its
   Applications show live status.
6. Delete the old token: `argocd account delete-token --account octopus <old-token-id>`.

**Prod after a lost cluster.** Prod has no destroy runbook, so its gateway registration and token outlive nothing. If
`aks-platform-prod` is ever recreated (disaster recovery), its new Argo CD signs with a new key and its gateway cannot
register under the old name. Before the recreated cluster's first sync, a platform owner deletes the stale registration
(Octopus, Infrastructure, Argo CD Instances, `argocd-prod`), then mints and seeds the token with steps 1 to 3 once the
tier is up, force-syncs `argocd-octopus-token`, and runs the instance's health check (Octopus, Argo CD Instances,
`argocd-prod`, Check health). Nonprod does all of this itself: env-apply removes the stale registration, and
`argocd-secret-persisted` keeps the signing key and token list across a rebuild.

### 6. Octopus API key

One key has several consumers, so all of them change in one window, outside `prod-weekend-freeze`, with no release
build, deployment or runbook running in the space.

1. Sign in as `AISF-Service-Account` and create a replacement API key with an expiry of 90 days or less.
2. Update `OCTOPUS_API_KEY` in Codefresh context `platform-octopus`. Re-run the last release build of any app: its
   Octopus step reports the existing release.
3. Apply `octopus/terraform` with `TF_VAR_platform_octopus_api_key` set to the new key: it updates
   `PlatformWake.OctopusApiKey` (library set `Platform Automation`) and the step-scoped key of
   `platform-infrastructure`. Run `env-plan` in `infra-nonprod` and in `infra-prod`: their `wake-environment` steps
   call the API with the new key. Until this step is done, every deployment (through `platform-wake`), every keyed
   wake and `env-sleep` fail.
4. Write the key as `octopus-gateway-registration-token` into `<kv-platform-nonprod>` and `<kv-platform-prod>`,
   force-sync ExternalSecret `octopus-gateway-registration` in namespace `octopus-argocd-gateway` of each awake tier,
   and confirm `argocd-nonprod` and `argocd-prod` are still healthy in Octopus. Whether the gateway uses the key only at
   registration is [VERIFY] (Q13).
5. Revoke the old key in Octopus. Check the audit log for any use of the old key after the switch; any use is an
   incident.
6. Re-run the `octopus/terraform` plan with the new key; it must show no changes.

### 7. Codefresh runner token

The runtime `<cf-runtime>` (`aks-platform-build/codefresh`) authenticates with a Codefresh API key stored as a Secret
in namespace `codefresh` of `aks-platform-build`; `codefresh/runner/values.yaml` only references it
(`global.codefreshTokenSecretKeyRef`). No Git file holds the value.

1. In Codefresh, create a new API key with the minimal scopes listed in the header of `codefresh/runner/values.yaml`
   and write it to `./value.txt`.
2. As `platform-operators` (AKS RBAC Cluster Admin on `rg-platform-build`), replace Secret `codefresh-token`
   (key `token`, the `global.codefreshTokenSecretKeyRef` of the values) without echoing the value:

   ```bash
   kubectl -n codefresh create secret generic codefresh-token --from-file=token=./value.txt \
     --dry-run=client -o yaml | kubectl -n codefresh apply -f -
   shred -u ./value.txt
   ```

3. Restart the runner agent (`kubectl -n codefresh rollout restart deployment -l app.kubernetes.io/name=cf-runtime`
   [VERIFY the label]).
4. Verify: the agent reports healthy within 5 minutes (CAP-CF-002) and a build succeeds on `<cf-runtime>` (CAP-CF-001).
5. Delete the old API key in Codefresh.

When the conformance pipelines cannot use their own build access (`CF_API_KEY`, Q49), `CODEFRESH_API_KEY` in context
`platform-conformance` follows the same create, switch, verify, delete order.

### 8. Other platform secrets

- **Octopus worker registration token**: used once per install and ends when it expires. To re-register a worker,
  generate a new token, enter it as `Octopus.WorkerRegistrationToken`, and run `env-apply` with a replace of
  `helm_release.octopus_worker["<env>"]`. The token stays out of Terraform state (ephemeral variable, write-only
  argument).
- **Statuses-only GitHub App key** (R16, owner-only; yearly): the App `aisf-octopus-status-reporter` (App ID 5130161,
  installation ID 166359160, `Commit statuses: write`, installed on `20260923-001` only) exists and its IDs are in
  `.octopus/apps/workorders/workorders/variables.ocl`. The private key never enters the repository, a chat or a command
  line.
  - **First enable**: the owner generates a private key in the App settings and stores it as the sensitive variable
    `GitHub.StatusAppPrivateKey` in the app project (Octopus UI), then sets `GitHub.StatusEnabled` to `True`
    (`variables.ocl`, through a pull request). Until then `report-commit-status` skips.
  - **Yearly rotation**: generate a new private key for the App; set it as `GitHub.StatusAppPrivateKey`; the next tdd
    deployment must post `platform/tdd`; then delete the old key in the App settings.

### 9. App keys

`apps-apply` creates each key of a descriptor's `secrets[]` once: a random value when `generate: true`, otherwise the
stand-in `not-set-see-credential-rotation-runbook`. Set real values after the first `apps-apply`, and rotate them:

1. Write the new value from a file into `kv-<app>-<e>-<hash4>` (rules above). Find the vault name in the
   `apps-apply` output `vault_names`, or with
   `az keyvault list -g rg-platform-<tier>-apps --query "[?tags.\"platform-app\"=='<app>' && tags.\"platform-env\"=='<env>'].name"`.
2. Force-sync the app's ExternalSecret, then restart the app's own Deployments by name (section 4, step 4).
3. Verify through the app's own health check (for `workorders`, `/_healthcheck` with the LLM check healthy).
4. Revoke the old value at its issuer (for `ai-openai-apikey`, Azure OpenAI).

### 10. Argo CD UI account jeffrey

Interim local account (docs/argocd-ui-access.md). Each tier has its own password. For each tier:

1. Generate a random password (at least 24 characters) and its bcrypt hash (cost 10), in memory or in files that get
   shredded afterwards (for example `python3 -c "import bcrypt,sys; print(bcrypt.hashpw(sys.stdin.read().strip().encode(), bcrypt.gensalt(10)).decode())"`).
2. Write `argocd-user-jeffrey-password` (plaintext), `argocd-user-jeffrey-password-bcrypt` (hash) and
   `argocd-user-jeffrey-password-mtime` (current UTC time, RFC3339 such as `2026-09-28T22:00:00Z`) into
   `<kv-platform-<tier>>`.
3. Force-sync ExternalSecret `argocd-secret-persisted` in namespace `argocd`
   (`kubectl -n argocd annotate externalsecret argocd-secret-persisted force-sync=$(date +%s) --overwrite`). A newer
   mtime ends existing sessions of `jeffrey`.
4. Sign in at `https://argocd.<apps-domain-<tier>>` from an allow-listed IP with the new password, and give the new
   password to the owner.

To disable the account, set `accounts.jeffrey.enabled: "false"` in `argocd/bootstrap/values-<tier>.yaml`, or remove the
IP from the allow-list (Key Vault secret `argocd-ui-allowlist`, then `scripts/argocd/set-argocd-ui-allowlist.ps1`;
[argocd-ui-access.md](../argocd-ui-access.md#the-allow-list-is-cluster-only)).

## Verification

- Every consumer in the Schedule table works with the new credential (the check named in each section).
- The old credential is revoked or deleted.
- New expiry dates are recorded in the change record.
- The nightly conformance run passes without Inconclusive results caused by credentials.

## Audit evidence

- Key Vault audit events of the rotation (who wrote which secret, never the value), from `log-platform-<tier>`:

  ```kusto
  AzureDiagnostics
  | where ResourceProvider == "MICROSOFT.KEYVAULT" and OperationName == "SecretSet"
  | where TimeGenerated > ago(1d)
  | project TimeGenerated, Resource, id_s, identity_claim_upn_s, CallerIPAddress
  ```

- Entra audit log entries for application credential changes (provisioner, `sp-platform-conformance`).
- The Azure Activity Log for ACR token credential operations.
- The Octopus audit log for account, Git credential and variable changes.
- The GitHub organization audit log for token creation and revocation.
- The Codefresh audit log for context, integration and API-key changes.
