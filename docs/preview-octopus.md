# Octopus live-object migration and full apply (P1-06)

Step P1-06 of ADR-IR34 applies the complete `octopus/terraform` once, on a copy of the phase 0 preview state. The apply keeps the 33 live preview objects with their IDs and history (`moved` blocks and in-place renames, §11.9), then creates everything else. It is run by the main loop with the only Octopus credential, the Space Manager key of `AISF-Service-Account` (ADR-IR32), through `octopus/apply.ps1`. The phase 0 script `octopus/preview/apply-preview.sh` is retired.

## Live objects and what the apply does with them

Live values (2026-09-24): space `Spaces-335`, slug `ai-software-factory-prototype`; automation user `Users-741` (`aisf-service-account`, display name `AISF-Service-Account`). The preview state `octopus-preview.tfstate` (Terraform 1.16.4, provider 1.20.0) is kept outside the repository.

![Level 3: the Octopus space seen from the projects](../design/diagrams/c4-3-octopus-a.png)

*Level 3, the Octopus space seen from the projects. The app projects `workorders` and `sandbox` (one group `app-<app>` per app) have the channels `Default` and `Hotfix`, and `sandbox` adds `Strict`. Step 0 of every app process deploys `platform-wake`, whose one step runs env-wake of `platform-infrastructure` through the REST API and waits. The hourly triggers run env-sleep, and the Terraform runbooks wake the cluster first. `octopus/terraform` creates the space objects, and the stored Git credential reads and commits the OCL in the environment repo.*

![Level 3: the Octopus space seen from the environments](../design/diagrams/c4-3-octopus-b.png)

*Level 3, the Octopus space seen from the environments. The four lifecycles sit above the environments, grouped by tier: nonprod holds tdd, uat and infra-nonprod; prod holds prod, infra-prod and the weekend freezes. Each tier has its Kubernetes worker pools, its Argo CD instance registered by the in-cluster gateway, and its lifecycle OIDC account. Shared by all: the optional app accounts, `hosted-ubuntu`, the three feeds and the seven teams.*

| Live object (ID) | Preview address | New address | Change |
|---|---|---|---|
| Environments `tdd` (`Environments-584`), `uat` (`583`), `prod` (`582`), `infra-nonprod` (`581`), `infra-prod` (`585`) | `octopusdeploy_environment.this[<name>]` | same | Descriptions only |
| Lifecycle `workorders-standard` (`Lifecycles-617`) | `octopusdeploy_lifecycle.workorders_standard` | `octopusdeploy_lifecycle.platform_standard` | Renamed `platform-standard`; TDD automatic (`tdd_auto_deploy`, CAP-OCT-001) |
| Lifecycle `workorders-hotfix` (`Lifecycles-614`) | `.workorders_hotfix` | `.platform_hotfix` | Renamed `platform-hotfix` |
| Lifecycle `workorders-infrastructure` (`Lifecycles-616`) | `.workorders_infrastructure` | `.platform_infrastructure` | Renamed `platform-infrastructure` |
| Set `WorkOrders Environment` (`LibraryVariableSets-281`) | `octopusdeploy_library_variable_set.workorders_environment` | `.platform_environment` | Renamed `Platform Environment`; values added |
| Set `WorkOrders Infrastructure` (`LibraryVariableSets-282`) | `.workorders_infrastructure` | `.platform_infrastructure` | Renamed `Platform Infrastructure`; values added |
| Project group `Work Orders` (`ProjectGroups-734`) | `octopusdeploy_project_group.work_orders` | `octopusdeploy_project_group.app["workorders"]` | Renamed `app-workorders` |
| Project `workorders` (`Projects-943`) | `octopusdeploy_project.workorders` | `octopusdeploy_project.app["workorders"]` | Converted to Git: `.octopus/apps/workorders/workorders` |
| Project `workorders-infrastructure` (`Projects-944`) | `octopusdeploy_project.workorders_infrastructure` | `octopusdeploy_project.platform_infrastructure` | Renamed `platform-infrastructure` (name and slug), group `Platform`, converted to Git: `.octopus/platform-infrastructure` |
| Channel `Default` of `workorders` (`Channels-984`) | `octopusdeploy_channel.default[0]` | none | Forgotten by a `removed` block, never deleted: Default channels are no longer managed |
| Feed `docker-hub` (`Feeds-1807`) | `octopusdeploy_docker_container_registry.docker_hub` | same | Kept |
| Freeze `prod-weekend-freeze` (`DeploymentFreezes-1`) | `octopusdeploy_project_deployment_freeze.prod_weekend` | `...prod_weekend["workorders"]` | Renamed `prod-weekend-freeze-workorders`; `sandbox` gets its own |
| Teams `SRE On-call` (`Teams-248`), `Release Managers` (`249`), `Platform Engineers` (`250`), `Prod Approvers` (`251`), `UAT Approvers` (`252`), `Developers` (`253`), `CI Release Publishers` (`254`) | `octopusdeploy_team.this[<name>]` | same | The automation user joins `UAT Approvers`, `Prod Approvers` and `Platform Engineers` |
| Ten role assignments (`ScopedUserRoles-696` to `705`) | `octopusdeploy_scoped_user_role.this[<key>]` | same keys | Scopes name environments only; project and group scopes are removed |

Created by the same apply: lifecycle `platform-wake`; groups `Platform` and `app-sandbox`; projects `platform-wake` and `sandbox` (Git); channels `Hotfix` of both app projects and `Strict` of `sandbox`; feed `acr-apps`; accounts `azure-platform-lifecycle-{nonprod,prod}` and `azure-workorders-{tdd,uat,prod}`; pools `k8s-tdd`, `k8s-uat`, `k8s-prod`; machine policy `Sleep-tolerant Kubernetes workers`; set `Platform Automation`; the variables of the three sets; the step-scoped `Platform.OctopusApiKey` and the prompted, optional `Octopus.WorkerRegistrationToken` of `platform-infrastructure`; step templates `platform-sod-guard`, `platform-db-backup`, `platform-pin-writer`; freeze `prod-weekend-freeze-sandbox`; triggers `env-sleep-hourly-nonprod` and `env-sleep-hourly-prod`.

Expected first plan: about 45 to add, about 32 to change, 0 to destroy, 9 moved addresses, 1 forgotten. `octopus/apply.ps1` refuses any plan that deletes or replaces something other than a role assignment or a variable.

## Before the apply

1. `terraform/foundation` is applied: identities `id-platform-lifecycle-{nonprod,prod}` and `id-octopus-acr-pull`, their Octopus federated credentials, groups `rg-platform-*` and the state accounts.
2. The ADR-IR34 files are merged to `main` of the environment repository, with the provisioning placeholders of `.octopus/` replaced:

   | Placeholder | Files | Value |
   |---|---|---|
   | `<worker-tools-version>` | `.octopus/platform-infrastructure/runbooks/*.ocl`, `.octopus/platform-wake/deployment_process.ocl`, `.octopus/apps/workorders/workorders/deployment_process.ocl` | A tag of `octopusdeploy/worker-tools` with az, kubectl, kubelogin, jq, curl, python3 and Terraform 1.11 or later (`terraform/tier` and `terraform/apps/tier` require 1.11; `terraform/tier/scripts/aks-token.sh` needs curl and python3) |
   | `<ci-image-version>`, `<ci-image-digest>` | `.octopus/apps/workorders/workorders/variables.ocl` (`StepImage.CiDotnet`) | The pushed `platform/ci-dotnet` image, as `platform/ci-dotnet:<tag>@sha256:<digest>` without the registry host (the container feed `acr-apps` adds it) |
   | `<azure-openai-endpoint>`, `<model-deployment-name>` | same file (`AI.*`) | App #1 settings |
   | `<github-status-app-id>`, `<github-status-app-installation-id>` | same file (`GitHub.*`) | Unused while `GitHub.StatusEnabled` is `False` |

   Record the merge commit: `pre_apply_sha="$(git rev-parse origin/main)"`.
3. The stored Git credential `GitHub clearmeasure-aisf-sample-apps` is restricted to the environment repository (the `check` in `projects.tf` warns otherwise). No project lists Octopus-protected branches; GitHub `main` is not protected (§13).
4. The shell has an Azure login with read access to the subscription, as for `terraform/foundation` (`ARM_CLIENT_ID`, `ARM_CLIENT_SECRET`, `ARM_TENANT_ID`, `ARM_SUBSCRIPTION_ID`, or `az login`). `azure.tf` reads identities and ingress IPs; nothing is written to Azure.

## The apply

The inputs come from the foundation outputs. The Space Manager key goes to the environment only.

```bash
state_dir="$HOME/octopus-state"                 # outside the repository
mkdir -p "$state_dir" && chmod 700 "$state_dir"
cp <preview-state-dir>/octopus-preview.tfstate "$state_dir/octopus-space.tfstate"

fo="$(terraform -chdir=terraform/foundation output -json)"   # after the foundation's backend init
cat > "$state_dir/terraform.tfvars" <<EOF
octopus_url           = "<OCTOPUS_URL>"
octopus_space_id      = "Spaces-335"
octopus_space_slug    = "ai-software-factory-prototype"
azure_tenant_id       = "$(jq -r .subscription.value.tenant_id <<<"$fo")"
azure_subscription_id = "$(jq -r .subscription.value.subscription_id <<<"$fo")"
acr_login_server      = "$(jq -r .registry.value.login_server <<<"$fo")"
tier_state_storage_accounts = {
  nonprod = "$(jq -r .tfstate.value.nonprod.storage_account_name <<<"$fo")"
  prod    = "$(jq -r .tfstate.value.prod.storage_account_name <<<"$fo")"
}
EOF

export OCTOPUS_API_KEY=<the Space Manager key>
# ArgoCD.RepoReadCredential: the first env-apply seeds Argo CD's repository credential from it (terraform/tier).
export TF_VAR_argocd_repo_read_credential="$(jq -cn --arg p "$GITHUB_TOKEN" '{username: "x-access-token", password: $p}')"
STATE_FILE="$state_dir/octopus-space.tfstate" TFVARS="$state_dir/terraform.tfvars" PLAN_ONLY=1 pwsh -NoProfile -File octopus/apply.ps1
STATE_FILE="$state_dir/octopus-space.tfstate" TFVARS="$state_dir/terraform.tfvars" pwsh -NoProfile -File octopus/apply.ps1
```

| Variable | Source |
|---|---|
| `octopus_url` | `<OCTOPUS_URL>`, no trailing slash; must equal `octopus_url` of `terraform/foundation` (the OIDC issuer) |
| `octopus_space_id`, `octopus_space_slug` | The live space; the slug must equal `octopus_space_slug` of `terraform/foundation` (subjects) |
| `azure_tenant_id`, `azure_subscription_id` | Foundation output `subscription` |
| `acr_login_server` | Foundation output `registry.login_server` |
| `tier_state_storage_accounts` | Foundation output `tfstate.<tier>.storage_account_name` |
| Client IDs of the lifecycle, ACR-pull and deploy identities; `Platform.AppsDomain` | Looked up in Azure by `azure.tf`; nothing to pass |
| `OCTOPUS_API_KEY` | The Space Manager key: provider credential, `PlatformWake.OctopusApiKey` and `Platform.OctopusApiKey` |
| `TF_VAR_argocd_repo_read_credential` | The stored org PAT (`GITHUB_TOKEN`) as JSON: sensitive `ArgoCD.RepoReadCredential` of `platform-infrastructure`. Required before the first `env-apply` while `argocd_repo_private` keeps its default: `terraform/tier` writes Secret `argocd/argocd-repo-creds` from it only once. The repository is public (#47), so Argo CD can clone it without the credential; the Secret is the interim that avoids unauthenticated rate limits. Give it on every later run of `apply.ps1` too: without it the plan deletes the variable, and `apply.ps1` refuses such a plan. |

The apply runs with `-parallelism=1`. When its only errors are "Provider produced inconsistent result after apply", `apply.ps1` plans and applies a second time (the converted `workorders` reads its release notes template back from Git; see `projects.tf`). Output `oidc_subjects` lists the subjects the federated credentials must carry; compare it with the foundation output `octopus_federation`.

## After the apply

### C1: conversion commits [VERIFY how Octopus converts a project whose base path already holds OCL]

Converting a project makes Octopus commit its serialized configuration to `main`. If it replaced the reviewed files, restore them in one commit:

```bash
git fetch origin main
git diff --stat "$pre_apply_sha" origin/main -- .octopus/
# Only when the diff is not empty:
git switch --detach origin/main
git restore --source="$pre_apply_sha" --staged --worktree -- \
  .octopus/apps/workorders/workorders .octopus/apps/sandbox/sandbox .octopus/platform-infrastructure .octopus/platform-wake
git commit -m "Restore the reviewed OCL over the Octopus conversion commits (ADR-IR34 §11.9)"
git push origin HEAD:main
```

Then run `apply.ps1` with `PLAN_ONLY=1` again: it must plan no change to any project.

### The first platform-wake release

App releases pin a `platform-wake` release (Deploy a Release step), so one must exist before the first app release, and a new one after every change under `.octopus/platform-wake/`:

```bash
curl -sS -X POST "<OCTOPUS_URL>/api/Spaces-335/releases/create/v1" \
  -H "X-Octopus-ApiKey: $OCTOPUS_API_KEY" -H "Content-Type: application/json" \
  -d '{"SpaceId":"Spaces-335","SpaceIdOrName":"Spaces-335","ProjectName":"platform-wake","GitRef":"refs/heads/main","ReleaseVersion":"0.0.1"}'
```

The response names the release; later releases take `0.0.<n>`.

### V05: scope of `Platform.OctopusApiKey` (Q26)

- If the apply fails on `octopusdeploy_variable.infrastructure_platform_octopus_api_key` because Octopus rejects the runbook and step slugs as scope values, set `infrastructure_key_scope = "unscoped"` in `terraform.tfvars` and apply again.
- Otherwise, read the log of the first `env-sleep` run in `infra-nonprod`, from the hourly trigger or started in the UI. `Sleep.Decision=…` means the key reached `decide-sleep`. "Platform.OctopusApiKey is empty in this step" means the scope form is wrong: apply again with `infrastructure_key_scope = "unscoped"`. Before `env-apply` there is no cluster, so the run stops nothing.

### V06: triggers for runbooks stored in Git (Q27)

`GET /api/Spaces-335/projects/<platform-infrastructure-id>/triggers` lists both triggers. Their `Action.RunbookId` must equal the `Id` of `env-sleep` in `GET /api/spaces/Spaces-335/projects/<platform-infrastructure-id>/refs%2Fheads%2Fmain/runbooks`. If the apply failed on the triggers or the IDs differ, set `env_sleep_triggers_managed = false`, apply again, and create both triggers through the API (`Environments-581` for `nonprod`, `Environments-585` for `prod`):

```bash
curl -sS -X POST "<OCTOPUS_URL>/api/Spaces-335/projecttriggers" \
  -H "X-Octopus-ApiKey: $OCTOPUS_API_KEY" -H "Content-Type: application/json" \
  -d '{"Name":"env-sleep-hourly-nonprod","Description":"Runs env-sleep in infra-nonprod every hour (ADR-IR33).",
       "SpaceId":"Spaces-335","ProjectId":"<platform-infrastructure-id>","IsDisabled":false,
       "Filter":{"FilterType":"CronExpressionSchedule","CronExpression":"0 0 * * * *","Timezone":"UTC"},
       "Action":{"ActionType":"RunRunbook","RunbookId":"<env-sleep-runbook-id>","EnvironmentIds":["Environments-581"],"TenantIds":[],"TenantTags":[]}}'
```

### V07: interventions answered by the automation user (Q45)

Verified at the first intervention the main loop answers, `Approve environment apply` of `env-apply` in P1-07: `GET /api/Spaces-335/interruptions?regarding=<task-id>&pendingOnly=true`, then `PUT /api/Spaces-335/interruptions/<id>/responsible` and `POST /api/Spaces-335/interruptions/<id>/submit` with `{"Notes":"e2e:P1-07","Result":"Proceed"}`. In app deployments, `platform-sod-guard` accepts such an answer only while `Platform.InterventionTestMode` is `true` and the reason is `conformance:<run-id>` or `e2e:<run-id>`.

### State to the azurerm backend

Once the checks pass, move the state to `octopus-space.tfstate` in the global state account, then delete the local copies:

```bash
STATE_FILE="$state_dir/octopus-space.tfstate" MIGRATE_STATE=1 \
  TF_BACKEND_STORAGE_ACCOUNT="$(jq -r .tfstate.value.global.storage_account_name <<<"$fo")" pwsh -NoProfile -File octopus/apply.ps1
```

Later runs leave `STATE_FILE` unset and pass `TF_BACKEND_STORAGE_ACCOUNT`. The state holds the Space Manager key; access to the container protects it.

## Later runs of platform-infrastructure

- `env-apply` in P1-07 and P1-08 installs the Kubernetes workers: start it with a fresh registration token at the prompt `Octopus.WorkerRegistrationToken` (optional; empty sends nothing). `env-destroy` removes only the cluster and what depends on it (`-target=azurerm_kubernetes_cluster.this`); the platform vault, the static IPs and the workspace stay.
- The approvals of `env-apply`, `env-destroy`, `apps-apply` and `db-restore` belong to `Platform Engineers`, which holds the automation user, so the main loop answers them through the API (V07).

## Re-applies

The same command, with no new inputs:
- after `env-apply` in each tier (P1-07, P1-08): `azure.tf` finds `pip-platform-<tier>-ingress` and sets `Platform.AppsDomain` to `<ingress-ip-dashed-<tier>>.sslip.io`;
- after `terraform/apps/grants` for an app (P1-09): the accounts `azure-<app>-<env>` get the real client IDs (until then they carry `00000000-0000-0000-0000-000000000000`, and the check `app_deploy_identities_found` warns);
- after every onboarding or retirement: a new `apps/<app>.yaml` adds its group, project shells, channels, freeze and optional accounts. A removed descriptor fails the plan on `prevent_destroy`; its projects are deleted by hand.

## Task cap

The instance runs at most 5 tasks at once, for all 18 spaces. A deployment that wakes a cluster holds 3: its own task, the `platform-wake` child and `env-wake`. `env-wake` requests a health check only for a worker that is not healthy and never waits for it; the worker machine policy schedules no health checks. `env-sleep` runs hourly in both tiers and ends within seconds. Conformance deployments run one at a time.

## Provider 1.20.0 notes

- Every plan and apply uses `-parallelism=1`: concurrent team creates made Terraform panic.
- A team without members sends `users = null`: an empty set reads back as null.
- Environment sort orders start at 1: `0` counts as unset.
- Project shells never set deployment settings (release notes, connectivity, versioning, guided failure). For a project stored in Git, a change to them makes the provider commit to `main`; they live in `deployment_settings.ocl`.
- `is_version_controlled = true` is declared so that converting a database project plans the right value.

## If the preview state is lost

Import by name instead of copying the state: an untracked `imports.tf` with one `import {}` block per live object, using the new addresses of the table above and the live IDs, then the same `apply.ps1` run with a new `STATE_FILE`. Delete `imports.tf` after the apply. `octopusdeploy_channel.default[0]` is not imported: Default channels are no longer managed.
