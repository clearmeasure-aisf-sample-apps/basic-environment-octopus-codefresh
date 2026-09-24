# Codefresh phase 0 preview

Phase 0 makes the Codefresh side of the platform visible before any Azure resource exists. The projects and pipelines appear in Codefresh with their final names, runtimes and YAML locations. Nothing triggers them and nothing can run them successfully yet.

## What gets created

`codefresh/preview/register-preview.sh` creates each object, or replaces it by name if it already exists:

| Kind | Name | Rendered from |
|---|---|---|
| Project | `workorders` | — |
| Project | `platform-env` | — |
| Pipeline | `workorders/ci` | `codefresh/workorders/specs/workorders-ci.yml` |
| Pipeline | `workorders/release` | `codefresh/workorders/specs/workorders-release.yml` |
| Pipeline | `workorders/preview` | `codefresh/workorders/specs/workorders-preview.yml` |
| Pipeline | `workorders/ci-image` | `codefresh/workorders/specs/workorders-ci-image.yml` |
| Pipeline | `platform-env/env-checks` | `codefresh/specs/platform-env-checks.yml` |

Each pipeline is its committed spec with these changes:
- **No triggers or cron triggers.** Codefresh creates no webhook on `clearmeasure-aisf-sample-apps/20260923-001` or on this repo, and nothing fires on a schedule.
- **No contexts or pipeline variables.** `workorders-ci`, `workorders-release` and `workorders-octopus` (the Octopus Space Manager API key, `workorders/release` only) arrive in phase 1. The stored contexts `azure-runtime-provisioner` and `github-aisf-sample-apps-token` stay unattached, as always.
- **One runtime.** `runtimeEnvironment.name` is `$CF_RUNTIME` for all five pipelines. The phase-1 split into `<cf-runtime-ci>` and `<cf-runtime-release>` (ADR-D17) comes with the full specs.
- **The YAML location is kept.** `specTemplate` loads `codefresh/…/pipelines/<name>.yml` from `main` of this repo through the Git integration `github-aisf-sample-apps`, so the pipeline view shows the real YAML.
- **Marked as a preview.** `metadata.description` says "PHASE 0 PREVIEW: visible only, not runnable until phase 1", and the tag is `phase-0-preview`.

Concurrency (`workorders/release`, `workorders/ci-image`: 1) and the termination policy (`workorders/ci`, `workorders/preview`) are kept.

## Running it

```sh
# Render the payloads only (no API call, no key needed)
DRY_RUN=1 CF_RUNTIME=<runtime-name> bash codefresh/preview/register-preview.sh

# Create or replace the objects
CF_API_KEY=… CF_RUNTIME=<runtime-name> bash codefresh/preview/register-preview.sh
```

`CF_URL` defaults to `https://g.codefresh.io`. The key goes only into a private header file and is never printed. Running the script again replaces the pipelines with the same content.

## Why a manual run fails until phase 1

A build started by hand now fails. This is expected:
- Every workorders step image is `<acr-name>.azurecr.io/platform/ci-dotnet:<ci-image-version>`. The ACR, the registry integrations (`acr-workorders-release`, `acr-workorders-preview`, `acr-platform-ci`) and the first CI image do not exist yet, and the pipeline YAML still carries those placeholders.
- `workorders/ci-image` needs `acr-platform-ci` to push, and its Dockerfile's base-image digests are placeholders.
- The gates need `CI_SQL_SA_PASSWORD` and the OpenAI keys from `workorders-ci`. The handoff needs `workorders-release` and `workorders-octopus` (`OCTOPUS_URL`, `OCTOPUS_SPACE_ID`, `OCTOPUS_API_KEY`).
- `platform-env/env-checks` needs the pinned tool-image digests and `PLATFORM_BOT_AUTHORS`.
- Without a trigger, a manual build has no Git revision of `20260923-001` to build.

## Phase 1: re-registering the full specs

Once the bootstrap prerequisites exist (`docs/bootstrap.md`: runtimes, registry integrations, both contexts, the first `platform/ci-dotnet` tag, and the replaced placeholders), replace each preview with its full spec, from the root of this repo:

```sh
codefresh replace pipeline -f codefresh/workorders/specs/workorders-ci-image.yml
codefresh replace pipeline -f codefresh/workorders/specs/workorders-ci.yml
codefresh replace pipeline -f codefresh/workorders/specs/workorders-release.yml
codefresh replace pipeline -f codefresh/specs/platform-env-checks.yml
codefresh replace pipeline -f codefresh/workorders/specs/workorders-preview.yml   # phase 6
```

`replace` keeps each pipeline's ID, so `<CF_RELEASE_PIPELINE_ID>` (part of the Kyverno signer identity) does not change between the preview and phase 1. It restores the triggers (and with them the webhooks), the contexts, the two runtimes and the spec's own `specTemplate.revision`. Replacing overwrites the preview description; remove the `phase-0-preview` tag if it remains.

[VERIFY] The project routes (`GET /api/projects/name/<name>`, `POST /api/projects`) and whether `metadata.description` is stored for pipelines. The pipeline routes are the ones the `codefresh` CLI uses (`POST /api/pipelines`, `PUT /api/pipelines/<name>`).

## Applied result (phase 0, 2026-09-24)

`register-preview.sh` created projects `workorders` and `platform-env` and the five pipelines. None has triggers or contexts, and each loads its YAML from this repo's `main` through `github-aisf-sample-apps`. Codefresh answers the lookup of a missing project with HTTP 500 and a "not found" body; the script treats that as 404.
