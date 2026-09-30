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
  commits, and its check `stored_git_credential_restricted` stays. Follow-up #58 (child of #42) does not wait for
  Octopus: see "Retiring the stored credential (#58)" below.
- Recorded research finding of #58 (2026-09-30; the documents are silent, they do not state that a connection is
  unsupported): the step documentation (Argo CD steps, Update Argo CD Application image tags), the Git credentials
  documentation, the Argo CD troubleshooting page, the release notes 2026.2 (10 Jun 2026) and 2026.3 (14 Sep 2026;
  "sourcing from GitHub Connections in Process and Project Templates" is about templates, not Argo CD) and provider
  1.20.0 (resource `octopusdeploy_git_credential`: `username`, `password`, `type`, `repository_restrictions`) name no
  GitHub App connection for the Argo CD image-tag step. The owner can settle it by asking Octopus support.
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
   *always*) for the config-as-code commits and the GitHub App `aisf-pin-writer` (mode *always*) for the pin commits of
   the pin writer (issue #58, section below). While the stored credential still writes the pin commits, its machine
   user stays a bypass actor as well; remove it only when #58 stage 2 has landed. Do not add a repository role.
2. Verify: run one deployment of `workorders` to `tdd` (a release of any app) and check that the pin commit lands.

   ```powershell
   pwsh -NoProfile -File .claude/skills/feature-loop/board.ps1 deploy <merge-sha>
   gh api repos/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/commits/main --jq '.commit.message'
   ```

   Expected: the `update-argo-cd-image-tags` step succeeds, the newest commit on `main` is `Pin <project> <release> in tdd
   (Deployments-...)`, and the task log has no `protected branch`, `rule violations` or `bypass` error. A rejected push
   names the ruleset: then the bypass actor is missing or names the wrong identity.
3. The bot-path audit (CAP-OCT-012) still checks after the fact that the pin commit changed pin fields only.

## Retiring the stored credential (#58, owner goal #40: no PATs)

The Argo CD image-tag step needs a stored Git credential and Octopus documents no GitHub App connection for it (finding
above), so #58 replaces the step by the pin writer, step template `platform-pin-writer`
(`octopus/step-templates/pin-writer.ps1`), which mints a one-hour installation token of a dedicated GitHub App per run.
Design: `design/platform-design.md` ADR-IR34 decision 20, section 5.2 and TB6.

- **Stage 1 (merged with the pull request that added this section; nothing changes in Octopus).** The writer takes
  `PinWriter.AppId` and `PinWriter.InstallationId` (template parameters) and the sensitive variable
  `PinWriter.AppPrivateKey`; it signs a JWT with the key in memory, asks `POST /app/installations/{id}/access_tokens`
  for a token limited to this repository with `contents: write` and `metadata: read`, and pushes with it. An unset
  input or a refused or unanswered exchange fails the step before any commit; there is no PAT fallback. Author
  (`octopus-argocd-pin-bot`), message shape and Kustomize-only behaviour are unchanged, so `PLATFORM_BOT_AUTHORS` and the
  bot-path audit need no edit. The OCL of `workorders` and `sandbox`, the stored credential, its check and the rollback
  are untouched: the Octopus step stays active.
- **Stage 2 (a child of #58; its pull request merges only after the owner steps below, because Git-backed OCL means
  merge = live).** Replace step `update-argo-cd-image-tags` in both app processes by a `platform-pin-writer` step (same
  position, `environments = ["tdd","uat","prod"]`, worker pool `Platform.WorkerPool`), retire the stored credential and
  its Terraform, and make `octopus_github_app_connection_id` required (the rollback to a PAT disappears; the rollback for
  pins is reverting the OCL pull request).

Owner steps (OWNER-ONLY; never executed by an agent or a script):

1. Create the GitHub App `aisf-pin-writer`: Repository permissions Contents read and write, Metadata read, nothing else;
   no webhook; install it on `clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh` only (not the app
   repository); generate the private key.
2. In Octopus store the key as the sensitive variable `PinWriter.AppPrivateKey` and the App id and installation id as
   `PinWriter.AppId` and `PinWriter.InstallationId` (the app project or the platform library set); never in Git. Apply
   `octopus/apply.ps1` so the step template gets its two new parameters.
3. Add the App `aisf-pin-writer` as a bypass actor (mode *always*) of ruleset `main-protection`, next to the Octopus
   Deploy GitHub App (section above); later remove the machine user's bypass.
4. After stage 2 has merged and a tdd deployment has proved that the pin lands (`board.ps1 deploy <sha>`, then the
   bot-path audit): delete the stored Git credential `GitHub clearmeasure-aisf-sample-apps` in Octopus and revoke the
   machine user's token. Rotate the App key per [credential-rotation.md](credential-rotation.md), section "GitHub App
   aisf-pin-writer".
5. Optional: ask Octopus whether the Argo CD step will accept a GitHub App connection. If that ships, stage 2 can flip
   the step to the connection instead, and the pin writer stays the documented fallback.

## Related

- [credential-rotation.md](credential-rotation.md) section 2b, [public-repository.md](public-repository.md), TB6 in
  [../tool-boundaries.md](../tool-boundaries.md).
