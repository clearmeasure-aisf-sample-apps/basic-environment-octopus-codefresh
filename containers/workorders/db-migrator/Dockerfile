# workorders/db-migrator: the DbUp console ChurchBulletin.Database (src/Database of the
# application repo clearmeasure-aisf-sample-apps/20260923-001)
# plus its SQL scripts (contract §7.5). Used by the phase-6 preview Sync-hook Job
# (`rebuild`, then `seed` after WI-07; ADR-C4). Named environments migrate through
# the Octopus step migrate-database with the ChurchBulletin.Database package (ADR-C2).
#
# Build context: a directory holding publish/ (the `dotnet publish` output of
# src/Database) and scripts/ (a copy of src/Database/scripts), both from the
# application checkout, staged by codefresh/workorders/scripts/stage-built.sh;
# built, pushed and signed by workorders/release.
#
# Usage (positional arguments, F9):
#   update|rebuild|baseline <server> <database> /app/scripts [user] [password]
# Always pass /app/scripts: the console's default script directory is the Windows
# path ".\scripts", which does not resolve on Linux.
FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:<dotnet-runtime-10.0-digest>

ARG VERSION=0.0.0-local
ARG REVISION=unknown

LABEL org.opencontainers.image.source="https://github.com/clearmeasure-aisf-sample-apps/20260923-001" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.title="workorders/db-migrator" \
      org.opencontainers.image.description="Work Orders DbUp migration console with scripts"

WORKDIR /app
# Files stay root-owned and read-only for the runtime user.
COPY publish/ /app/
COPY scripts/ /app/scripts/

USER $APP_UID
ENTRYPOINT ["dotnet", "ClearMeasure.Bootcamp.Database.dll"]
