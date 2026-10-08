# Names (§7.0, ADR-IR34 "Resource groups") and the tag contract. Every other layer finds these objects
# by the same names, so they never change without a design change.

# quota_id (the subscription offer) decides whether budgets can exist (budgets.tf).
data "azurerm_subscription" "current" {}

# The principal that runs Terraform: the provisioner by design (variable provisioner_object_id).
data "azurerm_client_config" "current" {}

locals {
  tiers = ["nonprod", "prod"]

  # Lowercase, as Azure returns it: role-assignment scopes are compared as strings and force a new
  # assignment when they differ.
  subscription_resource_id = "/subscriptions/${lower(var.subscription_id)}"

  # --- Resource groups ------------------------------------------------------------------------------
  # Ten groups created here. AKS creates the three node groups (node_resource_group of each cluster);
  # terraform/apps/grants creates rg-app-<app>-<tier>. The foreign groups NetworkWatcherRG and ai-model
  # are never referenced.
  rg_global = "rg-platform-global"
  rg_build  = "rg-platform-build"
  rg_tier = {
    for t in local.tiers : t => {
      shared = "rg-platform-${t}-shared"
      aks    = "rg-platform-${t}-aks"
      data   = "rg-platform-${t}-data"
      apps   = "rg-platform-${t}-apps"
    }
  }

  # name => cost tags of the group.
  resource_groups = merge(
    {
      (local.rg_global) = { tier = "global", component = "state" }
      (local.rg_build)  = { tier = "build", component = "build" }
    },
    merge([
      for t in local.tiers : {
        for part, name in local.rg_tier[t] : name => { tier = t, component = part }
      }
    ]...),
  )

  # Created by other layers; used here only for grants, budgets and outputs.
  clusters = {
    build = {
      name                = "aks-platform-build"
      resource_group_name = local.rg_build
      node_resource_group = "rg-platform-build-aks-nodes"
    }
    nonprod = {
      name                = "aks-platform-nonprod"
      resource_group_name = local.rg_tier["nonprod"].aks
      node_resource_group = "rg-platform-nonprod-aks-nodes"
    }
    prod = {
      name                = "aks-platform-prod"
      resource_group_name = local.rg_tier["prod"].aks
      node_resource_group = "rg-platform-prod-aks-nodes"
    }
  }
  cluster_ids = {
    for k, c in local.clusters :
    k => "${local.subscription_resource_id}/resourceGroups/${c.resource_group_name}/providers/Microsoft.ContainerService/managedClusters/${c.name}"
  }
  node_resource_group_ids = {
    for k, c in local.clusters : k => "${local.subscription_resource_id}/resourceGroups/${c.node_resource_group}"
  }

  # --- Globally unique names (variable name_suffix) --------------------------------------------------
  # One suffix instead of six free-form names: the pattern stays readable, the operator chooses one
  # value, and every layer can derive the names. Storage names: 3-24 lowercase letters and digits.
  acr_name = "acrplatform${var.name_suffix}"
  tfstate_account_names = {
    global  = "sttfglobal${var.name_suffix}"
    nonprod = "sttfnonprod${var.name_suffix}"
    prod    = "sttfprod${var.name_suffix}"
  }
  backup_account_names = {
    for t in local.tiers : t => "stbkp${t}${var.name_suffix}"
  }

  # --- Octopus OIDC ------------------------------------------------------------------------------------
  octopus_issuer      = var.octopus_url
  federation_audience = "api://AzureADTokenExchange"

  # --- GitHub OIDC (the health dashboard's publisher) --------------------------------------------------
  # The subject GitHub presents for a workflow of the dashboard repository's main branch. The repository uses
  # immutable subjects (use_immutable_subject, GitHub's default for new repositories): owner and repository are
  # followed by their numeric IDs, so a renamed or re-created repository of the same name is not this subject.
  #   gh api repos/<owner>/<name>/actions/oidc/customization/sub
  dashboard_repository_parts = split("/", var.dashboard_repository)
  dashboard_subject          = "repo:${local.dashboard_repository_parts[0]}@${var.dashboard_repository_ids.owner}/${try(local.dashboard_repository_parts[1], "")}@${var.dashboard_repository_ids.repository}:ref:refs/heads/main"

  # --- Tags (§7.0: platform-tier and platform-component on every platform resource) --------------------
  base_tags = merge(var.tags, { "managed-by" = "terraform-foundation" })
}
