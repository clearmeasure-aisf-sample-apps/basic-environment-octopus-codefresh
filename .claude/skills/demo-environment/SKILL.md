---
name: demo-environment
description: >
  Provision a complete demo DevOps and GitOps environment for a .NET system on Azure: a system-level GitOps
  repository (environments, Bicep infrastructure, deployed versions) and the first app repository, a snapshot of the
  bootcamp work-order app (ClearMeasureLabs/bootcamp-palermo-workorders), wired to GitHub Actions for CI, Octopus
  Deploy for releases and deployments, and two Azure resource groups (nonprod, prod) on free tiers (Container Apps
  consumption, Azure SQL free offer). The first push of the system repository configures Octopus and creates the
  first Azure environment through the pipeline. Also drives the demo's progression steps (add an environment, turn a
  capability on, promote) and tears a demo down. Use when asked to "create a demo environment", "provision a new
  system for a class", "set up GitOps for the bootcamp app", "add uat/prod to the demo", or "tear down the demo".
---

# Demo environment (system GitOps repository + first app repository)

Builds one demo system, named by its **slug**. The design, the naming rules, the identities and the known limits are in `reference.md` next to this file; read the section a situation needs. The scripts are in `scripts/`. Run them with `pwsh -NoProfile -File`, each with `-Config <demo file>`. They are safe to re-run: each phase records its results (identifiers only) in `~/.demo-environment/<slug>/state.json`, and a repeated phase finds what it created before.

## Rules

- **No secret in a file, an argument or the chat.** The operator's logins (`az`, `gh`) and `OCTOPUS_ADMIN_API_KEY` stay in the shell. The scripts read them from there and never print them.
- **The pipeline creates the environments, not the skill.** The skill creates only layer 0 (the Azure seed) and the Octopus foothold, then pushes. Never apply `infra/` by hand to "help" a first run. If a run fails, fix the cause and re-run the workflow or the deployment.
- **Every later change is a pull request** to the system repository: `add-demo-environment.ps1`, `set-demo-capability.ps1`, or one by hand. Only Octopus commits to `main` directly, and only `environments/<env>/versions.json`.
- **Stop and report** when a prerequisite check fails, when a phase throws, or when a pipeline run or deployment fails twice for the same reason. Quote the failing step and the link; don't work around it.

## Inputs

1. **Demo file.** A copy of `demo.example.json`, kept outside both repositories, for example `~/demos/acme.json`. Fields:
   - `slug`: 3 to 10 lowercase letters and digits.
   - `name`
   - `githubOrg`
   - `azure`: `subscriptionId`, `location`, and `resourceGroups.nonprod` / `resourceGroups.prod`.
   - `octopus`: `url` (no trailing slash) and `spaceName`.
   - `bootcamp`: `repository` and `ref`.
   - `deployable`: `name`, `port`, `healthPath`.
   - `plannedEnvironments`: every environment the demo may reach, with its tier.
   - `initialEnvironments`: usually `["tdd"]`.
   - `board`: `true` creates a GitHub Project.
2. **Logins in the operator's shell:**
   - `gh auth login --web --scopes admin:org,repo,workflow,project,delete_repo` as an org owner;
   - `az login` as subscription Owner;
   - `export OCTOPUS_ADMIN_API_KEY=...`, the key of an Octopus administrator (system rights `SpaceCreate` and `UserEdit`).

## Phases

| # | Command (`scripts/…`) | Creates | Done when |
|---|---|---|---|
| 0 | `test-demo-prerequisites.ps1` | Nothing (read-only) | Every line is `PASS` |
| 1 | `new-octopus-foothold.ps1` | Space, service account `<slug>-github`, two GitHub OIDC identities on it | State `octopus` has `spaceId`, `spaceSlug`, `serviceAccountId` |
| 2 | `new-demo-seed.ps1` | Resource providers; seed: both resource groups, ACR (Basic), Terraform state account, identities with federated credentials and grants for every planned environment | State `seed` has the registry and identities |
| 3 | `new-system-repository.ps1` | `<org>/<slug>-system` (public): variables, environments `octopus` / `azure-read`, secret `OCTOPUS_GITHUB_TOKEN`, push of `main`, ruleset, board | Workflow `system` runs |
| 3a | `get-demo-status.ps1 -Wait -Project system -Environment tdd` | Nothing; waits | `PASS <slug>-system 1.0.1 in tdd`. The placeholder app answers over HTTPS |
| 4 | `new-app-repository.ps1` | `<org>/<slug>-workorders` (public): bootcamp snapshot, adapted workflows, environment `release`, variables, push of `master`, ruleset | Workflows `Build`, then `Release`, run |
| 4a | `get-demo-status.ps1 -Wait -Project deployable -Environment tdd -TimeoutMinutes 90` | Nothing; waits for Build, Release and the deployment | `PASS <slug>-<deployable> 2.4.1 in tdd`. `/_healthcheck` answers |

After phase 3, watch the first run at `https://github.com/<org>/<slug>-system/actions`:
1. `octopus-apply` creates the Octopus environments, lifecycle, accounts, projects and variables.
2. `system-release` pushes package `<slug>-system.1.0.1` and creates the release.
3. Octopus deploys that release to `tdd`. Its step "Apply environment" creates `stack-<slug>-tdd`: Key Vault, SQL server with a free-offer database, Container Apps environment, and the app running its placeholder image.

Phase 4 waits for phase 3a, because the first app release deploys to an environment that has to exist.

Report after phase 4a, with links:
- both repositories;
- the board;
- the Octopus space;
- the TDD URL.

## Progression (the demo's later steps, each a change)

| Step | Command (`scripts/…`) | Then |
|---|---|---|
| Add UAT | `add-demo-environment.ps1 -Environment uat [-Merge]` | `invoke-demo-promotion.ps1 -Project system -Environment uat -Wait`, then `-Project deployable` |
| Add Prod | `add-demo-environment.ps1 -Environment prod [-Merge]` | Same two promotions, to `prod` |
| Telemetry in TDD | `set-demo-capability.ps1 -Environment tdd -Capability telemetry [-Merge]` | The new `<slug>-system` release reaches TDD by itself |
| App change | A pull request to the app repository; `Build result` must pass | Merge: Build, then Release, then Octopus pins and deploys to TDD |
| Status | `get-demo-status.ps1` | One line per project and environment |

Without `-Merge`, the pull request stays open for the audience to review: the `env-checks` preview in its job summary lists the Azure changes. A new capability needs its module first. `reference.md` ("Capabilities") describes how to add one.

## Teardown

```
remove-demo-environment.ps1 -Config <file>            # prints what it would delete
remove-demo-environment.ps1 -Config <file> -Delete    # stacks, resource groups, vault purge, repos, board, space, state
```

Add `-KeepSpace` when the Octopus space is shared. Revoking the operator's own logins is left to the operator.

## Verify before reporting success

- `get-demo-status.ps1` shows `Success` for `<slug>-system` and `<slug>-<deployable>` in every initial environment.
- The TDD URL plus `/_healthcheck` answers 200.
- `environments/tdd/versions.json` on `main` holds the deployed version, committed by Octopus.
- The latest `drift` run (start it with `gh workflow run drift --repo <org>/<slug>-system`) is green.

The first live run of a new template version also confirms the points listed in `reference.md` under "Verify on the first live run". Report each one as confirmed or not.
