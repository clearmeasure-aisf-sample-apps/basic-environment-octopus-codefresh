module "descriptor" {
  source = "../descriptor"

  app             = var.app
  tier            = var.tier
  subscription_id = var.subscription_id
  descriptor      = var.descriptor
}

locals {
  envs = toset(module.descriptor.environments)

  rg_shared = "rg-platform-${var.tier}-shared"
  rg_aks    = "rg-platform-${var.tier}-aks"
  rg_data   = "rg-platform-${var.tier}-data"
  rg_apps   = "rg-platform-${var.tier}-apps"

  # SQL logins of the database component (§7.0 "Database"): sa for the init and backup Jobs, the migrator for
  # the PreSync Job, the app login for the workloads. Vault keys db-<login>-password.
  db_logins = {
    sa       = "sa"
    migrator = "${var.app}_migrator"
    app      = "${var.app}_app"
  }

  db_secrets = module.descriptor.has_database ? {
    for pair in setproduct(sort(tolist(local.envs)), sort(keys(local.db_logins))) : "${pair[0]}/${pair[1]}" => {
      env   = pair[0]
      login = pair[1]
    }
  } : {}

  app_secrets = {
    for pair in setproduct(sort(tolist(local.envs)), module.descriptor.secrets) : "${pair[0]}/${pair[1].name}" => {
      env      = pair[0]
      name     = pair[1].name
      generate = pair[1].generate
    }
  }

  # §7.0 tags: platform-tier and platform-component on every platform resource, platform-app and platform-env on
  # per-app resources.
  env_tags = {
    for e in local.envs : e => merge(var.tags, {
      "platform-tier" = var.tier
      "platform-app"  = var.app
      "platform-env"  = e
      "managed-by"    = "terraform-apps-tier"
    })
  }
}

# Tier objects of terraform/tier, found by their §7.0 names.
data "azurerm_log_analytics_workspace" "tier" {
  name                = "log-platform-${var.tier}"
  resource_group_name = local.rg_shared
}

data "azurerm_monitor_action_group" "oncall" {
  name                = "ag-platform-oncall"
  resource_group_name = local.rg_aks
}
