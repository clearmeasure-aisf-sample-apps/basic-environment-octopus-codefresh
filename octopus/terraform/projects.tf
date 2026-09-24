# Project groups, the version-controlled projects (workorders, workorders-infrastructure, platform-wake) and the
# scheduled runbook triggers (§7.2, ADR-D7, ADR-IR33).
#
# Order of first use: commit .octopus/workorders, .octopus/workorders-infrastructure and .octopus/platform-wake to
# main (protected) first, then apply. Octopus then reads the existing OCL instead of committing an initial skeleton
# to a protected branch [VERIFY conversion behaviour with pre-existing OCL]. Then create the first platform-wake
# release from main, before the first workorders release (ADR-IR33).
# Deployment settings, process, runbooks and non-sensitive variables live in Git; project-level settings that are
# stored in the database (lifecycle, group, included library variable sets, Git settings) live here.

resource "octopusdeploy_project_group" "work_orders" {
  name        = "Work Orders"
  description = "Work Orders delivery platform: application delivery and environment lifecycle (platform-design §7.2)."
}

# Stored Git credential: looked up by name, never managed. Its repository restriction should be narrowed to the
# environment repo (R3); the Argo CD step also selects it by that restriction.
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
  stored_git_credential_id = one([for c in data.octopusdeploy_git_credentials.stored.git_credentials : c.id if c.name == var.stored_git_credential_name])
}

resource "octopusdeploy_project" "workorders" {
  name                              = "workorders"
  slug                              = "workorders"
  description                       = "Work Orders application delivery: DbUp migration, Argo CD image-tag pin, verification and gates. Releases come from Codefresh workorders/release (§7.7)."
  project_group_id                  = octopusdeploy_project_group.work_orders.id
  lifecycle_id                      = octopusdeploy_lifecycle.workorders_standard.id
  tenanted_deployment_participation = "Untenanted"
  default_guided_failure_mode       = "EnvironmentDefault"
  # App project: never a platform secret (multi-app directive §10), so never WorkOrders Platform Automation.
  included_library_variable_sets = [octopusdeploy_library_variable_set.workorders_environment.id]

  git_library_persistence_settings {
    url                = var.env_repo_url
    git_credential_id  = local.stored_git_credential_id
    base_path          = ".octopus/workorders"
    default_branch     = "main"
    protected_branches = ["main"]
  }

  lifecycle {
    precondition {
      condition     = length(setintersection(toset(local.stored_library_variable_set_ids), toset([octopusdeploy_library_variable_set.workorders_environment.id]))) == 0
      error_message = "Stored library variable sets are included in no project (§5.3, ADR-C10)."
    }
  }
}

# Platform-owned (multi-app directive §10). It does not include WorkOrders Platform Automation: a library set cannot
# be scoped to steps, so the key reaches this project as a step-scoped sensitive variable instead (S5,
# library-variable-sets.tf). The configure-db-principals-<env> and rotate-<env> steps run inside the clusters and
# never receive it.
resource "octopusdeploy_project" "workorders_infrastructure" {
  name                              = "workorders-infrastructure"
  slug                              = "workorders-infrastructure"
  description                       = "Runbooks only: env-plan, env-apply, env-destroy (infra-nonprod), rotate-sql-passwords, provisioner-credential-check (ADR-D10), env-wake and env-sleep (ADR-IR33)."
  project_group_id                  = octopusdeploy_project_group.work_orders.id
  lifecycle_id                      = octopusdeploy_lifecycle.workorders_infrastructure.id
  tenanted_deployment_participation = "Untenanted"
  default_guided_failure_mode       = "EnvironmentDefault"
  included_library_variable_sets = [
    octopusdeploy_library_variable_set.workorders_infrastructure.id,
  ]

  git_library_persistence_settings {
    url                = var.env_repo_url
    git_credential_id  = local.stored_git_credential_id
    base_path          = ".octopus/workorders-infrastructure"
    default_branch     = "main"
    protected_branches = ["main"]
  }

  lifecycle {
    precondition {
      condition = length(setintersection(toset(local.stored_library_variable_set_ids), toset([
        octopusdeploy_library_variable_set.workorders_infrastructure.id,
      ]))) == 0
      error_message = "Stored library variable sets are included in no project (§5.3, ADR-C10)."
    }
  }
}

# Project platform-wake (ADR-IR33, multi-app directive §10): app deployments wake the shared cluster keylessly by
# deploying it with a Deploy a Release step (.octopus/workorders/deployment_process.ocl, step wake-environment).
# Its one step runs env-wake with Platform.OctopusApiKey (.octopus/platform-wake). No runbooks, so the Deployment
# Creator role that app teams hold on it grants nothing else (teams.tf). It needs one release, created from main by
# a Platform Engineer, before the first app release that deploys it; app releases pick the latest by creation time.
resource "octopusdeploy_project_group" "platform" {
  name        = "Platform"
  description = "Platform-owned projects that app projects call without holding platform secrets (multi-app directive §10)."
}

resource "octopusdeploy_project" "platform_wake" {
  name                              = "platform-wake"
  slug                              = "platform-wake"
  description                       = "Platform-owned. App deployments deploy it with a Deploy a Release step to wake the shared cluster without a key; its step runs env-wake of workorders-infrastructure and waits (ADR-IR33)."
  project_group_id                  = octopusdeploy_project_group.platform.id
  lifecycle_id                      = octopusdeploy_lifecycle.platform_wake.id
  tenanted_deployment_participation = "Untenanted"
  # A failed wake fails the app deployment at once; it never waits for a person.
  default_guided_failure_mode = "Off"
  included_library_variable_sets = [
    octopusdeploy_library_variable_set.platform_automation.id,
  ]

  git_library_persistence_settings {
    url                = var.env_repo_url
    git_credential_id  = local.stored_git_credential_id
    base_path          = ".octopus/platform-wake"
    default_branch     = "main"
    protected_branches = ["main"]
  }

  lifecycle {
    precondition {
      condition     = length(setintersection(toset(local.stored_library_variable_set_ids), toset([octopusdeploy_library_variable_set.platform_automation.id]))) == 0
      error_message = "Stored library variable sets are included in no project (§5.3, ADR-C10)."
    }
  }
}

# Scheduled runbook triggers (§7.2). Triggers are not stored in Git (E26) and run config-as-code runbooks from the
# latest commit on the default branch (https://octopus.com/docs/runbooks/config-as-code-runbooks).
# Octopus cron has six fields (seconds first). runbook_id for a config-as-code runbook: the slug is assumed
# [VERIFY the ID form Octopus expects for Git-stored runbooks].

resource "octopusdeploy_project_scheduled_trigger" "rotate_sql_passwords_monthly" {
  count = var.runbook_triggers_enabled ? 1 : 0

  name        = "rotate-sql-passwords-monthly"
  description = "Monthly rotation of the interim SQL passwords in infra-nonprod and infra-prod."
  project_id  = octopusdeploy_project.workorders_infrastructure.id
  space_id    = var.octopus_space_id
  timezone    = var.runbook_trigger_timezone

  cron_expression_schedule {
    cron_expression = "0 0 3 1 * *"
  }

  run_runbook_action {
    runbook_id = "rotate-sql-passwords"
    target_environment_ids = [
      octopusdeploy_environment.this["infra-nonprod"].id,
      octopusdeploy_environment.this["infra-prod"].id,
    ]
  }
}

resource "octopusdeploy_project_scheduled_trigger" "provisioner_credential_check_daily" {
  count = var.runbook_triggers_enabled ? 1 : 0

  name        = "provisioner-credential-check-daily"
  description = "Daily sign-in smoke and expiry warning for the stored provisioner secret (infra-nonprod, phases 1-2)."
  project_id  = octopusdeploy_project.workorders_infrastructure.id
  space_id    = var.octopus_space_id
  timezone    = var.runbook_trigger_timezone

  cron_expression_schedule {
    cron_expression = "0 0 6 * * *"
  }

  run_runbook_action {
    runbook_id             = "provisioner-credential-check"
    target_environment_ids = [octopusdeploy_environment.this["infra-nonprod"].id]
  }
}

# Hourly env-sleep, one trigger per cluster (ADR-IR33): env-sleep-hourly-nonprod runs it in infra-nonprod and
# env-sleep-hourly-prod in infra-prod. The contract's cron 0 * * * * is written with the leading seconds field that
# Octopus requires (https://octopus.com/docs/runbooks/scheduled-runbook-trigger). The time zone does not move an
# hourly schedule; America/Chicago matches Sleep.TimeZone, in which env-sleep evaluates the working window.
# Scheduled runbooks: env-sleep (hourly), rotate-sql-passwords (monthly; wakes its cluster first) and
# provisioner-credential-check (daily; touches no cluster).
resource "octopusdeploy_project_scheduled_trigger" "env_sleep_hourly" {
  for_each = var.runbook_triggers_enabled ? local.infra_environments : {}

  name        = "env-sleep-hourly-${each.value}"
  description = "Hourly env-sleep in ${each.key}: stops aks-workorders-${each.value} outside the working window or when idle, never while a task runs (ADR-IR33)."
  project_id  = octopusdeploy_project.workorders_infrastructure.id
  space_id    = var.octopus_space_id
  timezone    = "America/Chicago"

  cron_expression_schedule {
    cron_expression = "0 0 * * * *"
  }

  run_runbook_action {
    runbook_id             = "env-sleep"
    target_environment_ids = [octopusdeploy_environment.this[each.key].id]
  }
}
