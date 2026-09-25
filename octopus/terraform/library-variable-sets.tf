# Library variable sets (ADR-IR34 decision 24, §7.0, §7.2).
#
# - Platform Environment (renamed from WorkOrders Environment, ID kept; moved.tf): per-environment platform values,
#   included in every app project. Platform values only, never a secret.
# - Platform Infrastructure (renamed from WorkOrders Infrastructure, ID kept): Environment.Class and the Terraform
#   backend of each tier, included in platform-infrastructure. The preview created both sets empty.
# - Platform Automation (new): the sensitive PlatformWake.OctopusApiKey, included in platform-wake only. platform-wake
#   has no project variables, because a parent's passed variables would override them (E53).
# Library-set variables cannot be scoped to steps (Q26), so platform-infrastructure gets the key as a step-scoped
# project variable instead (S5). The only sensitive values managed here come from TF_VAR_platform_octopus_api_key
# and the two optional TF_VAR_* inputs of terraform/tier; Terraform keeps them in octopus-space.tfstate (versions.tf).
#
# Stored `Azure Runtime Provisioning` and `GitHub AISF Sample Apps`: looked up by name only and included in no project
# (preconditions in projects.tf; §5.3).

resource "octopusdeploy_library_variable_set" "platform_environment" {
  name        = "Platform Environment"
  description = "Per-environment platform values for every app project (ADR-IR34): Platform.AppsDomain, Platform.WorkerPool, Platform.Registry, Platform.AutomationUsername, Platform.InterventionTestMode, Platform.SoDMode. Managed by octopus/terraform."
}

resource "octopusdeploy_library_variable_set" "platform_infrastructure" {
  name        = "Platform Infrastructure"
  description = "Environment class and Terraform backend settings of each tier, for project platform-infrastructure. Managed by octopus/terraform."
}

resource "octopusdeploy_library_variable_set" "platform_automation" {
  name        = "Platform Automation"
  description = "Sensitive PlatformWake.OctopusApiKey for the one step of platform-wake (ADR-IR33, ADR-IR34 decision 24). Included in platform-wake only, never in an app project. Managed by octopus/terraform."
}

locals {
  # Platform Environment. Platform.AppsDomain exists only once the tier has a domain (azure.tf: the re-apply after the
  # tier's env-apply); until then App.BaseUrl of an app stays unresolved and its smoke test fails, which is intended.
  platform_environment_variables = merge(
    {
      "Platform.Registry"             = { name = "Platform.Registry", type = "String", value = var.acr_login_server, environment = null }
      "Platform.AutomationUsername"   = { name = "Platform.AutomationUsername", type = "String", value = var.platform_automation_display_name, environment = null }
      "Platform.InterventionTestMode" = { name = "Platform.InterventionTestMode", type = "String", value = tostring(var.platform_intervention_test_mode), environment = null }
      "Platform.SoDMode"              = { name = "Platform.SoDMode", type = "String", value = var.platform_sod_mode, environment = null }
    },
    {
      # Worker pool variable (type WorkerPool, value = pool ID) [VERIFY that a library set accepts this type; fallback: a
      # WorkerPool project variable per app, which the starters would then carry].
      for env in local.app_environments : "Platform.WorkerPool|${env}" => {
        name = "Platform.WorkerPool", type = "WorkerPool", value = octopusdeploy_static_worker_pool.k8s[env].id, environment = env
      }
    },
    {
      for env in local.app_environments : "Platform.AppsDomain|${env}" => {
        name = "Platform.AppsDomain", type = "String", value = local.apps_domains[local.environment_tiers[env]], environment = env
      } if contains(keys(local.apps_domains), local.environment_tiers[env])
    },
  )

  platform_environment_descriptions = {
    "Platform.Registry"             = "Login server of the shared registry <acr-name>.azurecr.io; images live under apps/<app>/<image>."
    "Platform.AutomationUsername"   = "The automation user (ADR-IR32), compared case-insensitively by platform-sod-guard."
    "Platform.InterventionTestMode" = "true: the automation user may answer manual interventions, only with the reason conformance:<run-id> or e2e:<run-id> (ADR-IR34 decision 23)."
    "Platform.SoDMode"              = "single-operator: the deployment creator may approve with a reason; enforce: the creator may not approve. Channel Strict of sandbox sets enforce."
    "Platform.WorkerPool"           = "Shared Kubernetes worker pool k8s-<env> for in-cluster steps (acceptance tests, backups, restores)."
    "Platform.AppsDomain"           = "<apps-domain-<tier>>: apps are served at https://<app>-<env>.<apps-domain-<tier>> (ADR-IR34 decision 21)."
  }

  # Platform Infrastructure: one value per infrastructure environment.
  platform_infrastructure_variables = merge([
    for environment, tier in local.infra_environments : {
      "Environment.Class|${environment}"             = { name = "Environment.Class", environment = environment, value = tier }
      "Terraform.StateResourceGroup|${environment}"  = { name = "Terraform.StateResourceGroup", environment = environment, value = "rg-platform-${tier}-shared" }
      "Terraform.StateStorageAccount|${environment}" = { name = "Terraform.StateStorageAccount", environment = environment, value = var.tier_state_storage_accounts[tier] }
      "Terraform.StateContainer|${environment}"      = { name = "Terraform.StateContainer", environment = environment, value = "tfstate" }
      "Terraform.StateKey|${environment}"            = { name = "Terraform.StateKey", environment = environment, value = "tier-${tier}.tfstate" }
    }
  ]...)
}

resource "octopusdeploy_variable" "platform_environment" {
  for_each = local.platform_environment_variables

  owner_id    = octopusdeploy_library_variable_set.platform_environment.id
  name        = each.value.name
  type        = each.value.type
  value       = each.value.value
  description = local.platform_environment_descriptions[each.value.name]

  dynamic "scope" {
    for_each = each.value.environment == null ? [] : [each.value.environment]
    content {
      environments = [octopusdeploy_environment.this[scope.value].id]
    }
  }
}

resource "octopusdeploy_variable" "platform_infrastructure" {
  for_each = local.platform_infrastructure_variables

  owner_id = octopusdeploy_library_variable_set.platform_infrastructure.id
  name     = each.value.name
  type     = "String"
  value    = each.value.value

  scope {
    environments = [octopusdeploy_environment.this[each.value.environment].id]
  }
}

# PlatformWake.OctopusApiKey: the Space Manager key (ADR-IR32). The one step of platform-wake uses it to run env-wake
# of platform-infrastructure and read that task (ADR-IR33). Rotating the key (R23) means re-applying.
resource "octopusdeploy_variable" "platform_wake_octopus_api_key" {
  owner_id        = octopusdeploy_library_variable_set.platform_automation.id
  name            = "PlatformWake.OctopusApiKey"
  type            = "Sensitive"
  is_sensitive    = true
  sensitive_value = var.platform_octopus_api_key
  description     = "API key of the automation user (ADR-IR32), read only by step run-env-wake of platform-wake: runs env-wake and reads its task. Set from TF_VAR_platform_octopus_api_key."
}

# S5 (ADR-IR33 risk 6): the same key for platform-infrastructure, scoped to the runbooks and steps that call the
# Octopus REST API: wake-environment (env-plan, env-apply, env-destroy, rotate-db-passwords), wait-for-workers-and-gateway
# (env-wake), decide-sleep and stop-cluster (env-sleep). Terraform and in-cluster steps never receive it. The scope
# values are runbook and step slugs of .octopus/platform-infrastructure/runbooks [VERIFY the ID form Octopus expects for
# runbooks and steps stored in Git (Q26, check V05 of docs/preview-octopus.md); fallback
# infrastructure_key_scope = "unscoped", which lets every step of the project resolve the key].
# Sensitive variables of a project stored in Git stay in the Octopus database (E26); this resource writes there.
locals {
  infrastructure_key_processes = ["env-wake", "env-sleep", "env-plan", "env-apply", "env-destroy", "rotate-db-passwords"]
  infrastructure_key_actions   = ["wake-environment", "wait-for-workers-and-gateway", "decide-sleep", "stop-cluster"]
}

resource "octopusdeploy_variable" "infrastructure_platform_octopus_api_key" {
  owner_id        = octopusdeploy_project.platform_infrastructure.id
  name            = "Platform.OctopusApiKey"
  type            = "Sensitive"
  is_sensitive    = true
  sensitive_value = var.platform_octopus_api_key
  description     = "API key of the automation user (ADR-IR32), only for the steps of platform-infrastructure that run env-wake or read tasks through the Octopus REST API (ADR-IR33, S5). Set from TF_VAR_platform_octopus_api_key."

  dynamic "scope" {
    for_each = var.infrastructure_key_scope == "steps" ? [1] : []
    content {
      processes = local.infrastructure_key_processes
      actions   = local.infrastructure_key_actions
    }
  }
}

# Ephemeral inputs of terraform/tier, passed by env-plan, env-apply and env-destroy as TF_VAR_*.
# Octopus.WorkerRegistrationToken is short-lived, so it is a prompted, optional sensitive variable: the person (or the
# main loop, or a conformance run) who starts an env-apply that installs or replaces workers gives a fresh token; an
# empty prompt sends nothing. Its stored value is empty unless TF_VAR_octopus_worker_registration_token is given.
# Scoped to the three runbooks like the key above [VERIFY the scope form, Q26; "unscoped" prompts in every runbook].
resource "octopusdeploy_variable" "infrastructure_worker_registration_token" {
  owner_id        = octopusdeploy_project.platform_infrastructure.id
  name            = "Octopus.WorkerRegistrationToken"
  type            = "Sensitive"
  is_sensitive    = true
  sensitive_value = var.octopus_worker_registration_token == null ? "" : var.octopus_worker_registration_token
  description     = "Bearer token that registers the Kubernetes workers k8s-<env> (terraform/tier helm_release octopus-worker-<env>). Short-lived: give a fresh one at the prompt of an env-apply that installs or replaces a worker."

  prompt {
    label       = "Worker registration token"
    description = "Only when this run installs or replaces an Octopus Kubernetes worker: a fresh registration token (Infrastructure, Workers, Add worker). Leave empty otherwise."
    # No display settings: the provider accepts only text, checkbox and select controls, and the variable's
    # Sensitive type already keeps the value out of logs [VERIFY that the prompt masks the input].
    is_required = false
  }

  dynamic "scope" {
    for_each = var.infrastructure_key_scope == "steps" ? [1] : []
    content {
      processes = ["env-plan", "env-apply", "env-destroy"]
    }
  }
}

# Optional: set here only when TF_VAR_argocd_repo_read_credential is given; otherwise a Platform Engineer sets it in the
# project (Variables) before the first env-apply. Once set here, every later apply passes it again: a run without it
# would delete the variable, and octopus/apply.ps1 refuses that plan.
resource "octopusdeploy_variable" "infrastructure_argocd_repo_read_credential" {
  count = var.argocd_repo_read_credential == null ? 0 : 1

  owner_id        = octopusdeploy_project.platform_infrastructure.id
  name            = "ArgoCD.RepoReadCredential"
  type            = "Sensitive"
  is_sensitive    = true
  sensitive_value = var.argocd_repo_read_credential
  description     = "JSON read credential of the environment repository for Argo CD (terraform/tier); the stored PAT until R11."
}

# Stored sets: looked up by name only, never managed. Zero matches is accepted because R5 allows deleting them after
# phase 2; more than one match is an error.
data "octopusdeploy_library_variable_sets" "stored" {
  for_each = toset(var.stored_library_variable_set_names)

  partial_name = each.key
  take         = 10

  lifecycle {
    postcondition {
      condition     = length([for s in self.library_variable_sets : s if s.name == each.key]) <= 1
      error_message = "More than one library variable set is named '${each.key}'."
    }
  }
}

locals {
  stored_library_variable_set_ids = flatten([
    for name, lookup in data.octopusdeploy_library_variable_sets.stored : [
      for s in lookup.library_variable_sets : s.id if s.name == name
    ]
  ])
}
