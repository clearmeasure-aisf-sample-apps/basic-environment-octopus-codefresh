# syntax=docker/dockerfile:1.7
# platform/ci-dotnet: the .NET toolchain image of the platform (contract §7.0 "Registry":
# platform/<image>; §7.5). One image for
#   - the freestyle steps of every .NET app pipeline (app #1, the sandbox) and of the platform
#     pipelines (env-checks, conformance, registry-retention, fixtures);
#   - Octopus container steps that name #{StepImage.CiDotnet} (Azure CLI, sqlcmd, the
#     Playwright acceptance tests).
# Built, pushed and signed keyless by platform-env/ci-image-dotnet
# (codefresh/platform/pipelines/ci-image-dotnet.yml) with registry integration acr-platform-ci;
# tag <ci-image-version> (date-based); consumers pin <ci-image-version>@sha256:<ci-image-digest>.
#
# Contents:
#   .NET SDK 10.0.401 (the 1xx band's images stopped at 10.0.103; 10.0.401 satisfies app #1's
#     global.json 10.0.100 rollForward latestMinor and the harness's latestFeature), with pwsh 7;
#   docker CLI (freestyle steps reach the build's dind daemon; runtime userAccess: true);
#   cosign, Syft and crane (supply chain), Gitleaks (security_scan);
#   the Octopus CLI `octopus` (release handoff; ADR-IR18: pinned with this image);
#   Azure CLI, sqlcmd (mssql-tools18), jq, git, curl, unzip, python3 with PyYAML (consistency.sh under
#     the offline tests);
#   Helm 4 (helm template of the tenant chart in the GitOps offline tests; never install or upgrade);
#   the repository checks of platform-env/env-checks: kustomize, kubeconform, Terraform (fmt and validate
#     only), yamllint, and the PSScriptAnalyzer module for PowerShell 7 (the platform's script language,
#     docs/scripting.md);
#   Chromium for Playwright 1.54 with its OS libraries (app #1's acceptance tests);
#   dotnet global tools crap4dotnet 0.1.1, dotnet-script 2.0.0 and Octopus.DotNet.Cli for root,
#     with /root/.dotnet/tools on PATH (the CRAP gate and Package-Everything).
# Tools that ship as images are copied from their vendors' images, pinned by digest; apt
# packages come from signed repositories at pinned versions.
#
# User: root. Codefresh freestyle steps write the shared volume and build.ps1 runs
# `playwright install --with-deps` (apt), so the default stays root; `app` (uid 1654) exists for
# runtimes that want a non-root user (docker run --user app).
#
# Building behind a TLS-intercepting proxy (local rehearsal only): pass the proxy CA as a
# BuildKit secret, which never reaches a layer:
#   docker build --network host --build-arg HTTPS_PROXY --secret id=build-ca,src=<ca.pem> .
# Codefresh builds with `buildkit: true` and no secret.

FROM ghcr.io/sigstore/cosign/cosign:v2.6.2@sha256:57bd8f63e0207052ce8f70692c2f0e5950115b83bc1d0876fb0060e5ff8e7a8c AS cosign
FROM anchore/syft:v1.52.0@sha256:500e2d872ac019436926e8322b4fc1f39441d94d21f6f4046c6ff29b30e8cb02 AS syft
FROM gcr.io/go-containerregistry/crane:v0.22.1@sha256:1f968817b95790bed063f71175aa6b8ff879fa17064020415f3e18bb6e6a36e1 AS crane
FROM ghcr.io/gitleaks/gitleaks:v8.28.0@sha256:cdbb7c955abce02001a9f6c9f602fb195b7fadc1e812065883f695d1eeaba854 AS gitleaks
FROM docker:29.8.1-cli@sha256:018edbc908e08fcc9dbf029c812c34251e9b4719e6f71ca0e5eae2a987d014ca AS dockercli

# kustomize and kubeconform built by `go install`, which verifies every module against the Go checksum
# database (the same trust as the env-checks steps that installed them before); static binaries.
FROM golang:1.26-bookworm@sha256:a688600ca24f8a4d3ca77f95b0dd40704a9fc787c826660eb7ba0b641b8b175d AS gotools
ARG KUSTOMIZE_VERSION=v5.8.1
ARG KUBECONFORM_VERSION=v0.8.0
RUN --mount=type=secret,id=build-ca,required=false,mode=0444 \
    if [ -s /run/secrets/build-ca ]; then export SSL_CERT_FILE=/run/secrets/build-ca; fi \
 && CGO_ENABLED=0 GOBIN=/out go install "sigs.k8s.io/kustomize/kustomize/v5@${KUSTOMIZE_VERSION}" \
 && CGO_ENABLED=0 GOBIN=/out go install "github.com/yannh/kubeconform/cmd/kubeconform@${KUBECONFORM_VERSION}"

FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29

ARG AZURE_CLI_VERSION=2.90.0
ARG HELM_VERSION=4.3.0
# sha256 of helm-v${HELM_VERSION}-linux-amd64.tar.gz, from get.helm.sh/<file>.sha256sum.
ARG HELM_SHA256=86584a54def73570558f66f5111cc53dfed56689637ae32c1201205d494f54fb
ARG MSSQL_TOOLS_VERSION=18.7.1.1-1
ARG OCTOPUS_CLI_VERSION=2.26.0
ARG OCTOPUS_DOTNET_CLI_VERSION=9.1.7
ARG PLAYWRIGHT_VERSION=1.54.0
ARG PSSCRIPTANALYZER_VERSION=1.25.0
ARG TERRAFORM_VERSION=1.16.4
# sha256 of terraform_${TERRAFORM_VERSION}_linux_amd64.zip, from releases.hashicorp.com/terraform/<version>/..._SHA256SUMS.
ARG TERRAFORM_SHA256=dc94af0eef1147718ad7c8daea792ed199e3e0492eec180d0adafa2a65a879df
ARG YAMLLINT_VERSION=1.38.0

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    MSBUILDTERMINALLOGGER=off \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    DEBIAN_FRONTEND=noninteractive \
    PATH="${PATH}:/opt/mssql-tools18/bin:/root/.dotnet/tools"

SHELL ["/bin/bash", "-o", "pipefail", "-c"]

# Proxy CA for this RUN only (see the header); absent in Codefresh.
# hadolint ignore=DL3008
# apt switches to https only when the build-ca secret is given (local build behind the TLS-intercepting
# proxy). Without it the base image has no trust store yet, so the default http sources (apt-key signed) stay.
RUN --mount=type=secret,id=build-ca,required=false,mode=0444 \
    if [ -s /run/secrets/build-ca ]; then \
      export CURL_CA_BUNDLE=/run/secrets/build-ca; \
      printf 'Acquire::https::CaInfo "/run/secrets/build-ca";\n' > /etc/apt/apt.conf.d/99build-ca; \
      sed -i 's|http://|https://|g' /etc/apt/sources.list.d/ubuntu.sources; \
    fi \
 && apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl git jq unzip gnupg lsb-release python3 python3-yaml python3-venv \
 && install -d -m 0755 /etc/apt/keyrings \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /etc/apt/keyrings/microsoft.gpg \
 && curl -fsSL https://apt.octopus.com/public.key | gpg --dearmor -o /etc/apt/keyrings/octopus.gpg \
 && chmod a+r /etc/apt/keyrings/*.gpg \
 && codename="$(lsb_release -cs)" arch="$(dpkg --print-architecture)" \
 && printf 'Types: deb\nURIs: https://packages.microsoft.com/repos/azure-cli/\nSuites: %s\nComponents: main\nArchitectures: %s\nSigned-by: /etc/apt/keyrings/microsoft.gpg\n' "${codename}" "${arch}" > /etc/apt/sources.list.d/azure-cli.sources \
 && printf 'Types: deb\nURIs: https://packages.microsoft.com/ubuntu/24.04/prod\nSuites: %s\nComponents: main\nArchitectures: %s\nSigned-by: /etc/apt/keyrings/microsoft.gpg\n' "${codename}" "${arch}" > /etc/apt/sources.list.d/microsoft-prod.sources \
 && printf 'Types: deb\nURIs: https://apt.octopus.com/\nSuites: stable\nComponents: main\nArchitectures: %s\nSigned-by: /etc/apt/keyrings/octopus.gpg\n' "${arch}" > /etc/apt/sources.list.d/octopus.sources \
 && apt-get update \
 && ACCEPT_EULA=Y apt-get install -y --no-install-recommends \
      "azure-cli=${AZURE_CLI_VERSION}-1~${codename}" \
      "mssql-tools18=${MSSQL_TOOLS_VERSION}" unixodbc \
      "octopus-cli=${OCTOPUS_CLI_VERSION}" \
 && rm -f /etc/apt/apt.conf.d/99build-ca \
 && rm -rf /var/lib/apt/lists/*

COPY --from=cosign /ko-app/cosign /usr/local/bin/cosign
COPY --from=syft /syft /usr/local/bin/syft
COPY --from=crane /ko-app/crane /usr/local/bin/crane
COPY --from=gitleaks /usr/bin/gitleaks /usr/local/bin/gitleaks
COPY --from=dockercli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=gotools /out/kustomize /usr/local/bin/kustomize
COPY --from=gotools /out/kubeconform /usr/local/bin/kubeconform

# Helm 4 from the project's release host, checked against its published sha256 (amd64 only, like
# every platform image).
RUN --mount=type=secret,id=build-ca,required=false,mode=0444 \
    if [ -s /run/secrets/build-ca ]; then export CURL_CA_BUNDLE=/run/secrets/build-ca; fi \
 && [ "$(dpkg --print-architecture)" = "amd64" ] \
 && curl -fsSL -o /tmp/helm.tar.gz "https://get.helm.sh/helm-v${HELM_VERSION}-linux-amd64.tar.gz" \
 && printf '%s  %s\n' "${HELM_SHA256}" /tmp/helm.tar.gz | sha256sum -c - \
 && tar -xzf /tmp/helm.tar.gz -C /tmp linux-amd64/helm \
 && install -m 0755 /tmp/linux-amd64/helm /usr/local/bin/helm \
 && rm -rf /tmp/helm.tar.gz /tmp/linux-amd64

# Terraform (fmt and validate in env-checks; never plan or apply here), checked against its published sha256;
# yamllint in its own virtual environment; PSScriptAnalyzer for every user of pwsh.
RUN --mount=type=secret,id=build-ca,required=false,mode=0444 \
    if [ -s /run/secrets/build-ca ]; then \
      export CURL_CA_BUNDLE=/run/secrets/build-ca PIP_CERT=/run/secrets/build-ca SSL_CERT_FILE=/run/secrets/build-ca; \
    fi \
 && [ "$(dpkg --print-architecture)" = "amd64" ] \
 && curl -fsSL -o /tmp/terraform.zip "https://releases.hashicorp.com/terraform/${TERRAFORM_VERSION}/terraform_${TERRAFORM_VERSION}_linux_amd64.zip" \
 && printf '%s  %s\n' "${TERRAFORM_SHA256}" /tmp/terraform.zip | sha256sum -c - \
 && unzip -q -o /tmp/terraform.zip terraform -d /usr/local/bin \
 && chmod 0755 /usr/local/bin/terraform \
 && rm -f /tmp/terraform.zip \
 && python3 -m venv /opt/yamllint \
 && /opt/yamllint/bin/pip install --no-cache-dir --disable-pip-version-check "yamllint==${YAMLLINT_VERSION}" \
 && ln -s /opt/yamllint/bin/yamllint /usr/local/bin/yamllint \
 && pwsh -NoProfile -Command "Save-PSResource -Name PSScriptAnalyzer -Version '${PSSCRIPTANALYZER_VERSION}' -Repository PSGallery -TrustRepository -Path /usr/local/share/powershell/Modules -Quiet" \
 && chmod -R a+rX /usr/local/share/powershell

# Chromium (and its headless shell) for Microsoft.Playwright ${PLAYWRIGHT_VERSION}, installed by
# the package's own script from a throwaway project, with the OS libraries (--with-deps).
# hadolint ignore=DL3003
RUN --mount=type=secret,id=build-ca,required=false,mode=0444 \
    if [ -s /run/secrets/build-ca ]; then \
      export SSL_CERT_FILE=/run/secrets/build-ca NODE_EXTRA_CA_CERTS=/run/secrets/build-ca; \
      printf 'Acquire::https::CaInfo "/run/secrets/build-ca";\n' > /etc/apt/apt.conf.d/99build-ca; \
    fi \
 && mkdir -p /tmp/pw && cd /tmp/pw \
 && dotnet new classlib --framework net10.0 --name pw --output . \
 && dotnet add package Microsoft.Playwright --version "${PLAYWRIGHT_VERSION}" \
 && dotnet build --configuration Release --nologo --verbosity quiet -p:CopyLocalLockFileAssemblies=true \
 && pwsh -NoProfile -File bin/Release/net10.0/playwright.ps1 install chromium --with-deps \
 && cd / && rm -rf /tmp/pw /root/.nuget/packages /tmp/NuGetScratch* \
 && rm -f /etc/apt/apt.conf.d/99build-ca \
 && rm -rf /var/lib/apt/lists/* \
 && chmod -R a+rX /ms-playwright

# dotnet global tools that the CRAP audit and Package-Everything would otherwise fetch on every
# run; versions match scripts/crap/run-crap-audit.ps1 of app #1.
RUN --mount=type=secret,id=build-ca,required=false,mode=0444 \
    if [ -s /run/secrets/build-ca ]; then export SSL_CERT_FILE=/run/secrets/build-ca; fi \
 && dotnet tool install --global crap4dotnet --version 0.1.1 \
 && dotnet tool install --global dotnet-script --version 2.0.0 \
 && dotnet tool install --global Octopus.DotNet.Cli --version "${OCTOPUS_DOTNET_CLI_VERSION}" \
 && rm -rf /root/.nuget/packages /tmp/NuGetScratch*

# Non-root user for runtimes that ask for one; .NET images may define `app` already.
RUN id -u app >/dev/null 2>&1 || useradd --uid 1654 --create-home --shell /bin/bash app

RUN dotnet --list-sdks \
 && pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' \
 && docker --version && cosign version && syft version && crane version && gitleaks version \
 && helm version --short \
 && kustomize version && kubeconform -v && terraform version && yamllint --version \
 && pwsh -NoProfile -Command "Import-Module PSScriptAnalyzer; (Get-Module PSScriptAnalyzer).Version.ToString()" \
 && octopus version && az version --output none && sqlcmd -? >/dev/null \
 && jq --version && git --version && python3 -c 'import yaml' \
 && ls -d /ms-playwright/chromium-* /ms-playwright/chromium_headless_shell-*

WORKDIR /workspace

# Declared last: a VERSION variable in the environment of an earlier RUN would become the
# MSBuild Version property of the dotnet commands there.
ARG VERSION=0.0.0-local
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.title="platform/ci-dotnet" \
      org.opencontainers.image.description="Platform .NET toolchain: SDK 10, pwsh 7 with PSScriptAnalyzer, docker CLI, cosign, Syft, Helm 4, kustomize, kubeconform, Terraform, yamllint, Octopus CLI, Azure CLI, sqlcmd, Playwright Chromium"
