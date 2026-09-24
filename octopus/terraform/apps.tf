# App descriptors (ADR-IR34 decision 12): one state for the space, for_each over apps/*.yaml. The schema is
# apps/schema.json; Platform.Onboarding check validates the descriptors, so this file reads only what it needs:
#   name, status (active | frozen), environments (default tdd, uat, prod), octopus.azureAccount (default false),
#   octopus.projects[].name, .lifecycle (default platform-standard) and .channels (default Default, Hotfix).
# Per descriptor: project group app-<app>; per project: the shell with config as code at .octopus/apps/<app>/<project>,
# its channels other than Default (Octopus creates Default with the project), the prod freeze and, with
# octopus.azureAccount, the accounts azure-<app>-<env>.
# A frozen app keeps its objects and its projects are disabled (decision 28). Deleting a descriptor removes its
# objects, except the projects: prevent_destroy keeps release history until a person retires them by hand.

locals {
  repo_root = var.repo_root != null ? var.repo_root : abspath("${path.module}/../..")
  apps_dir  = "${local.repo_root}/apps"

  descriptors = {
    for file in fileset(local.apps_dir, "*.yaml") : trimsuffix(file, ".yaml") => yamldecode(file("${local.apps_dir}/${file}"))
  }

  apps = {
    for app, d in local.descriptors : app => {
      name          = try(d.name, "")
      status        = try(d.status, "active")
      environments  = try(d.environments, ["tdd", "uat", "prod"])
      azure_account = try(d.octopus.azureAccount, false)
      projects = [
        for p in try(d.octopus.projects, []) : {
          name      = p.name
          lifecycle = try(p.lifecycle, "platform-standard")
          channels  = try(p.channels, ["Default", "Hotfix"])
        }
      ]
    }
  }

  app_projects = merge(concat([{}], [
    for app, a in local.apps : {
      for p in a.projects : p.name => {
        app          = app
        lifecycle    = p.lifecycle
        channels     = p.channels
        frozen       = a.status == "frozen"
        environments = a.environments
      }
    }
  ])...)

  # Channels besides Default: Hotfix always uses platform-hotfix; any other channel (the sandbox's Strict) uses the
  # project's lifecycle.
  app_channels = merge(concat([{}], [
    for project, p in local.app_projects : {
      for channel in p.channels : "${project}/${channel}" => {
        project   = project
        name      = channel
        lifecycle = channel == "Hotfix" ? "platform-hotfix" : p.lifecycle
      } if channel != "Default"
    }
  ])...)

  app_accounts = merge(concat([{}], [
    for app, a in local.apps : {
      for env in a.environments : "${app}-${env}" => { app = app, environment = env }
    } if a.azure_account
  ])...)

  # A syntactically valid client ID that matches no identity: the account exists, so OCL that names it loads, and an
  # Azure login with it fails until terraform/apps/grants has created the identity and its ID is applied.
  placeholder_client_id = "00000000-0000-0000-0000-000000000000"
}

check "descriptor_names_match_files" {
  assert {
    condition     = alltrue([for app, a in local.apps : a.name == app])
    error_message = "Every descriptor apps/<app>.yaml must declare name: <app>."
  }
}

check "descriptors_found" {
  assert {
    condition     = length(local.app_projects) > 0
    error_message = "No descriptor under ${local.apps_dir} declares an Octopus project; check repo_root (${local.repo_root})."
  }
}
