# GitOps starter `raw`

Copied once by `onboarding scaffold <app> --gitops raw` into the app folder `gitops/apps/<app>/`, then owned
by the app (docs/onboarding.md, section 3). This README is not copied. Contract: design §7.0 (Tenant,
Database, Migrations, Pins, Ingress) and ADR-IR34.

## Shape

Plain manifests, one full set per environment and deployable (Argo CD directory source, top level only), for
an app declared as `onboarding new <app> --packaging raw --images web,migrator --database mssql-2022-express`:

| Path | Holds | Writer |
|---|---|---|
| `envs/<env>/<deployable>/web.yaml` | ServiceAccount, Deployment and Service `web`; pin: the `image:` tag | Octopus (tag), pull request (rest) |
| `envs/<env>/<deployable>/secrets.yaml` | ExternalSecret `web-secrets` from store `<app>-<env>` | pull request |
| `envs/<env>/<deployable>/network.yaml` | HTTPRoute `web` on ListenerSet `<namespace>` | pull request |
| `envs/<env>/<deployable>/migrate.yaml` | PreSync Job `db-migrate`; pin: the `image:` tag | Octopus (tag), pull request (rest) |
| `envs/<env>/db/` | Database (Application `<app>-db-<env>`), written by the kit for an app with a database | pull request |

Tokens: `<app>`, `<env>`, `<deployable>` and `<namespace>`. Path segments use `__ENV__` and `__DEPLOYABLE__`:
angle brackets are not valid in Windows file names. No `kustomization.yaml` or `Chart.yaml` may appear in
these folders, or Argo CD renders them as Kustomize or Helm.

## Image contract

- `web`: HTTP on 8080, `GET /healthz`, non-root, read-only root file system (writable `/tmp`). Reads
  `APP_ENVIRONMENT`, `APPLICATIONINSIGHTS_CONNECTION_STRING` and `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`,
  `DB_PASSWORD` (Secret `db-app`).
- `migrator`: applies the pending migrations and exits 0, idempotent and forward-only. Reads the same `DB_*`
  names (Secret `db-migrator`), retries its login until `MIGRATION_DB_READY_TIMEOUT_SECONDS`, and trusts the
  server certificate through `SSL_CERT_FILE` (`platform-internal-ca`) (CAP-GIT-009, CAP-GIT-010).

## Adapting

- Pins: every image reference under the registry placeholder is an image of the deployable, with a tag and no
  digest; `onboarding check <app>` enforces it. Octopus rewrites only the tags.
- No database: delete `migrate.yaml` and the `DB_*` variables of `web.yaml` in every environment, and the
  `migrator` image from the descriptor.
- App secrets: each `secrets[]` entry of the descriptor is a vault key; map it in `web-secrets`.
- Frozen apps: the tenant chart cannot scale raw manifests; set `replicas: 0` by pull request.
- Never here: namespaces, quotas, limit ranges, NetworkPolicies, secret stores, ListenerSets, Kyverno or
  Argo CD kinds (tenant chart; AppProject `app-<app>` denies them).
