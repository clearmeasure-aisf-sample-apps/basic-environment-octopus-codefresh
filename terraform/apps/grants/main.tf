module "descriptor" {
  source   = "../descriptor"
  for_each = toset(["nonprod", "prod"])

  app             = var.app
  tier            = each.key
  subscription_id = var.subscription_id
  descriptor      = var.descriptor
}

locals {
  # Tier-independent keys read the same in both instances.
  d = module.descriptor["nonprod"]

  # environment -> tier, for the environments the app runs in (the tier map is fixed, §7.0).
  env_tier = merge([for t in ["nonprod", "prod"] : { for e in module.descriptor[t].environments : e => t }]...)
  tiers    = distinct(values(local.env_tier))

  vault_ids = merge([
    for t in ["nonprod", "prod"] : {
      for e, name in module.descriptor[t].vault_names :
      e => "/subscriptions/${var.subscription_id}/resourceGroups/rg-platform-${t}-apps/providers/Microsoft.KeyVault/vaults/${name}"
    }
  ]...)

  resource_groups = local.d.resource_group ? { for t in local.tiers : t => "rg-app-${var.app}-${t}" } : {}

  deploy_identities = local.d.azure_account ? { for e, t in local.env_tier : e => { tier = t, name = "id-${var.app}-${e}-deploy" } } : {}
  app_identities    = local.d.workload_identity ? { for e, t in local.env_tier : e => { tier = t, name = "id-${var.app}-${e}-app" } } : {}

  # One Octopus-issuer credential per project and environment (decision 14).
  deploy_credentials = {
    for pair in setproduct(sort(keys(local.deploy_identities)), local.d.octopus_projects) : "${pair[0]}/${pair[1]}" => {
      env     = pair[0]
      project = pair[1]
    }
  }

  # Roles of each app identity on its tier's app group.
  app_role_assignments = {
    for pair in setproduct(sort(keys(local.app_identities)), local.d.app_roles) : "${pair[0]}/${pair[1]}" => {
      env  = pair[0]
      role = pair[1]
    } if contains(keys(local.resource_groups), local.env_tier[pair[0]])
  }

  conformance = var.conformance_principal_object_id != null

  base_tags = merge(var.tags, {
    "platform-app" = var.app
    "managed-by"   = "terraform-apps-grants"
  })
}
