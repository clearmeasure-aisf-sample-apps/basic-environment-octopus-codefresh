# apps/workorders/db-migrator: the DbUp console ClearMeasure.Bootcamp.Database (src/Database of
# app #1) with its SQL scripts (contract §7.0 "Migrations"; ADR-IR34 decision 1). It runs as the
# Argo CD PreSync hook Job `db-migrate` in the app namespace, before the new pods roll out.
#
# Build context: a folder holding publish/ (dotnet publish of src/Database), scripts/ (a copy
# of src/Database/scripts) and migrate.sh, staged by
# codefresh/apps/workorders/scripts/stage-built.ps1; built, pushed and signed by
# workorders/release (and workorders/preview).
#
# Usage (the console's positional arguments, passed through by migrate.sh):
#   update|rebuild|baseline <server>[,<port>] <database> /app/scripts [user] [password]
# Always pass /app/scripts: the console's default script folder is the Windows path ".\scripts".
# Kubernetes expands $(VAR) in args, so the Job can pass the db-migrator Secret's keys through
# environment variables without a shell.
#
# Database readiness: migrate.sh retries the console until it succeeds or
# MIGRATION_DB_READY_TIMEOUT_SECONDS (default 300; 0 = one attempt) pass, so a Job that starts
# while SQL Server or its logins are still coming up waits instead of failing (§7.0).
#
# TLS: for any server that is not local, the console requires encryption and validates the
# certificate (ADR-IR24). The Job mounts ca.crt of Secret db-tls (the platform-internal-ca) and
# sets SSL_CERT_FILE to it; .NET on Linux reads its trust anchors from SSL_CERT_FILE (Q43:
# verified for .NET 10 TLS through a private CA, 2026-09-24; see docs/preview-codefresh.md).
FROM mcr.microsoft.com/dotnet/runtime:10.0.12-noble@sha256:ff17a18b639a0327e52c7c296fa2e1abe6e03eb61d8121a8ef67cc6aa430a27e

ARG VERSION=0.0.0-local
ARG REVISION=unknown

LABEL org.opencontainers.image.source="https://github.com/clearmeasure-aisf-sample-apps/20260923-001" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.title="apps/workorders/db-migrator" \
      org.opencontainers.image.description="Work Orders DbUp migration console with scripts"

ENV MIGRATION_DB_READY_TIMEOUT_SECONDS=300 \
    MIGRATION_COMMAND="dotnet /app/ClearMeasure.Bootcamp.Database.dll"

WORKDIR /app
# Files stay root-owned and read-only for the runtime user.
COPY publish/ /app/
COPY scripts/ /app/scripts/
# Staged with mode 0755 by stage-built.ps1 (a plain COPY works with and without BuildKit).
COPY migrate.sh /usr/local/bin/migrate.sh

USER $APP_UID
ENTRYPOINT ["/usr/local/bin/migrate.sh"]
