# apps/sandbox/migrator: the migration console of the conformance fixture app, run by the Argo CD
# PreSync hook Job db-migrate in sandbox-<env> (contract §7.0 "Migrations"; CAP-GIT-009, CAP-GIT-010).
#
# Build context: publish/ (dotnet publish of src/Sandbox.Migrator), scripts/ (db/scripts, plus the
# failing script when the commit carries toggles/failing-migration) and migrate.sh, staged by
# codefresh/apps/sandbox/scripts/stage-images.sh; built, pushed and signed by sandbox/release.
#
# Settings: DB_HOST, DB_PORT, DB_NAME, DB_USER, DB_PASSWORD (Secret db-migrator; envFrom with prefix
# DB_ works) and SSL_CERT_FILE (ca.crt of Secret db-tls): the server certificate is validated.
# migrate.sh retries until MIGRATION_DB_READY_TIMEOUT_SECONDS pass.
FROM mcr.microsoft.com/dotnet/runtime:10.0.12-noble@sha256:ff17a18b639a0327e52c7c296fa2e1abe6e03eb61d8121a8ef67cc6aa430a27e

ARG VERSION=0.0.0-local
ARG REVISION=unknown

LABEL org.opencontainers.image.source="https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.title="apps/sandbox/migrator" \
      org.opencontainers.image.description="Platform conformance fixture migration console with scripts"

ENV MIGRATION_DB_READY_TIMEOUT_SECONDS=300 \
    MIGRATION_COMMAND="dotnet /app/Sandbox.Migrator.dll"

WORKDIR /app
COPY publish/ /app/
COPY scripts/ /app/scripts/
# Staged with mode 0755 (a plain COPY works with and without BuildKit).
COPY migrate.sh /usr/local/bin/migrate.sh

USER $APP_UID
ENTRYPOINT ["/usr/local/bin/migrate.sh"]
CMD ["update", "--scripts", "/app/scripts"]
