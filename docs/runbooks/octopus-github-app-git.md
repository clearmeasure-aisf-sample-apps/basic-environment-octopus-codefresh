# Octopus Git access through the GitHub App connection (R3, issue #42)

Owner-only where marked. The Octopus Deploy GitHub App is installed on the org (all repositories) and the Octopus GitHub
connection `GitHubAppConnections-41` exists (Octopus: Library, Git connections). Nothing on this page is done by a script
or an agent: Terraform changes reach Octopus only through the normal `octopus/apply.ps1` runbook after the pull request
is merged. Design: `design/platform-design.md` section 5.2 (R3 rows); boundary TB6.

## What changed in code

| Where | Change |
|---|---|
| `octopus/terraform/projects.tf` | The four config-as-code projects (`workorders`, `sandbox`, `platform-infrastructure`, `platform-wake`) set `git_github_app_persistence_settings` (`github_connection_id`) instead of `git_library_persistence_settings` (`git_credential_id`). Check `projects_git_via_github_app` reports a project that drifts to another kind or connection. |
| `octopus/terraform/variables.tf` | `octopus_github_app_connection_id` (default `GitHubAppConnections-41`; validated; empty string is the rollback). The provider has no data source for connections, so the ID is a value. |
| `octopus/apply.ps1` | The plan output lists each project whose Git persistence changes kind, for example `Git credential -> GitHub App connection`; a delete or replace of a project is still refused. |

## Provider finding (recorded, not guessed)

`OctopusDeploy/octopusdeploy` 1.20.0, the current release (2026-09-21), supports a GitHub App connection for project Git
persistence: block `git_github_app_persistence_settings` of `octopusdeploy_project` with `url`, `github_connection_id`,
`base_path`, `default_branch`, `protected_branches`. Support arrived in provider 1.10.0 (release note "fix: git
persistence settings for password auth and github app", #177); 1.8.0 added the GitHub App as a source for git dependency
resources. Evidence: `docs/resources/project.md`, `octopusdeploy_framework/resource_project_expand.go` and
`resource_project_flatten.go`, and the unit tests `TestExpandGitGitHubAppPersistenceSettings` and
`TestProcessPersistenceSettings_GitHubApp` at tag v1.20.0. The pin stays 1.20.0; no newer release exists.

What the provider does not settle, and Octopus documentation does not either:

- Whether the Argo CD image-tag step can commit with a GitHub App connection. Octopus documents that the Argo CD steps
  choose their Git credential from the Git credentials library by repository restriction; it names no connection there.
  So the stored credential `GitHub clearmeasure-aisf-sample-apps` (restricted to this repository, R3) stays for the pin
  commits, and its check `stored_git_credential_restricted` stays. Follow-up #58 (child of #42) retires it when
  Octopus supports the connection.
- Whether an operation that Octopus starts without an interactive user commits through the connection. Octopus says a
  connection lets users "commit as their GitHub users"; the first `PLAN_ONLY` and apply below are the proof.

## Owner steps (OWNER-ONLY)

1. Plan only, from an operator session with the state and variables of `docs/preview-octopus.md`:

   ```powershell
   $env:PLAN_ONLY = '1'; pwsh -NoProfile -File octopus/apply.ps1
   ```

   Expected: four `octopusdeploy_project` updates in place (three resources, `app` twice), the lines `Git persistence of
   octopusdeploy_project.<name>: Git credential -> GitHub App connection`, no delete or replace, output
   `git_persistence.method = github-app`.
2. Apply through the normal runbook (`octopus/apply.ps1` without `PLAN_ONLY`). A second pass for the known "inconsistent
   result" errors of provider 1.20.0 is normal.
3. In Octopus, open Settings, Version Control of `platform-wake` and confirm the connection `GitHubAppConnections-41` and
   a working test of the connection; then make a trivial change to a process step of `platform-wake` and confirm the
   commit reaches `main` (or the Octopus-created branch) of this repository.
4. If step 3 fails because the App cannot act without an interactive user, roll back: set
   `octopus_github_app_connection_id = ""` in `terraform.tfvars` and apply. The projects return to the stored credential
   and nothing else moves. Record the finding in issue #42 and design section 5.2.

## `main-protection` bypass for pin commits (OWNER-ONLY; never edited by an agent)

Pin commits go straight to `main`, so the identity that writes them must be a bypass actor of ruleset `main-protection`
once the ruleset requires a pull request (target state, `docs/owner/public-repo-checklist.md` step 3).

1. Repository, Settings, Rules, Rulesets, `main-protection`, Bypass list: add the Octopus Deploy GitHub App (mode
   *always*). While the stored credential still writes the pin commits (issue #58), its machine user stays a bypass
   actor as well; remove it only when #58 lands. Do not add a repository role.
2. Verify: run one deployment of `workorders` to `tdd` (a release of any app) and check that the pin commit lands.

   ```powershell
   pwsh -NoProfile -File .claude/skills/feature-loop/board.ps1 deploy <merge-sha>
   gh api repos/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/commits/main --jq '.commit.message'
   ```

   Expected: the `update-argo-cd-image-tags` step succeeds, the newest commit on `main` is `Pin <project> <release> in tdd
   (Deployments-...)`, and the task log has no `protected branch`, `rule violations` or `bypass` error. A rejected push
   names the ruleset: then the bypass actor is missing or names the wrong identity.
3. The bot-path audit (CAP-OCT-012) still checks after the fact that the pin commit changed pin fields only.

## Related

- [credential-rotation.md](credential-rotation.md) section 2b, [public-repository.md](public-repository.md), TB6 in
  [../tool-boundaries.md](../tool-boundaries.md).
