# State migration of the phase 0 preview (ADR-IR34 §11.9). The preview state holds 33 live objects; one apply of this
# configuration on a copy of that state keeps all of them (IDs unchanged) and creates the rest (docs/preview-octopus.md).
#
# Unchanged addresses (updated in place where their arguments changed):
#   octopusdeploy_environment.this["tdd" | "uat" | "prod" | "infra-nonprod" | "infra-prod"]
#   octopusdeploy_docker_container_registry.docker_hub
#   octopusdeploy_team.this[<the seven team names>]
#   octopusdeploy_scoped_user_role.this[<the ten keys>]      project scopes dropped, environment scopes kept
# Renamed addresses: the moved blocks below. Renames of the objects themselves (workorders-* to platform-*, the group
# Work Orders to app-workorders, workorders-infrastructure to platform-infrastructure) are in-place updates.
# octopusdeploy_channel.default[0] (Channels-984, Default of workorders): no longer managed (channels.tf); forgotten,
# never deleted.

moved {
  from = octopusdeploy_lifecycle.workorders_standard
  to   = octopusdeploy_lifecycle.platform_standard
}

moved {
  from = octopusdeploy_lifecycle.workorders_hotfix
  to   = octopusdeploy_lifecycle.platform_hotfix
}

moved {
  from = octopusdeploy_lifecycle.workorders_infrastructure
  to   = octopusdeploy_lifecycle.platform_infrastructure
}

moved {
  from = octopusdeploy_library_variable_set.workorders_environment
  to   = octopusdeploy_library_variable_set.platform_environment
}

moved {
  from = octopusdeploy_library_variable_set.workorders_infrastructure
  to   = octopusdeploy_library_variable_set.platform_infrastructure
}

moved {
  from = octopusdeploy_project_group.work_orders
  to   = octopusdeploy_project_group.app["workorders"]
}

moved {
  from = octopusdeploy_project.workorders
  to   = octopusdeploy_project.app["workorders"]
}

moved {
  from = octopusdeploy_project.workorders_infrastructure
  to   = octopusdeploy_project.platform_infrastructure
}

moved {
  from = octopusdeploy_project_deployment_freeze.prod_weekend
  to   = octopusdeploy_project_deployment_freeze.prod_weekend["workorders"]
}

removed {
  from = octopusdeploy_channel.default

  lifecycle {
    destroy = false
  }
}

# platform-continuous was created through the API on 2026-09-27 so workorders could promote without people before the
# next apply; the apply adopts it instead of creating a second lifecycle of that name.
import {
  to = octopusdeploy_lifecycle.platform_continuous
  id = "Lifecycles-634"
}
