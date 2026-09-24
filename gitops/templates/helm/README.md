# GitOps starter `helm`

Copied once by `onboarding scaffold <app> --gitops helm` into the app folder `gitops/apps/<app>/`, then owned
by the app (docs/onboarding.md, section 3). This README is not copied. Contract: design §7.0 (Tenant,
Database, Migrations, Pins, Ingress) and ADR-IR34 decision 7 (charts vendored in Git; Octopus never promotes
chart versions).

## Shape

One copy per deployable of `apps/<app>.yaml` (default deployable `app`), for an app declared as
`onboarding new <app> --packaging helm --images web,migrator --database mssql-2022-express`. That command
writes `helm.chart: charts/app` and the two `imageReplacePaths` the values below use:
`{{ .Values.migrator.repository }}:{{ .Values.migrator.tag }}` and `{{ .Values.web.repository }}:{{ .Values.web.tag }}`.

| Path | Holds | Writer |
|---|---|---|
| `charts/<deployable>/` | The chart: ServiceAccount, Deployment and Service `web`; ExternalSecret `web-secrets`; HTTPRoute `web`; PreSync Job `db-migrate` when `database.enabled` | pull request |
| `envs/<env>/<deployable>/values.yaml` | The only values file of Application `<app>-<deployable>-<env>`: pins `web.tag` and `migrator.tag` (Octopus only), and the settings (pull request) | both |
| `envs/<env>/db/` | Database (Application `<app>-db-<env>`), written by the kit for an app with a database | pull request |

Tokens: `<app>`, `<env>`, `<deployable>`. Path segments use `__ENV__` and `__DEPLOYABLE__`: angle brackets are
not valid in Windows file names.

## Conventions the tenant chart relies on

- Release name: the deployable name; namespace: the Application's (`.Release.Namespace`), which is also the
  name of the namespace's ListenerSet in `platform-ingress`.
- `replicaCount`: the tenant chart passes `replicaCount=0` while the app is frozen (`status: frozen`).
- Octopus rewrites every values file an Application references, so the pinned values live only in
  `envs/<env>/<deployable>/values.yaml`; `helm.imageReplacePaths` names exactly those paths.

## Image contract

- `web`: HTTP on 8080, `GET /healthz`, non-root, read-only root file system (writable `/tmp`). Reads
  `APP_ENVIRONMENT`, `APPLICATIONINSIGHTS_CONNECTION_STRING` and, with a database, `DB_HOST`, `DB_PORT`,
  `DB_NAME`, `DB_USER`, `DB_PASSWORD` (Secret `db-app`).
- `migrator`: applies the pending migrations and exits 0, idempotent and forward-only. Reads the same `DB_*`
  names (Secret `db-migrator`), retries its login until `MIGRATION_DB_READY_TIMEOUT_SECONDS`, and trusts the
  server certificate through `SSL_CERT_FILE` (`platform-internal-ca`) (CAP-GIT-009, CAP-GIT-010).

## Adapting

- No database: set `database.enabled: false` in `values.yaml`, delete the `migrator` values and remove the
  image and its `imageReplacePaths` entry from the descriptor.
- Other image names: rename the values keys, the descriptor's `images` and `imageReplacePaths` together.
- App secrets: each `secrets[]` entry of the descriptor is a vault key; map it in `templates/secrets.yaml`.
- A chart from elsewhere: vendor it under the app folder and point `helm.chart` at it; keep the conventions.
- Never here: namespaces, quotas, limit ranges, NetworkPolicies, secret stores, ListenerSets, Kyverno or
  Argo CD kinds (tenant chart; AppProject `app-<app>` denies them).
- Verify: `onboarding check <app>` and
  `helm template <deployable> charts/<deployable> -n <app>-<env> -f envs/<env>/<deployable>/values.yaml`.
