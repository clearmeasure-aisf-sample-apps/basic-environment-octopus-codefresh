# Octopus objects that config-as-code does not store (E26): environments, lifecycles, projects and their
# version-control settings, channels, feeds, accounts, worker pools, library variable sets, teams, roles,
# service accounts, the OIDC identity, triggers, the worker machine policy and the prod freeze (platform-design
# §7.2, ADR-D7, ADR-IR33).
# Applied by a platform engineer from a workstation; never from a pipeline (ADR-D8: no Octopus API key in CI).
#
# Objects the user already stored are looked up by name and never created or managed:
#   account `Azure Runtime Provisioner`, library variable sets `Azure Runtime Provisioning` and
#   `GitHub AISF Sample Apps`, Git credential `GitHub clearmeasure-aisf-sample-apps` (§5.3).

terraform {
  # 1.7 or later: import blocks with for_each (channels.tf).
  required_version = ">= 1.7.0"

  required_providers {
    octopusdeploy = {
      source  = "OctopusDeploy/octopusdeploy"
      version = "1.20.0"
    }
  }

  # Partial azurerm backend in the shared state account (§7.10), completed at init time:
  #   terraform init \
  #     -backend-config=resource_group_name=rg-workorders-shared \
  #     -backend-config=storage_account_name=<tfstate-storage-account> \
  #     -backend-config=container_name=tfstate \
  #     -backend-config=key=octopus-space.tfstate \
  #     -backend-config=use_azuread_auth=true
  # State key octopus-space.tfstate (§7.10, ADR-IR9).
  # One secret is managed here: Platform.OctopusApiKey (library variable set WorkOrders Platform Automation,
  # ADR-IR33), from TF_VAR_platform_octopus_api_key. It is masked in plans but stored in this state, so access to
  # the state container (§7.10) protects it. OIDC accounts and the feed carry no credentials.
  # The stored-account lookup reads account metadata only; Octopus returns sensitive fields as "has value" flags
  # [VERIFY that the data source keeps no secret material in state].
  backend "azurerm" {}
}
