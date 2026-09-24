# apps/sandbox/unsigned:0.0.0-fixture: the admission fixture of CAP-AZ-001 (§7.0 Registry; ADR-IR34
# "Test harness"). platform-env/fixtures pushes it once with registry integration acr-apps-release
# and never signs it; no deployable pins it and registry retention keeps it.
# The prod release-signature policy must reject it; SignedAdmissionTests only dry-runs a pod with
# it, so the image holds no executable and cannot run anywhere.
FROM scratch
COPY fixture.txt /fixture.txt
LABEL org.opencontainers.image.source="https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh" \
      org.opencontainers.image.title="apps/sandbox/unsigned" \
      org.opencontainers.image.version="0.0.0-fixture" \
      org.opencontainers.image.description="Unsigned admission fixture of the platform conformance suite; never deployed"
