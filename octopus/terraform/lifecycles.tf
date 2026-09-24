# Lifecycles (§7.2). Lifecycles are not stored in Git (E26).
# optional_deployment_targets = environments deployed manually; automatic_deployment_targets = deployed as soon
# as the phase is reached (the Octopus API's OptionalDeploymentTargets / AutomaticDeploymentTargets).

resource "octopusdeploy_lifecycle" "workorders_standard" {
  name        = "workorders-standard"
  description = "Channel Default: TDD (automatic from phase 2), then UAT and Prod, both manual."

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

resource "octopusdeploy_lifecycle" "workorders_hotfix" {
  name        = "workorders-hotfix"
  description = "Channel Hotfix: UAT, then Prod. Skips TDD; step hotfix-justification records why (ADR-D13)."

  phase {
    name                        = "UAT"
    optional_deployment_targets = [octopusdeploy_environment.this["uat"].id]
  }

  phase {
    name                        = "Prod"
    optional_deployment_targets = [octopusdeploy_environment.this["prod"].id]
  }
}

resource "octopusdeploy_lifecycle" "workorders_infrastructure" {
  name        = "workorders-infrastructure"
  description = "Project workorders-infrastructure (runbooks only). Octopus requires one non-optional phase; runbooks ignore lifecycle progression."

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
# "Lifecycles"). One phase holds tdd, uat and prod as optional targets, so any release can be deployed to any of
# them at any time, including Hotfix deployments that skip TDD.
resource "octopusdeploy_lifecycle" "platform_wake" {
  name        = "platform-wake"
  description = "Project platform-wake: tdd, uat and prod in one phase, any order, so a Deploy a Release step can wake the cluster from any app deployment."

  phase {
    name                        = "Application environments"
    optional_deployment_targets = [for env in local.app_environments : octopusdeploy_environment.this[env].id]
    is_optional_phase           = false
  }
}
