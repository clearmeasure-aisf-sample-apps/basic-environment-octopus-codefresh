# Sandbox fixture app

The conformance fixture of the platform (ADR-IR34 "Testability hooks"; contract §7.0 "Conformance":
app `sandbox`, images `apps/sandbox/web` and `apps/sandbox/migrator`, namespaces `sandbox-<env>`).
The environment repo keeps this source at `fixtures/sandbox-app/`; step P1-11 seeds the private
repository `<sandbox-app-repo>` from it. The sandbox repository holds only this source: its
pipelines live in `codefresh/apps/sandbox/`, its Dockerfiles in `containers/apps/sandbox/` and its
desired state in `gitops/apps/sandbox/` of the environment repo (ADR-D18).

## What it is

A .NET 10 minimal API and a migration console, with unit tests (NUnit 4, Shouldly, TRX results).

| Endpoint | Behaviour |
|---|---|
| `GET /healthz` | `200 {"status":"ok"}`; no database call (probes) |
| `GET /version` | `200 {"app":"sandbox","version":…,"revision":…}` from `APP_VERSION` and `APP_REVISION` (set by the image) |
| `GET /data/canary` | `200 {"value":…,"updatedAtUtc":…}`, `404` before the first write, `503` without a database |
| `PUT /data/canary` | Body `{"value":"…"}` (1 to 200 characters); stores and returns it; `400` when invalid |

The canary is one row in `dbo.Canary`. Conformance tests write it before a disruption (sleep,
rebuild, restore, password rotation) and read it afterwards.

## Configuration

Both programs read the keys of the platform's database Secrets (`db-app` for the API, `db-migrator`
for the migrator; keys `host`, `port`, `database`, `username`, `password`), as environment variables
`DB_HOST`, `DB_PORT` (default 1433), `DB_NAME` (or `DB_DATABASE`), `DB_USER` (or `DB_USERNAME`) and
`DB_PASSWORD`. An `envFrom` with prefix `DB_` works, because configuration keys are
case-insensitive. `ConnectionStrings__Sandbox` overrides all of them.

- Encryption is always on. The API trusts the in-cluster server certificate
  (`DB_TRUST_SERVER_CERTIFICATE` defaults to `true`; ADR-IR34 decision 8).
- The migrator validates it (default `false`) against `SSL_CERT_FILE`, which the PreSync Job points
  at `ca.crt` of Secret `db-tls` (Q43).

## Migrations

`Sandbox.Migrator [update] [--scripts <folder>]` applies the `*.sql` files of `db/scripts/` in
ordinal order, each in one transaction, and journals them in `dbo.SchemaVersions`. Exit codes:
0 applied or up to date, 1 a script or the connection failed, 2 invalid settings. In the image, the
platform entrypoint `migrate.sh` retries it until `MIGRATION_DB_READY_TIMEOUT_SECONDS` pass.

## Toggles

See `toggles/README.md`: `toggles/failing-test` fails the unit tests (CAP-CF-004), and
`toggles/failing-migration` ships a failing script in the migrator image (CAP-GIT-010).

## Local build

```sh
dotnet build Sandbox.sln -c Release
dotnet test Sandbox.sln -c Release --logger trx
```
