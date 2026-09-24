# syntax=docker/dockerfile:1.7
# platform/db-tools-mssql: the tool image of the per-app backup and restore Jobs in namespace
# platform-backup (ADR-IR34 §11.7.1 item 8; image contract in
# gitops/platform/components/backup/mssql/kustomization.yaml): bash, GNU date, sqlcmd on PATH
# (mssql-tools18), the Azure CLI (workload-identity login, user-delegation SAS), plus azcopy and
# jq for the Q42 fallback (a TO DISK backup copied to Blob storage).
# Built, pushed and signed by platform-env/ci-image-dotnet next to platform/ci-dotnet; consumers
# pin <tag>@sha256:<digest>.
#
# User: `dbtools` (uid 1000; the base image's `ubuntu` user is removed), no root at run time; HOME and AZURE_CONFIG_DIR point at a writable
# volume that the Jobs mount (/work).
#
# Building behind a TLS-intercepting proxy (local rehearsal only): pass the proxy CA as a
# BuildKit secret, which never reaches a layer:
#   docker build --network host --build-arg HTTPS_PROXY --secret id=build-ca,src=<ca.pem> .
FROM ubuntu:24.04@sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3

ARG AZURE_CLI_VERSION=2.90.0
ARG MSSQL_TOOLS_VERSION=18.7.1.1-1
ARG AZCOPY_VERSION=10.32.8

ENV DEBIAN_FRONTEND=noninteractive \
    PATH="${PATH}:/opt/mssql-tools18/bin"

SHELL ["/bin/bash", "-o", "pipefail", "-c"]

# hadolint ignore=DL3008
RUN --mount=type=secret,id=build-ca,required=false,mode=0444 \
    if [ -s /run/secrets/build-ca ]; then \
      export CURL_CA_BUNDLE=/run/secrets/build-ca; \
      printf 'Acquire::https::CaInfo "/run/secrets/build-ca";\n' > /etc/apt/apt.conf.d/99build-ca; \
    fi \
 && sed -i 's|http://|https://|g' /etc/apt/sources.list.d/ubuntu.sources \
 && apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl gnupg jq lsb-release \
 && install -d -m 0755 /etc/apt/keyrings \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /etc/apt/keyrings/microsoft.gpg \
 && chmod a+r /etc/apt/keyrings/microsoft.gpg \
 && codename="$(lsb_release -cs)" arch="$(dpkg --print-architecture)" \
 && printf 'Types: deb\nURIs: https://packages.microsoft.com/repos/azure-cli/\nSuites: %s\nComponents: main\nArchitectures: %s\nSigned-by: /etc/apt/keyrings/microsoft.gpg\n' "${codename}" "${arch}" > /etc/apt/sources.list.d/azure-cli.sources \
 && printf 'Types: deb\nURIs: https://packages.microsoft.com/ubuntu/24.04/prod\nSuites: %s\nComponents: main\nArchitectures: %s\nSigned-by: /etc/apt/keyrings/microsoft.gpg\n' "${codename}" "${arch}" > /etc/apt/sources.list.d/microsoft-prod.sources \
 && apt-get update \
 && ACCEPT_EULA=Y apt-get install -y --no-install-recommends \
      "azure-cli=${AZURE_CLI_VERSION}-1~${codename}" \
      "mssql-tools18=${MSSQL_TOOLS_VERSION}" unixodbc \
      "azcopy=${AZCOPY_VERSION}" \
 && apt-get purge -y gnupg lsb-release && apt-get autoremove -y \
 && rm -f /etc/apt/apt.conf.d/99build-ca \
 && rm -rf /var/lib/apt/lists/* \
 && { userdel --remove ubuntu 2>/dev/null || true; } \
 && useradd --uid 1000 --create-home --shell /bin/bash dbtools \
 && sqlcmd -? >/dev/null && az version --output none && azcopy --version && jq --version && date --version | head -n 1

ARG VERSION=0.0.0-local
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.title="platform/db-tools-mssql" \
      org.opencontainers.image.description="Backup and restore tools for SQL Server in pods: sqlcmd, Azure CLI, azcopy"

USER 1000
WORKDIR /work
