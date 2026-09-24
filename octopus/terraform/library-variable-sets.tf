# Library variable sets (§7.2).
#
# New, managed here from terraform.tfvars:
# - `WorkOrders Environment` (included by project workorders): per-environment endpoints and resource names.
# - `WorkOrders Infrastructure` (included by project workorders-infrastructure): environment class and the
#   Terraform backend settings that env-plan, env-apply and env-destroy pass through -backend-config.
# - `WorkOrders Platform Automation` (ADR-IR33): the sensitive Platform.OctopusApiKey only, set from
#   TF_VAR_platform_octopus_api_key at apply time. Included by the platform-owned projects workorders-infrastructure
#   and platform-wake only, never by an app project (multi-app directive §10).
# Values that §7.2 defines by formula are derived: App.InternalUrl, Azure.ResourceGroup, Sql.ServerFqdn,
# Sql.MigratorUser, Sql.AcceptanceUser (tdd only), Environment.Class and Terraform.StateKey.
# The only sensitive value managed here is Platform.OctopusApiKey; Terraform keeps it in the state (versions.tf).
#
# Stored `Azure Runtime Provisioning` and `GitHub AISF Sample Apps`: looked up by name only, and included in no
# project (preconditions in projects.tf; ADR-C10, §5.3).

resource "octopusdeploy_library_variable_set" "workorders_environment" {
  name        = "WorkOrders Environment"
  description = "Per-environment endpoints and Azure resource names for project workorders (tdd, uat, prod). Managed by octopus/terraform."
}

resource "octopusdeploy_library_variable_set" "workorders_infrastructure" {
  name        = "WorkOrders Infrastructure"
  description = "Environment class and Terraform backend settings for project workorders-infrastructure. Managed by octopus/terraform."
}

locals {
  workorders_environment_variables = merge([
    for env, v in var.workorders_environment : merge(
      {
        "App.BaseUrl|${env}"         = { name = "App.BaseUrl", environment = env, value = v.app_base_url }
        "App.InternalUrl|${env}"     = { name = "App.InternalUrl", environment = env, value = "http://ui-server.workorders-${env}.svc.cluster.local:8080" }
        "Azure.ResourceGroup|${env}" = { name = "Azure.ResourceGroup", environment = env, value = "rg-workorders-${env}" }
        "KeyVault.Name|${env}"       = { name = "KeyVault.Name", environment = env, value = v.key_vault_name }
        "Sql.ServerName|${env}"      = { name = "Sql.ServerName", environment = env, value = v.sql_server_name }
        "Sql.ServerFqdn|${env}"      = { name = "Sql.ServerFqdn", environment = env, value = "${v.sql_server_name}.database.windows.net" }
        "Sql.Database|${env}"        = { name = "Sql.Database", environment = env, value = v.sql_database }
        "Sql.MigratorUser|${env}"    = { name = "Sql.MigratorUser", environment = env, value = "workorders_migrator" }
        "AI.OpenAIUrl|${env}"        = { name = "AI.OpenAIUrl", environment = env, value = v.ai_openai_url }
        "AI.OpenAIModel|${env}"      = { name = "AI.OpenAIModel", environment = env, value = v.ai_openai_model }
      },
      # The acceptance login exists in TDD only (ADR-C11 interlock 3).
      env == "tdd" ? { "Sql.AcceptanceUser|tdd" = { name = "Sql.AcceptanceUser", environment = "tdd", value = "workorders_acceptance" } } : {}
    )
  ]...)

  workorders_infrastructure_variables = merge(
    {
      "Terraform.StateResourceGroup"  = { name = "Terraform.StateResourceGroup", environments = [], value = var.workorders_infrastructure.state_resource_group }
      "Terraform.StateStorageAccount" = { name = "Terraform.StateStorageAccount", environments = [], value = var.workorders_infrastructure.state_storage_account }
      "Terraform.StateContainer"      = { name = "Terraform.StateContainer", environments = [], value = var.workorders_infrastructure.state_container }
    },
    merge([
      for environment, class in local.infra_environments : {
        "Environment.Class|${environment}"  = { name = "Environment.Class", environments = [environment], value = class }
        "Terraform.StateKey|${environment}" = { name = "Terraform.StateKey", environments = [environment], value = "environment-${class}.tfstate" }
      }
    ]...)
  )
}

resource "octopusdeploy_variable" "workorders_environment" {
  for_each = local.workorders_environment_variables

  owner_id = octopusdeploy_library_variable_set.workorders_environment.id
  type     = "String"
  name     = each.value.name
  value    = each.value.value

  scope {
    environments = [octopusdeploy_environment.this[each.value.environment].id]
  }
}

resource "octopusdeploy_variable" "workorders_infrastructure" {
  for_each = local.workorders_infrastructure_variables

  owner_id = octopusdeploy_library_variable_set.workorders_infrastructure.id
  type     = "String"
  name     = each.value.name
  value    = each.value.value

  dynamic "scope" {
    for_each = length(each.value.environments) > 0 ? [each.value.environments] : []
    content {
      environments = [for environment in scope.value : octopusdeploy_environment.this[environment].id]
    }
  }
}

# `WorkOrders Platform Automation` (sleep/wake, ADR-IR33). Platform.OctopusApiKey is the automation user's Space
# Manager key (ADR-IR32). Platform-owned steps use it only to run env-wake and to read tasks through the Octopus
# REST API.
# - It reaches only the steps that need it (S5). platform-wake has exactly one step, so it includes this set.
#   workorders-infrastructure has Terraform and in-cluster steps too, so it gets the step-scoped project variable
#   below instead. No app project holds the key: app deployments wake the cluster keylessly through platform-wake,
#   which is also the simpler design (ADR-IR33). The single trusted operator (the user) accepts the key in these
#   platform-owned places and in the gateways (ADR-IR33 threat model).
# - The value arrives as TF_VAR_platform_octopus_api_key at apply time and is never committed. The provider has
#   no write-only argument for it, so it is stored in octopus-space.tfstate; access to the state container
#   protects it (versions.tf). Rotating the key (R23) means re-applying this configuration.
resource "octopusdeploy_library_variable_set" "platform_automation" {
  name        = "WorkOrders Platform Automation"
  description = "Sensitive Platform.OctopusApiKey for the sleep and wake runbooks and steps: run env-wake, read tasks (ADR-IR33). Managed by octopus/terraform."
}

resource "octopusdeploy_variable" "platform_octopus_api_key" {
  owner_id        = octopusdeploy_library_variable_set.platform_automation.id
  type            = "Sensitive"
  name            = "Platform.OctopusApiKey"
  description     = "API key of the automation user (ADR-IR32). Used only to run env-wake and read tasks through the Octopus REST API (ADR-IR33). Set from TF_VAR_platform_octopus_api_key."
  is_sensitive    = true
  sensitive_value = var.platform_octopus_api_key
}

# S5 (ADR-IR33 risk 6): the same key for workorders-infrastructure, scoped to the steps that call the Octopus REST
# API: wake-environment (env-plan, env-apply, env-destroy, rotate-sql-passwords), wait-for-workers-and-gateway
# (env-wake), decide-sleep and stop-cluster (env-sleep). The Terraform steps and the in-cluster steps
# (configure-db-principals-<env>, rotate-<env> on k8s-<env>) never receive it. Scope values are the runbook and step
# slugs of .octopus/workorders-infrastructure/runbooks [VERIFY the ID form Octopus expects for runbooks and steps
# stored in Git, design §12 Q26; fallback: include the library set above in the project and record the residual,
# every step of the project can then resolve the key]. tool-boundaries.sh TB20 keeps references to these steps.
resource "octopusdeploy_variable" "infrastructure_platform_octopus_api_key" {
  owner_id        = octopusdeploy_project.workorders_infrastructure.id
  type            = "Sensitive"
  name            = "Platform.OctopusApiKey"
  description     = "API key of the automation user (ADR-IR32), only for the steps of workorders-infrastructure that run env-wake or read tasks through the Octopus REST API (ADR-IR33, S5). Set from TF_VAR_platform_octopus_api_key."
  is_sensitive    = true
  sensitive_value = var.platform_octopus_api_key

  scope {
    processes = ["env-wake", "env-sleep", "env-plan", "env-apply", "env-destroy", "rotate-sql-passwords"]
    actions   = ["wake-environment", "wait-for-workers-and-gateway", "decide-sleep", "stop-cluster"]
  }
}

# Stored sets: looked up by name only, never managed. Zero matches is accepted because R5 allows deleting them
# after phase 2; more than one match is an error.
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
