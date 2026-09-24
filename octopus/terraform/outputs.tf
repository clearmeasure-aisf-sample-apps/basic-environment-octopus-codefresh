# Outputs for the other layers, the portal steps and cross-checks. None is secret.

output "environment_ids" {
  description = "Environment name => ID."
  value       = { for name, e in octopusdeploy_environment.this : name => e.id }
}

output "lifecycle_ids" {
  description = "Lifecycle name => ID."
  value = {
    "platform-standard"       = octopusdeploy_lifecycle.platform_standard.id
    "platform-hotfix"         = octopusdeploy_lifecycle.platform_hotfix.id
    "platform-infrastructure" = octopusdeploy_lifecycle.platform_infrastructure.id
    "platform-wake"           = octopusdeploy_lifecycle.platform_wake.id
  }
}

output "project_ids" {
  description = "Project name => ID: the platform projects and every app project of apps/*.yaml."
  value       = merge({ for name, p in octopusdeploy_project.app : name => p.id }, local.platform_project_ids)
}

output "project_group_ids" {
  description = "Project group name => ID."
  value       = merge({ for app, g in octopusdeploy_project_group.app : g.name => g.id }, { (octopusdeploy_project_group.platform.name) = octopusdeploy_project_group.platform.id })
}

output "channel_ids" {
  description = "<project>/<channel> => ID for the managed channels (Default channels are created by Octopus and not managed)."
  value       = { for key, c in octopusdeploy_channel.app : key => c.id }
}

output "library_variable_set_ids" {
  description = "Library variable set name => ID; the stored sets are looked up only."
  value = {
    "Platform Environment"    = octopusdeploy_library_variable_set.platform_environment.id
    "Platform Infrastructure" = octopusdeploy_library_variable_set.platform_infrastructure.id
    "Platform Automation"     = octopusdeploy_library_variable_set.platform_automation.id
    stored                    = local.stored_library_variable_set_ids
  }
}

output "accounts" {
  description = "OIDC account name => ID, environment and client ID (placeholder until the identity exists)."
  value = merge(
    { for tier, a in octopusdeploy_azure_openid_connect.platform_lifecycle : a.name => { id = a.id, environment = local.tier_environments[tier], client_id = a.application_id } },
    { for key, a in octopusdeploy_azure_openid_connect.app : a.name => { id = a.id, environment = local.app_accounts[key].environment, client_id = a.application_id } },
  )
}

output "oidc_subjects" {
  description = "Federated credential subjects the Azure identities must carry (issuer <OCTOPUS_URL>, audience api://AzureADTokenExchange). terraform/foundation and terraform/apps/grants create them; compare with their octopus_federation and deploy_subjects outputs."
  value = merge(
    {
      for tier, environment in local.tier_environments :
      "id-platform-lifecycle-${tier}" => ["space:${var.octopus_space_slug}:project:platform-infrastructure:environment:${environment}"]
    },
    { "id-octopus-acr-pull" = ["space:${var.octopus_space_slug}:feed:acr-apps"] },
    {
      for key, a in local.app_accounts : "id-${key}-deploy" => [
        for project, p in local.app_projects : "space:${var.octopus_space_slug}:project:${project}:environment:${a.environment}" if p.app == a.app
      ]
    },
  )
}

output "feeds" {
  description = "Feed name => ID."
  value = {
    "acr-apps"   = octopusdeploy_azure_container_registry.acr_apps.id
    "docker-hub" = octopusdeploy_docker_container_registry.docker_hub.id
  }
}

output "worker_pools" {
  description = "Kubernetes worker pools k8s-<env> => ID, the Hosted Ubuntu pool, and the machine policy the workers register with (terraform/tier octopus_worker_machine_policy)."
  value = {
    kubernetes     = { for env, p in octopusdeploy_static_worker_pool.k8s : p.name => p.id }
    hosted_ubuntu  = one([for p in data.octopusdeploy_worker_pools.hosted_ubuntu.worker_pools : p.id if p.name == "Hosted Ubuntu"])
    machine_policy = { name = octopusdeploy_machine_policy.kubernetes_workers.name, id = octopusdeploy_machine_policy.kubernetes_workers.id }
  }
}

output "apps_domains" {
  description = "<apps-domain-<tier>> per tier that has one (Platform.AppsDomain)."
  value       = local.apps_domains
}

output "step_templates" {
  description = "Step template name => ID and version."
  value = {
    for t in [octopusdeploy_step_template.sod_guard, octopusdeploy_step_template.db_backup, octopusdeploy_step_template.pin_writer] :
    t.name => { id = t.id, version = t.version }
  }
}

output "teams" {
  description = "Team name => ID, and the automation user's ID and teams."
  value = {
    teams           = { for name, t in octopusdeploy_team.this : name => t.id }
    automation_user = { id = local.automation_user_id, username = var.automation_username, teams = sort(var.automation_user_teams) }
  }
}

output "freezes" {
  description = "App project => ID of its prod-weekend-freeze-<project>."
  value       = { for project, f in octopusdeploy_project_deployment_freeze.prod_weekend : project => { id = f.id, name = f.name } }
}

output "env_sleep_triggers" {
  description = "env-sleep-hourly-<tier> => trigger ID, or a note when the triggers are created through the REST API (env_sleep_triggers_managed = false)."
  value = var.env_sleep_triggers_managed ? { for name, t in octopusdeploy_project_scheduled_trigger.env_sleep : name => t.id } : {
    "env-sleep-hourly-nonprod" = "not managed: create through the REST API (docs/preview-octopus.md)"
    "env-sleep-hourly-prod"    = "not managed: create through the REST API (docs/preview-octopus.md)"
  }
}

output "stored_objects" {
  description = "Objects the user stored, looked up by name and never managed."
  value = {
    git_credential        = { name = var.stored_git_credential_name, id = local.stored_git_credential_id }
    azure_account         = local.stored_provisioner_account == null ? null : { name = local.stored_provisioner_account.name, id = local.stored_provisioner_account.id }
    library_variable_sets = local.stored_library_variable_set_ids
  }
}

output "platform_octopus_context" {
  description = "Non-secret values of Codefresh context platform-octopus (OCTOPUS_API_KEY is the Space Manager key, never an output)."
  value = {
    OCTOPUS_URL      = var.octopus_url
    OCTOPUS_SPACE_ID = var.octopus_space_id
  }
}
