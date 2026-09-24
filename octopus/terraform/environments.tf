# Environments, in order (ADR-IR34 §7.0). Names are also the slugs that OCL, accounts and the Argo CD annotations use
# (argo.octopus.com/environment). The five live environments are kept with their IDs (§11.9); only the descriptions
# change. The tier map is fixed: tdd and uat run on aks-platform-nonprod, prod on aks-platform-prod.

# sort_order starts at 1: provider 1.20.0 treats 0 as unset, and Octopus then assigns its own value.
locals {
  environments = {
    "tdd"           = { sort_order = 1, description = "Application environment on aks-platform-nonprod. Releases deploy here automatically; destructive app tests run here only." }
    "uat"           = { sort_order = 2, description = "Application environment on aks-platform-nonprod. Manual promotion and UAT sign-off." }
    "prod"          = { sort_order = 3, description = "Application environment on aks-platform-prod. Prod go/no-go, separation-of-duties guard, pre-release backup, weekend freeze." }
    "infra-nonprod" = { sort_order = 4, description = "Runbook environment of the nonprod tier (project platform-infrastructure): terraform/tier and terraform/apps/tier, env-wake, env-sleep." }
    "infra-prod"    = { sort_order = 5, description = "Runbook environment of the prod tier (project platform-infrastructure): terraform/tier and terraform/apps/tier, env-wake, env-sleep. No destroy." }
  }

  app_environments = ["tdd", "uat", "prod"]
  # Tier by infrastructure environment, and the other way round.
  infra_environments = { "infra-nonprod" = "nonprod", "infra-prod" = "prod" }
  tier_environments  = { "nonprod" = "infra-nonprod", "prod" = "infra-prod" }
  # Tier of each application environment (§7.0).
  environment_tiers = { "tdd" = "nonprod", "uat" = "nonprod", "prod" = "prod" }
}

resource "octopusdeploy_environment" "this" {
  for_each = local.environments

  name                         = each.key
  slug                         = each.key
  description                  = each.value.description
  sort_order                   = each.value.sort_order
  allow_dynamic_infrastructure = false
  use_guided_failure           = false
}
