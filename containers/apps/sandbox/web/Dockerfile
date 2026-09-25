# apps/sandbox/web: the API of the conformance fixture app (fixtures/sandbox-app, seeded into
# <sandbox-app-repo>; contract §7.0 "Conformance"). Endpoints /healthz, /version, /data/canary.
#
# Build context: a folder holding publish/ (dotnet publish of src/Sandbox.Web), staged by
# codefresh/apps/sandbox/scripts/stage-images.ps1; built, pushed and signed by sandbox/release.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f

ARG VERSION=0.0.0-local
ARG REVISION=unknown

LABEL org.opencontainers.image.source="https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.title="apps/sandbox/web" \
      org.opencontainers.image.description="Platform conformance fixture API: healthz, version, canary"

# /version reports these.
ENV APP_VERSION="${VERSION}" \
    APP_REVISION="${REVISION}"

WORKDIR /app
COPY publish/ /app/

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sandbox.Web.dll"]
