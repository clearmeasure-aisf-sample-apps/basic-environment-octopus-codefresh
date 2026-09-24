# Channels of the app projects (ADR-IR34 §7.0): Default and Hotfix per project, and Strict for the sandbox, from
# octopus.projects[].channels of each descriptor. Channels are not stored in Git (E26).
#
# Default: Octopus creates it with every project; it inherits the project lifecycle (platform-standard) and is not
# managed here, so a new app needs no second apply to adopt it. The Default channel of workorders that the preview
# imported is dropped from state without being deleted (moved.tf).
# Hotfix: lifecycle platform-hotfix (uat, prod); step hotfix-justification runs first. Created by Release Managers.
# Strict (sandbox only): the project lifecycle; variables.ocl of the sandbox scopes Platform.SoDMode = enforce to it.
# Git reference rule refs/heads/main: releases come from the default branch only. No package version rules: they
# would name app-specific steps and packages, and the app owns those (scaffold, then own).

locals {
  channel_descriptions = {
    "Hotfix" = "Hotfix releases (<package-version>-hotfix.<n>), created by Release Managers: uat, then prod."
    "Strict" = "Conformance channel: Platform.SoDMode is enforce, so the creator of a prod deployment may not approve it (CAP-OCT-004)."
  }
}

resource "octopusdeploy_channel" "app" {
  for_each = local.app_channels

  name                = each.value.name
  description         = lookup(local.channel_descriptions, each.value.name, "Channel ${each.value.name} of ${each.value.project} (apps/*.yaml).")
  project_id          = octopusdeploy_project.app[each.value.project].id
  lifecycle_id        = local.app_lifecycle_ids[each.value.lifecycle]
  is_default          = false
  git_reference_rules = ["refs/heads/main"]
}
