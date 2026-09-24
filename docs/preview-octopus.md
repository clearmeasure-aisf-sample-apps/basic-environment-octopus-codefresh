# Octopus phase 0 preview

The user approved a "phase 0 preview": the Octopus objects of `octopus/terraform` that need no Azure output are created now, before the foundation exists, so the platform space shows the delivery model. Nothing deploys yet. The preview uses the same Terraform files as phase 1, so phase 1 adopts these objects instead of recreating them.

## How to run it

The only Octopus credential is the Space Manager API key of the existing user `AISF-Service-Account`, in the prototype space only (ADR-IR32). Nothing in the preview needs more: no users, custom roles or OIDC identities exist in `octopus/terraform` any more, and teams and assignments of built-in roles need only `TeamCreate` and `TeamEdit`. The key is passed through the environment only.

```bash
OCTOPUS_URL=https://<instance>.octopus.app \
OCTOPUS_API_KEY=<aisf-service-account-api-key> \
OCTOPUS_SPACE_ID=<octopus-space-id> \
STATE_DIR=<directory-outside-the-repo> \
TF_BIN=<path-to-terraform-1.7-or-later> \
PLAN_ONLY=1 \
octopus/preview/apply-preview.sh
```

Run it with `PLAN_ONLY=1` first, then again without it to apply. The script:

1. Copies `octopus/terraform` to a temporary directory. It adds `octopus/preview/preview.tfvars` and a local-backend override that writes `STATE_DIR/octopus-preview.tfstate`.
2. Reads the ID of `infra-nonprod` by name through the REST API and imports that environment. The user created it by hand (R4), so it is not recreated.
3. Plans with `-target` for the approved resources only. It refuses to apply if the plan touches any other resource or deletes anything.
4. On the first real run it makes a second pass. The first pass creates project `workorders`, which comes with an Octopus-created `Default` channel. The second pass looks that channel up by name and imports it, so its rules and Git reference rule can be set.

Reruns are idempotent: a second run plans no changes. The state file contains no secret, but keep it; phase 1 needs it.

## What gets created

| Object | Terraform address | Count |
|---|---|---|
| Environments `tdd`, `uat`, `prod`, `infra-prod` (created) and `infra-nonprod` (imported) | `octopusdeploy_environment.this[*]` | 5 |
| Lifecycles `workorders-standard` (TDD manual, `tdd_auto_deploy = false`), `workorders-hotfix`, `workorders-infrastructure` | `octopusdeploy_lifecycle.*` | 3 |
| Project group `Work Orders` | `octopusdeploy_project_group.work_orders` | 1 |
| Library variable sets `WorkOrders Environment`, `WorkOrders Infrastructure` (empty: their values are Azure outputs) | `octopusdeploy_library_variable_set.*` | 2 |
| Projects `workorders` and `workorders-infrastructure`, version-controlled from `main` of this repo through the stored Git credential | `octopusdeploy_project.*` | 2 |
| Channels `Hotfix` (created) and `Default` (imported, then given its rules) | `octopusdeploy_channel.*` | 2 |
| Feed `docker-hub` (anonymous Docker Hub) | `octopusdeploy_docker_container_registry.docker_hub` | 1 |
| Freeze `prod-weekend-freeze` on `workorders` / `prod` | `octopusdeploy_project_deployment_freeze.prod_weekend` | 1 |
| Seven space teams; `CI Release Publishers` holds the existing user `AISF-Service-Account` (read by name), the others have no members yet | `octopusdeploy_team.this[*]` | 7 |
| Team assignments of built-in roles (approvers: Project Deployer on `uat` / `prod`; CI publishers: Release Creator and Package Publisher) | `octopusdeploy_scoped_user_role.this[*]` | 10 |

In total there are 34 resources: 32 are created and 2 are imported (`infra-nonprod` and the `Default` channel). No user, service account, custom user role or OIDC identity is created (ADR-IR32); `SKIP_SYSTEM_OBJECTS` is obsolete and ignored. The stored objects (`Azure Runtime Provisioner`, `Azure Runtime Provisioning`, `GitHub AISF Sample Apps`, and the Git credential) are only read, as always.

Not created in the preview:
- The Azure OIDC accounts.
- Feed `acr-workorders`.
- Worker pools `k8s-tdd`, `k8s-uat` and `k8s-prod`.
- The variables of both library variable sets.
- The scheduled runbook triggers.

## What will not work until phase 1

- **Deployments and runbooks.** The OCL references `azure-oidc-deploy-*`, `azure-oidc-env-lifecycle-prod`, `acr-workorders` and `k8s-*`, which do not exist yet. The process editor may flag these as unresolved references. Every deployment or runbook run would fail, so run none.
- **Releases.** Codefresh logs in with the `workorders-octopus` context (ADR-IR32), but the images and `acr-workorders` do not exist yet, so a release would find no versions.
- **Variables.** `App.BaseUrl`, `Sql.*`, `KeyVault.Name`, `Environment.Class`, `Terraform.State*` and the other library variable set values are missing. Scripts that use them would receive empty values.
- **People.** Apart from `AISF-Service-Account` in `CI Release Publishers`, the teams have no members, so no one can take the manual interventions yet. The role assignments exist.
- **Stored account.** The `check` on `Azure Runtime Provisioner` is not evaluated in the preview. Its restriction to `infra-nonprod` (R4) is already done by hand.

## How phase 1 adopts these objects

Phase 1 applies the complete `octopus/terraform` (docs/bootstrap.md) with the real, untracked `terraform.tfvars`. This includes `default_channel_import_id` set to the ID of the `Default` channel, taken from `terraform state show 'octopusdeploy_channel.default[0]'` in the preview state. State lives in the foundation's state storage under the key `octopus-space.tfstate` (§7.10).

**Preferred: migrate the preview state.**

```bash
cd octopus/terraform
terraform init \
  -backend-config=resource_group_name=rg-workorders-shared \
  -backend-config=storage_account_name=<tfstate-storage-account> \
  -backend-config=container_name=tfstate \
  -backend-config=key=octopus-space.tfstate \
  -backend-config=use_azuread_auth=true
terraform state push <STATE_DIR>/octopus-preview.tfstate   # only into an empty remote state
terraform plan    # expect creates for the skipped objects and updates only where the real tfvars differ
terraform apply
```

After a successful apply, delete the preview state file and its directory. Delete no Octopus object by hand.

**If the preview state is lost: import by name.** Start with an empty remote state. Write an untracked `imports.tf` with one `import {}` block per object, then delete it after the apply, as for `infra-nonprod` in docs/bootstrap.md step 3. Take the IDs from the Octopus UI or from REST lookups by name:

| Address | Look up |
|---|---|
| `octopusdeploy_environment.this["<name>"]` | `GET /api/<space>/environments?partialName=<name>` |
| `octopusdeploy_lifecycle.<resource>` | `GET /api/<space>/lifecycles?partialName=<name>` |
| `octopusdeploy_project_group.work_orders` | `GET /api/<space>/projectgroups?partialName=Work Orders` |
| `octopusdeploy_library_variable_set.<resource>` | `GET /api/<space>/libraryvariablesets?partialName=<name>` |
| `octopusdeploy_project.<resource>` | `GET /api/<space>/projects?partialName=<name>` |
| `octopusdeploy_channel.default[0]` | Set `default_channel_import_id`; the committed `import` block adopts it |
| `octopusdeploy_channel.hotfix` | `GET /api/<space>/projects/<project-id>/channels` |
| `octopusdeploy_docker_container_registry.docker_hub` | `GET /api/<space>/feeds?partialName=docker-hub` |
| `octopusdeploy_project_deployment_freeze.prod_weekend` | The freeze ID in the project's Freezes page |
| `octopusdeploy_team.this["<name>"]` | `GET /api/<space>/teams?partialName=<name>` |
| `octopusdeploy_scoped_user_role.this["<key>"]` | `GET /api/<space>/teams/<team-id>/scopeduserroles` |

Match every result on the exact name. Then run `terraform plan`: it must show no create for an object that already exists. Only then apply.

If an object cannot be imported, delete it in Octopus and let the phase 1 apply recreate it. The exceptions are the projects: the OCL in Git keeps their process, but their release history is lost if they are deleted.

## Applied result (phase 0, 2026-09-24)

`apply-preview.sh` first ran against the platform space with `SKIP_SYSTEM_OBJECTS=1`, because at that time the design still had service accounts and custom roles, which a Space Manager cannot create. The state file lives outside the repo; if it is lost, phase 1 imports these objects by name.

| Object | State |
|---|---|
| Environments `tdd`, `uat`, `prod`, `infra-nonprod` (imported), `infra-prod` | Created, sort order 1–5 |
| Lifecycles `workorders-standard`, `workorders-hotfix`, `workorders-infrastructure` | Created |
| Project group and projects `workorders`, `workorders-infrastructure` | Created **database-backed** |
| Library variable sets `WorkOrders Environment`, `WorkOrders Infrastructure` | Created, empty |
| Feed `docker-hub`, prod weekend freeze | Created |
| Default channel of `workorders` | Imported, not modified |
| Hotfix channel, Default channel rules | Phase 1 |
| Teams and scoped roles | Next preview run (ADR-IR32): expected plan 17 to add (7 teams, 10 assignments), 0 to change, 0 to destroy |
| Custom user roles, service accounts, OIDC identity | Dropped by ADR-IR32; never created |

Fixes made while applying:
- `environments.tf` sort orders start at 1. Provider 1.20.0 treats `0` as unset, and Octopus then assigns its own value.
- The `infra-nonprod` phase of `workorders-infrastructure` is not optional. Octopus rejects a lifecycle whose phases are all optional.
- Projects stay database-backed in phase 0. Converting a project to version control makes Octopus commit its initial OCL to the default branch, and `main` is protected and already holds the reviewed OCL.
- Channels wait for phase 1. Their version rules name steps (`migrate-database`, `update-argo-cd-image-tags`, `acceptance-tests`) that exist only in the Git-backed process. `MANAGE_CHANNELS=1` re-enables them.

Phase 1 conversion to version control [VERIFY the exact flow on the instance]:
1. Create branch `octopus/convert-<project>` from `main`.
2. Convert each project with that branch as the initial-commit branch.
3. Review the diff between Octopus's generated OCL and the committed `.octopus/<project>` files, keep the committed files, and merge by pull request.
4. Re-run the full `octopus/terraform` apply, including channels, with the Space Manager key (ADR-IR32).

## Applied result, single Space Manager key (ADR-IR32, 2026-09-24)

Re-applied with the provided Space Manager key only, with no System Manager and no new users, keys or custom roles. It added seven space teams and ten built-in role assignments:

| Team | Built-in roles |
|---|---|
| Platform Engineers | Space Manager |
| Release Managers | Project Deployer, Release Creator (workorders) |
| UAT Approvers | Project Deployer (workorders, uat) |
| Prod Approvers | Project Deployer (workorders, prod) |
| SRE On-call | Runbook Consumer (workorders, uat and prod), Project Viewer (group) |
| Developers | Project Viewer (group) |
| CI Release Publishers | Release Creator (workorders), Package Publisher; member: the automation user |

Provider 1.20.0 workarounds, now in the code:
- `teams.tf` sends `users = null` for a team with no members. The provider reads an empty set back as null, which Terraform reports as "inconsistent result after apply".
- `apply-preview.sh` applies with `-parallelism=1`. Concurrent team creates made Terraform panic while saving state; the orphaned teams were deleted and recreated.
