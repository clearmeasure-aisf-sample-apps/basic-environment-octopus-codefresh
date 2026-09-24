# Lifecycles (ADR-IR34 §7.0). The three live lifecycles keep their IDs and are renamed from workorders-* (moved.tf,
# §11.9); platform-wake is new. An app may add its own lifecycle app-<app>-<name> in a later change.
# optional_deployment_targets = environments deployed manually; automatic_deployment_targets = deployed as soon as the
# phase is reached (the Octopus API's OptionalDeploymentTargets / AutomaticDeploymentTargets).

resource "octopusdeploy_lifecycle" "platform_standard" {
  name        = "platform-standard"
  description = "Channel Default of every app project: tdd (automatic), then uat and prod, both manual."

  phase {
    name                         = "TDD"
    automatic_deployment_targets = var.tdd_auto_deploy ? [octopusdeploy_environment.this["tdd"].id] : []
    optional_deployment_targets  = var.tdd_auto_deploy ? [] : [octopusdeploy_environment.this["tdd"].id]
  }

  phase {
    name                        = "UAT"
    optional_deployment_targets = [octopusdeploy_environment.this["uat"].id]
  }

  phase {
    name                        = "Prod"
    optional_deployment_targets = [octopusdeploy_environment.this["prod"].id]
  }
}

resource "octopusdeploy_lifecycle" "platform_hotfix" {
  name        = "platform-hotfix"
  description = "Channel Hotfix of every app project: uat, then prod. Skips tdd; step hotfix-justification records why (ADR-D13)."

  phase {
    name                        = "UAT"
    optional_deployment_targets = [octopusdeploy_environment.this["uat"].id]
  }

  phase {
    name                        = "Prod"
    optional_deployment_targets = [octopusdeploy_environment.this["prod"].id]
  }
}

resource "octopusdeploy_lifecycle" "platform_infrastructure" {
  name        = "platform-infrastructure"
  description = "Project platform-infrastructure (runbooks only). Octopus requires one non-optional phase; runbooks ignore lifecycle progression."

  phase {
    name                        = "Infra Nonprod"
    optional_deployment_targets = [octopusdeploy_environment.this["infra-nonprod"].id]
    is_optional_phase           = false
  }

  phase {
    name                        = "Infra Prod"
    optional_deployment_targets = [octopusdeploy_environment.this["infra-prod"].id]
    is_optional_phase           = true
  }
}

# Project platform-wake (ADR-IR33). A Deploy a Release step deploys the child to the parent's environment, and the
# child release must be eligible there (https://octopus.com/docs/projects/coordinating-multiple-projects/deploy-release-step,
# "Lifecycles"). One phase holds tdd, uat and prod as optional targets, so any release can be deployed to any of them
# at any time, Hotfix deployments that skip tdd included.
resource "octopusdeploy_lifecycle" "platform_wake" {
  name        = "platform-wake"
  description = "Project platform-wake: tdd, uat and prod in one phase, any order, so a Deploy a Release step can wake the cluster from any app deployment."

  phase {
    name                        = "Application environments"
    optional_deployment_targets = [for env in local.app_environments : octopusdeploy_environment.this[env].id]
    is_optional_phase           = false
  }
}

locals {
  app_lifecycle_ids = {
    "platform-standard" = octopusdeploy_lifecycle.platform_standard.id
    "platform-hotfix"   = octopusdeploy_lifecycle.platform_hotfix.id
  }
}
