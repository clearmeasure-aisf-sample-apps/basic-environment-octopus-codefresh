# apps/workorders/worker: the NServiceBus endpoint WorkOrderProcessing (src/Worker of app #1,
# clearmeasure-aisf-sample-apps/20260923-001), deployed as Deployment `worker` of
# gitops/apps/workorders/ (contract §7.0 "Registry": apps/<app>/<image>). No HTTP endpoint and
# no probes until WI-04; it needs RemotableBus__ApiUrl and the SQL connection string from the
# environment (appsettings files are not published).
#
# Build context: a folder holding publish/ (dotnet publish of src/Worker from the app checkout),
# staged by codefresh/apps/workorders/scripts/stage-built.ps1; built, pushed and signed by
# workorders/release (and workorders/preview for previews).
#
# Base: the ASP.NET Core runtime, because ChurchBulletin.ServiceDefaults references the
# Microsoft.AspNetCore.App shared framework. Pinned by digest; bumped by pull request.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f

ARG VERSION=0.0.0-local
ARG REVISION=unknown

LABEL org.opencontainers.image.source="https://github.com/clearmeasure-aisf-sample-apps/20260923-001" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.title="apps/workorders/worker" \
      org.opencontainers.image.description="Work Orders NServiceBus endpoint WorkOrderProcessing"

WORKDIR /app
# Files stay root-owned and read-only for the runtime user.
COPY publish/ /app/

USER $APP_UID
ENTRYPOINT ["dotnet", "Worker.dll"]
