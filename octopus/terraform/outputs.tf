# Outputs for the bootstrap guide (docs/bootstrap.md), terraform/foundation cross-checks and Codefresh objects.

output "environment_ids" {
  description = "Environment IDs by slug."
  value       = { for slug, environment in octopusdeploy_environment.this : slug => environment.id }
}

output "project_ids" {
  description = "Project IDs by slug."
  value       = local.project_ids
}

output "lifecycle_ids" {
  description = "Lifecycle IDs by name."
  value = {
    "workorders-standard"       = octopusdeploy_lifecycle.workorders_standard.id
    "workorders-hotfix"         = octopusdeploy_lifecycle.workorders_hotfix.id
    "workorders-infrastructure" = octopusdeploy_lifecycle.workorders_infrastructure.id
  }
}

output "channel_ids" {
  description = "Channel IDs of project workorders. Default is null until the auto-created channel is imported (channels.tf)."
  value = {
    "Default" = try(octopusdeploy_channel.default[0].id, null)
    "Hotfix"  = octopusdeploy_channel.hotfix.id
  }
}

output "worker_pool_ids" {
  description = "Worker pool IDs by slug; terraform/environment registers the Kubernetes workers into k8s-<env>."
  value = merge(
    { for env, pool in octopusdeploy_static_worker_pool.k8s : "k8s-${env}" => pool.id },
    { "hosted-ubuntu" = one([for p in data.octopusdeploy_worker_pools.hosted_ubuntu.worker_pools : p.id if p.name == "Hosted Ubuntu"]) }
  )
}

output "library_variable_set_ids" {
  description = "IDs of the library variable sets managed here."
  value = {
    "WorkOrders Environment"    = octopusdeploy_library_variable_set.workorders_environment.id
    "WorkOrders Infrastructure" = octopusdeploy_library_variable_set.workorders_infrastructure.id
  }
}

# Subjects that terraform/foundation must use for the Octopus-issuer federated credentials (§5.2).
output "oidc_federated_subjects" {
  description = "Expected Octopus OIDC subjects per UAMI, rendered from the configured subject keys. The ACR feed format is [VERIFY]."
  value = merge(
    { for env in local.app_environments : "id-octopus-deploy-${env}" => "space:${var.octopus_space_slug}:project:workorders:environment:${env}" },
    { for environment, class in local.infra_environments : "id-env-lifecycle-${class}" => "space:${var.octopus_space_slug}:project:workorders-infrastructure:environment:${environment}" },
    { "id-octopus-acr-pull" = "space:${var.octopus_space_slug}:feed:acr-workorders" }
  )
}

output "oidc_account_names" {
  description = "Azure OIDC account names (also their slugs) referenced by the OCL variables."
  value = concat(
    [for account in octopusdeploy_azure_openid_connect.deploy : account.name],
    [for account in octopusdeploy_azure_openid_connect.env_lifecycle : account.name]
  )
}

# Non-secret values of the Codefresh secret context workorders-octopus (ADR-IR32). OCTOPUS_API_KEY is the API key of
# the automation user; it never passes through Terraform. Attached only to workorders/release.
output "codefresh_octopus_context" {
  description = "Non-secret keys of Codefresh secret context workorders-octopus; OCTOPUS_API_KEY is added by hand."
  value = {
    OCTOPUS_URL      = var.octopus_url
    OCTOPUS_SPACE_ID = var.octopus_space_id
  }
}

output "automation_user_id" {
  description = "ID of the existing automation user (read by name, ADR-IR32)."
  value       = local.automation_user_id
}

output "team_ids" {
  description = "Team IDs by name."
  value       = { for name, team in octopusdeploy_team.this : name => team.id }
}

output "stored_objects" {
  description = "Stored objects found by name (never managed here)."
  value = {
    git_credential_id         = local.stored_git_credential_id
    provisioner_account_id    = try(local.stored_provisioner_account.id, null)
    library_variable_set_ids  = local.stored_library_variable_set_ids
    provisioner_account_envs  = try(local.stored_provisioner_account.environments, [])
    provisioner_account_scope = "infra-nonprod only (R4); referenced solely by Azure.LifecycleAccount"
  }
}

output "prod_freeze_id" {
  description = "ID of prod-weekend-freeze."
  value       = octopusdeploy_project_deployment_freeze.prod_weekend.id
}
