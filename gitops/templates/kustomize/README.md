# GitOps starter `kustomize`

Copied once by `onboarding scaffold <app> --gitops kustomize` into the app folder `gitops/apps/<app>/`,
then owned by the app (docs/onboarding.md, section 3). This README is not copied. Contract: design
§7.0 (Tenant, Database, Migrations, Pins, Ingress) and ADR-IR34.

## Shape

One copy per deployable of `apps/<app>.yaml` (default deployable `app`), for an app declared as
`onboarding new <app> --images web,migrator --database mssql-2022-express`:

| Path | Holds | Writer |
|---|---|---|
| `<deployable>/base/` | ServiceAccount, Deployment and Service `web`; ExternalSecret `web-secrets`; HTTPRoute `web` | pull request |
| `<deployable>/database/` | Component: PreSync Job `db-migrate` (image `migrator`) and the `DB_*` variables of `web` | pull request |
| `envs/<env>/<deployable>/kustomization.yaml` | Pins: `images[].newTag` of `web` and `migrator` (Application `<app>-<deployable>-<env>`) | Octopus only |
| `envs/<env>/<deployable>/config/` | Namespace `<namespace>`, settings (`web-config`), store `<app>-<env>`, listener `<namespace>` | pull request |
| `envs/<env>/db/` | Database (Application `<app>-db-<env>`), written by the kit for an app with a database | pull request |

Tokens: `<app>`, `<env>`, `<deployable>` and `<namespace>` (`<app>-<env>`, or `<app>-<part>-<env>`). Path
segments use `__ENV__` and `__DEPLOYABLE__`: angle brackets are not valid in Windows file names.

## Image contract

- `web`: HTTP on 8080, `GET /healthz`, non-root, read-only root file system (writable `/tmp`). Reads
  `APP_ENVIRONMENT`, `APPLICATIONINSIGHTS_CONNECTION_STRING` and `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`,
  `DB_PASSWORD` (Secret `db-app`). In-cluster connections may use `TrustServerCertificate=True` (decision 8).
- `migrator`: applies the pending migrations and exits 0, idempotent and forward-only (expand/contract). Reads
  the same `DB_*` names (Secret `db-migrator`), retries its login until `MIGRATION_DB_READY_TIMEOUT_SECONDS`,
  and trusts the server certificate through `SSL_CERT_FILE` (`platform-internal-ca`). A failure fails the Job,
  the sync and the Octopus deployment; the old pods keep serving (CAP-GIT-009, CAP-GIT-010).

## Adapting

- Other image names: rename them in `base/`, `database/`, the pins and `apps/<app>.yaml` together;
  `onboarding check <app>` compares pins and descriptor.
- No database: delete `components:` from `base/kustomization.yaml`, the `database/` folder, the `migrator`
  pins and the image in the descriptor.
- App secrets: each `secrets[]` entry of the descriptor is a vault key; map it in `web-secrets`.
- Two deployables in one namespace (no `part`): rename the objects of one of them.
- Never here: namespaces, quotas, limit ranges, NetworkPolicies, secret stores, ListenerSets, Kyverno or
  Argo CD kinds. The tenant chart renders them; AppProject `app-<app>` denies them.
- Frozen apps (`status: frozen`): the tenant chart patches every Deployment to zero replicas.
- Verify: `onboarding check <app>` and `kustomize build envs/<env>/<deployable>`.
