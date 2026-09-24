# Project groups and project shells (ADR-IR34 §7.0, decisions 19 and 26). Everything a project stores in Git (process,
# runbooks, deployment settings, non-sensitive variables) lives under .octopus/; this file sets what Octopus keeps in
# its database: group, lifecycle, included library variable sets, Git settings, disabled state.
#
# Config as code (ADR-IR34 §11.7.2, §11.9):
# - Repository <ENV_REPO_URL> through the stored Git credential; default branch main.
# - No Octopus-protected branches. Decision (single operator, §13): the earlier conversion failed only because the
#   Octopus-side protected-branch list named main, which makes Octopus refuse its own conversion commit on main.
#   GitHub branch protection is what guards main; Octopus protected branches guard only the Octopus UI anyway.
#   protected_branches is left unset on purpose: the provider then sends an empty list, and an explicit empty set
#   risks the "inconsistent result after apply" that empty sets cause in provider 1.20.0 (teams.tf). The check
#   no_octopus_protected_branches reports any protected branch added later in the UI.
# - Order: the OCL is committed to main before the apply, so each conversion finds its folder filled. Whether
#   Octopus adopts existing OCL or commits its own serialization over it is [VERIFY]: docs/preview-octopus.md gives
#   the check after the apply and the one-commit restore.
# - For projects stored in Git the provider writes deployment settings (guided failure, release notes, connectivity,
#   versioning) into Git when their Terraform values change, so these shells never set them: they live in
#   deployment_settings.ocl, and every environment has guided failure off (environments.tf). After converting a
#   database project the provider reads those settings back from Git; release_notes_template of workorders then
#   differs from the planned empty value, which provider 1.20.0 reports as "inconsistent result after apply". The new
#   value is saved and a second apply converges; octopus/apply.sh runs that second pass by itself.
# - depends_on: every object the OCL names (pools, feeds, accounts, teams, platform-wake) exists before a project is
#   converted or created; channels follow their project, so no process names a channel slug.
# - prevent_destroy: a plan that would delete a project fails; a retired app's projects are deleted by hand after
#   their Applications and federated credentials (the pragmatist's retire steps).

resource "octopusdeploy_project_group" "app" {
  for_each = local.apps

  name        = "app-${each.key}"
  slug        = "app-${each.key}"
  description = "Octopus projects of app ${each.key} (apps/${each.key}.yaml)."
}

resource "octopusdeploy_project_group" "platform" {
  name        = "Platform"
  slug        = "platform"
  description = "Platform-owned projects: platform-infrastructure (runbooks) and platform-wake (keyless wake for app deployments)."
}

# Stored Git credential: looked up by name, never managed.
data "octopusdeploy_git_credentials" "stored" {
  name = var.stored_git_credential_name
  take = 10

  lifecycle {
    postcondition {
      condition     = length([for c in self.git_credentials : c if c.name == var.stored_git_credential_name]) == 1
      error_message = "Expected exactly one Git credential named '${var.stored_git_credential_name}'. It is stored by the user and never created here."
    }
  }
}

locals {
  stored_git_credential    = one([for c in data.octopusdeploy_git_credentials.stored.git_credentials : c if c.name == var.stored_git_credential_name])
  stored_git_credential_id = local.stored_git_credential.id
}

# R3: the credential that commits config as code and pins is restricted to the environment repository.
check "stored_git_credential_restricted" {
  assert {
    condition = try(
      local.stored_git_credential.repository_restrictions.enabled &&
      contains([for url in local.stored_git_credential.repository_restrictions.allowed_repositories : trimsuffix(lower(url), ".git")], trimsuffix(lower(var.env_repo_url), ".git")),
      false
    )
    error_message = "Git credential '${var.stored_git_credential_name}' is not restricted to ${var.env_repo_url} (R3). Restrict it in Octopus (Deploy, Git Credentials); Terraform never manages it."
  }
}

resource "octopusdeploy_project" "app" {
  for_each = local.app_projects

  name                              = each.key
  slug                              = each.key
  description                       = "App ${each.value.app}: config as code at .octopus/apps/${each.value.app}/${each.key}/ (ADR-IR34). Releases come from the app's Codefresh release pipeline."
  project_group_id                  = octopusdeploy_project_group.app[each.value.app].id
  lifecycle_id                      = local.app_lifecycle_ids[each.value.lifecycle]
  tenanted_deployment_participation = "Untenanted"
  is_disabled                       = each.value.frozen
  # App project: platform values only, never a platform secret (ADR-IR34 decision 24).
  included_library_variable_sets = [octopusdeploy_library_variable_set.platform_environment.id]

  # Declared, so that converting a database project plans true instead of its prior false (provider 1.20.0 reports
  # an inconsistent result otherwise).
  is_version_controlled = true

  git_library_persistence_settings {
    url               = var.env_repo_url
    git_credential_id = local.stored_git_credential_id
    base_path         = ".octopus/apps/${each.value.app}/${each.key}"
    default_branch    = "main"
  }

  depends_on = [
    octopusdeploy_static_worker_pool.k8s,
    octopusdeploy_azure_container_registry.acr_apps,
    octopusdeploy_docker_container_registry.docker_hub,
    octopusdeploy_azure_openid_connect.app,
    octopusdeploy_team.this,
    octopusdeploy_project.platform_wake,
  ]

  lifecycle {
    prevent_destroy = true

    precondition {
      condition     = length(setintersection(toset(local.stored_library_variable_set_ids), toset([octopusdeploy_library_variable_set.platform_environment.id]))) == 0
      error_message = "Stored library variable sets are included in no project (§5.3)."
    }
  }
}

# Platform-owned runbooks project, renamed from workorders-infrastructure with its ID kept (moved.tf, §11.9). The
# rename changes the OIDC subject to space:<octopus-space-slug>:project:platform-infrastructure:environment:infra-<tier>,
# which terraform/foundation's federated credentials already use (§7.0).
resource "octopusdeploy_project" "platform_infrastructure" {
  name                              = "platform-infrastructure"
  slug                              = "platform-infrastructure"
  description                       = "Platform runbooks: env-plan, env-apply, env-destroy (infra-nonprod), apps-plan, apps-apply, rotate-db-passwords, env-wake, env-sleep (ADR-IR33, ADR-IR34)."
  project_group_id                  = octopusdeploy_project_group.platform.id
  lifecycle_id                      = octopusdeploy_lifecycle.platform_infrastructure.id
  tenanted_deployment_participation = "Untenanted"
  included_library_variable_sets    = [octopusdeploy_library_variable_set.platform_infrastructure.id]

  # Declared, so that converting a database project plans true instead of its prior false (provider 1.20.0 reports
  # an inconsistent result otherwise).
  is_version_controlled = true

  git_library_persistence_settings {
    url               = var.env_repo_url
    git_credential_id = local.stored_git_credential_id
    base_path         = ".octopus/platform-infrastructure"
    default_branch    = "main"
  }

  depends_on = [
    octopusdeploy_azure_openid_connect.platform_lifecycle,
    octopusdeploy_static_worker_pool.k8s,
    octopusdeploy_docker_container_registry.docker_hub,
    octopusdeploy_team.this,
  ]

  lifecycle {
    prevent_destroy = true

    precondition {
      condition     = length(setintersection(toset(local.stored_library_variable_set_ids), toset([octopusdeploy_library_variable_set.platform_infrastructure.id]))) == 0
      error_message = "Stored library variable sets are included in no project (§5.3)."
    }
  }
}

# Project platform-wake (ADR-IR33, ADR-IR34 decision 17): app deployments deploy it with a Deploy a Release step to wake
# their cluster keylessly. Its one step runs env-wake with PlatformWake.OctopusApiKey from library variable set Platform
# Automation. No runbooks. Its first release is created after the apply (docs/preview-octopus.md).
resource "octopusdeploy_project" "platform_wake" {
  name                              = "platform-wake"
  slug                              = "platform-wake"
  description                       = "Platform-owned. App deployments deploy it (Deploy a Release) to wake their cluster without a key; its step runs env-wake of platform-infrastructure and waits."
  project_group_id                  = octopusdeploy_project_group.platform.id
  lifecycle_id                      = octopusdeploy_lifecycle.platform_wake.id
  tenanted_deployment_participation = "Untenanted"
  included_library_variable_sets    = [octopusdeploy_library_variable_set.platform_automation.id]

  # Declared, so that converting a database project plans true instead of its prior false (provider 1.20.0 reports
  # an inconsistent result otherwise).
  is_version_controlled = true

  git_library_persistence_settings {
    url               = var.env_repo_url
    git_credential_id = local.stored_git_credential_id
    base_path         = ".octopus/platform-wake"
    default_branch    = "main"
  }

  depends_on = [octopusdeploy_docker_container_registry.docker_hub]

  lifecycle {
    prevent_destroy = true

    precondition {
      condition     = length(setintersection(toset(local.stored_library_variable_set_ids), toset([octopusdeploy_library_variable_set.platform_automation.id]))) == 0
      error_message = "Stored library variable sets are included in no project (§5.3)."
    }
  }
}

locals {
  platform_project_ids = {
    "platform-infrastructure" = octopusdeploy_project.platform_infrastructure.id
    "platform-wake"           = octopusdeploy_project.platform_wake.id
  }
}

check "no_octopus_protected_branches" {
  assert {
    condition = alltrue([
      for p in concat(values(octopusdeploy_project.app), [octopusdeploy_project.platform_infrastructure, octopusdeploy_project.platform_wake]) :
      length(coalesce(try(one(p.git_library_persistence_settings).protected_branches, null), toset([]))) == 0
    ])
    error_message = "A project stored in Git has Octopus-protected branches. The single-operator platform uses none (ADR-IR34 §11.9): remove them in Settings, Version Control."
  }
}
