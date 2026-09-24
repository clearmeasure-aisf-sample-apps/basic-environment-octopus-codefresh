# Codefresh: clean start, runner and registration

How the Codefresh side of the platform is put in place: the clean start of the account (P1-01), the build runner (P1-04) and the registration of every project, pipeline, context and registry integration (P1-05). Contracts: `design/platform-design.md` §7.0 "Codefresh", ADR-IR34 "Build runner" and §11.9; `contracts/platform-contracts.yaml` key `codefresh`. The main loop runs these steps for the user; no step writes a secret to a file of this repository.

## What lives where

| Path | Content |
|---|---|
| `codefresh/runner/values.yaml` | Values of Helm chart `cf-runtime` 10.5.6 for `aks-platform-build` |
| `codefresh/register.sh` | Creates or replaces projects, pipelines, contexts and registry integrations by name |
| `codefresh/platform/integrations.yaml` | Shared contexts and registry integrations, declared without values; the superseded objects |
| `codefresh/platform/{pipelines,specs}/` | Project `platform-env`: `env-checks`, `ci-image-dotnet`, `conformance-arm`, `conformance`, `conformance-destructive`, `registry-retention`, `fixtures` |
| `codefresh/platform/scripts/` | Scripts of the platform pipelines (conformance, runbook runs, power state, retention) |
| `codefresh/apps/<app>/{pipelines,specs,scripts}/`, `integrations.yaml`, `version.env` | The pipelines of each app; app #1 is `workorders` ([README](../codefresh/apps/workorders/README.md)), the conformance fixture is `sandbox` |
| `codefresh/templates/{minimal,multi-image,dotnet-buildps1}/` | Starters copied by the onboarding tool into `codefresh/apps/<app>/` |
| `containers/platform/*`, `containers/apps/<app>/*` | Tool images (`platform/ci-dotnet`, `platform/db-tools-mssql`) and app images |

## Target state

| Kind | Objects |
|---|---|
| Runtime | `aks-platform-build/codefresh` (`<cf-runtime>`), the account default; agent `aks-platform-build_codefresh` |
| Projects | `platform-env`, `workorders`, `sandbox`, and one per onboarded app |
| Pipelines | `platform-env/{env-checks,ci-image-dotnet,conformance-arm,conformance,conformance-destructive,registry-retention,fixtures}`; `workorders/{ci,release,preview}`; `sandbox/{ci,release}` |
| Contexts | `platform-octopus`, `platform-registry`, `platform-registry-retention`, `platform-conformance`; the optional `app-workorders-ci` |
| Registry integrations | `acr-apps-release`, `acr-apps-preview`, `acr-platform-ci`, `acr-platform-pull` (primary for the registry domain) |
| Kept as they are | Git integration `github-aisf-sample-apps`; stored context `github-aisf-sample-apps-token` (attached to nothing) |

Every spec sets `runtimeEnvironment` to `aks-platform-build/codefresh`. The conformance and retention crons start disabled (`conformance-arm` weekdays 07:00 UTC, `conformance-destructive` Sunday 08:00 UTC, `registry-retention` nightly 03:00 UTC); P1-13 enables them. The weekly cron of `ci-image-dotnet` is on from the start.

## P1-01 Clean start

The user allowed every old object to be changed or discarded. Export each object to the operator's private folder (outside this repository), then delete it:
- projects `codefresh-k8s-pipeline`, `codefresh-onion8-aks` and `default`, with their pipelines;
- stored context `azure-runtime-provisioner` (never attached), and the other old contexts and integrations, except the Git integration `github-aisf-sample-apps`, the context `github-aisf-sample-apps-token` and the undeletable default Git context.

Keep projects `workorders` and `platform-env` and the pipelines `workorders/ci`, `workorders/release`, `workorders/preview` and `platform-env/env-checks`: `register.sh` replaces their specs in place, which keeps their IDs. The dead runtime `trf-CodeFresh-dev/codefresh` stays the account default until P1-04, because Codefresh refuses to delete the default runtime.

## P1-04 Runner on `aks-platform-build`

Prerequisites: `terraform/build` applied (cluster `aks-platform-build`, pools `system` and `builds`, the latter with taint `codefresh.io/builds=true:NoSchedule` and 30 pods per node), `CF_API_KEY` in the operator's shell, and a kubeconfig context `aks-platform-build` that signs in with Entra ID (local accounts are off; the provisioner holds AKS RBAC Cluster Admin on `rg-platform-build`):

```sh
az aks get-credentials --resource-group rg-platform-build --name aks-platform-build --overwrite-existing
kubelogin convert-kubeconfig --login azurecli        # the Azure CLI signed in as the provisioner
# Only behind the TLS-re-terminating proxy: validate the API server against the system trust store, which holds the
# proxy's CA, instead of the pinned cluster CA, as PLATFORM_TLS_SYSTEM_TRUST does for the harness [VERIFY].
# Fallback: the same commands through az aks command invoke (--file codefresh/runner/values.yaml).
kubectl config unset clusters.aks-platform-build.certificate-authority-data
```

```sh
# 1. Namespace and the token secret (the key never touches a file of this repository)
kubectl --context aks-platform-build create namespace codefresh
kubectl --context aks-platform-build -n codefresh create secret generic codefresh-token \
  --from-literal=token="$CF_API_KEY"

# 2. The runner: runtime aks-platform-build/codefresh, agent aks-platform-build_codefresh
helm upgrade --install cf-runtime oci://quay.io/codefresh/cf-runtime --version 10.5.6 \
  --kube-context aks-platform-build --namespace codefresh \
  --values codefresh/runner/values.yaml --wait --timeout 15m

# 3. Codefresh API: the default runtime first, then the dead runtime and its agent
H="$(mktemp)"; (umask 077 && printf 'Authorization: %s\n' "$CF_API_KEY" >"$H")
CF=https://g.codefresh.io/api
curl -fsS -H @"$H" "$CF/runtime-environments/aks-platform-build%2Fcodefresh" | jq '.metadata'
curl -fsS -X PUT -H @"$H" "$CF/runtime-environments/default/aks-platform-build%2Fcodefresh"
curl -fsS -X DELETE -H @"$H" "$CF/runtime-environments/trf-CodeFresh-dev%2Fcodefresh"
AGENT_ID="$(curl -fsS -H @"$H" "$CF/agents" | jq -r '.[] | select(.name == "trf-CodeFresh-dev_codefresh") | .id')"
curl -fsS -X DELETE -H @"$H" "$CF/agent/$AGENT_ID"
rm -f "$H"
```

The values that matter (`codefresh/runner/values.yaml`):
- `global`: `codefreshHost: https://g.codefresh.io`, `accountId: 66327682d5f6e0bfd0ef936a`, `context: aks-platform-build`, `runtimeName: aks-platform-build/codefresh`, `agentName: aks-platform-build_codefresh`, `codefreshTokenSecretKeyRef: {name: codefresh-token, key: token}`.
- The runner agent, the volume provisioner and the chart's hooks run on the `system` pool. Engine and dind pods select `kubernetes.azure.com/agentpool: builds` and tolerate `codefresh.io/builds=true:NoSchedule`; the first build scales the pool from zero [VERIFY Q51].
- dind requests 3 CPU and 11 GiB (limits 4 CPU and 12 GiB) on a `Standard_D4ds_v5` node; `storage.backend: local` with a 50 GiB volume per build node; `userAccess: true` gives freestyle steps the build's Docker daemon. The request fits because `terraform/build` sets 30 pods per `builds` node (about 15 GiB allocatable); at the overlay default of 250, AKS reserves 4 GiB and the pod never schedules.
- The app proxy, the monitor and the event exporter are off.

Verify: `kubectl -n codefresh get pods` shows the runner and the volume provisioner Running on the system node; CAP-CF-001 to CAP-CF-003 (`PlatformRuntimeTests`, `RunnerHealthTests`, `BuildScalingTests`). V03: the peak memory of app #1's release build fits one `Standard_D4ds_v5` node; the fallback is `Standard_D8ds_v5` for the `builds` pool with the dind values doubled.

## P1-05 Registration

`codefresh/register.sh` renders every object from the committed specs and declarations, then creates or replaces it by name. It is idempotent and never prints a secret.

| Mode | Creates or replaces |
|---|---|
| `--preview` | Every project and pipeline, without triggers, crons, contexts or variables, tagged `preview` (phase 0; not runnable) |
| `--full` | Every project and pipeline with triggers, crons, contexts and variables; every context and registry integration of `codefresh/platform/integrations.yaml` and `codefresh/apps/*/integrations.yaml` |
| `--app <app>` | The projects, pipelines and app-owned contexts of one app; the platform contexts it attaches must exist |
| `--dry-run` | With any mode: prints the payloads with secret values masked; no API call |
| `--prune` | With `--full`: deletes the superseded objects (pipeline `workorders/ci-image`; contexts `workorders-octopus`, `workorders-ci`, `workorders-release`, `azure-runtime-provisioner`) |

The operator's shell for `--full` (values never in a file):

| Variable | Becomes |
|---|---|
| `CF_API_KEY` | The operator's Codefresh key (header file only). `CF_URL` defaults to `https://g.codefresh.io`, `CF_RUNTIME` to `aks-platform-build/codefresh` |
| `OCTOPUS_URL`, `OCTOPUS_SPACE_ID`, `OCTOPUS_API_KEY` | Context `platform-octopus` (the Space Manager key, ADR-IR32) |
| `ACR_REGISTRY` | Domain of the four registry integrations; `ACR_REGISTRY` of `platform-registry` and `platform-registry-retention` |
| `CF_APPS_RELEASE_PASSWORD` | Token `cf-apps-release`: integration `acr-apps-release` and context `platform-registry` |
| `CF_APPS_PREVIEW_PASSWORD`, `CF_PLATFORM_CI_PASSWORD`, `CF_PLATFORM_PULL_PASSWORD` | Integrations `acr-apps-preview`, `acr-platform-ci`, `acr-platform-pull` |
| `CF_PLATFORM_RETENTION_PASSWORD` | Context `platform-registry-retention` (token `cf-platform-retention`) |
| `CONFORMANCE_AZURE_TENANT_ID`, `CONFORMANCE_AZURE_CLIENT_ID`, `CONFORMANCE_AZURE_CLIENT_SECRET`, `CONFORMANCE_GITHUB_TOKEN` | Context `platform-conformance` (`sp-platform-conformance`, the org PAT) |
| `CONFORMANCE_CODEFRESH_API_KEY` (optional) | `CODEFRESH_API_KEY` of `platform-conformance`, only if the build's own `CF_API_KEY` cannot read builds and agents (Q49) |
| `PLATFORM_BOT_AUTHORS` | Spec variable of `platform-env/env-checks` (`^octopus-argocd-pin-bot$`) |
| `SANDBOX_APP_REPO` | `owner/name` of `<sandbox-app-repo>`: the sandbox triggers and the conformance pipelines' variable |
| `APP_WORKORDERS_AI_OPENAI_APIKEY`, `…_URL`, `…_MODEL` (optional) | Context `app-workorders-ci`; while it is absent, the app's specs leave it off |

A missing value never becomes an empty secret: the object is reported `PENDING`, a pipeline whose spec still holds a placeholder is reported `ERROR` and skipped, and the exit code stays non-zero until everything is registered.

```sh
bash codefresh/register.sh --full --dry-run     # review
bash codefresh/register.sh --full               # apply
```

Then mint the first tool images and pin them in one reviewed commit:

```sh
# Run platform-env/ci-image-dotnet once (Codefresh UI, or: codefresh run platform-env/ci-image-dotnet -b main).
# Take the tag and the digest of platform/ci-dotnet from the build, then:
ACR=acrname TAG=20260924.0600-abc1234 DIGEST=0123abcd...      # the real values
grep -rl '<ci-image-version>' codefresh/apps codefresh/platform/pipelines codefresh/templates |
  xargs sed -i -e "s/<acr-name>/$ACR/g" -e "s/<ci-image-version>/$TAG/g" -e "s/<ci-image-digest>/$DIGEST/g"
```

`StepImage.CiDotnet` of the Octopus projects takes the same tag and digest; `consistency.sh` flags references that differ. The same build pushes `platform/db-tools-mssql`: its tag replaces `<db-tools-mssql-version>` (and `<acr-name>`) in `gitops/platform/tenant/values.yaml`, the image of the backup and restore Jobs. P1-11 runs `platform-env/fixtures` once (the unsigned `apps/sandbox/unsigned:0.0.0-fixture`). After the first release has gone through `platform-octopus`: `bash codefresh/register.sh --full --prune`.

## Behaviour notes

- Codefresh answers some lookups of a missing object with HTTP 500 and a "not found" body; `register.sh` treats that as 404.
- Pipelines are replaced with `PUT /api/pipelines/<name>`, so their IDs, and with them the signer identity of the release pipelines, survive a re-registration.
- A spec variable whose committed value is a `<placeholder>` takes the environment variable of the same name. Any other spec value that is a whole `<token>` takes `TOKEN` (upper case, dashes to underscores), for example `<sandbox-app-repo>` in the sandbox triggers from `SANDBOX_APP_REPO`.
- [VERIFY] the project routes (`GET /api/projects/name/<name>`, `POST /api/projects`), `PUT /api/runtime-environments/default/<name>`, and the cron time zone (UTC assumed, Q41).

## History

- 2026-09-24, phase 0: the former `register-preview.sh` created projects `workorders` and `platform-env` and five preview pipelines without triggers or contexts. `register.sh --preview` reproduces that state from the new layout; `--full` replaces it.
