# Octopus objects that config as code does not store (E26): environments, lifecycles, project groups and project
# shells with their Git settings, channels, feeds, accounts, worker pools, the machine policy, library variable sets,
# step templates, teams and role assignments, the prod freezes and the env-sleep triggers (ADR-IR34 §7.0, §11.7.2).
# App objects come from the descriptors apps/*.yaml (apps.tf, for_each); platform objects are fixed.
# Applied by a Space Manager with the one Octopus credential, the Space Manager API key (ADR-IR32): by the main loop
# at P1-06 through octopus/apply.ps1, and after every onboarding. Never from a pipeline.
#
# Objects the user stored are looked up by name and never created or managed: account `Azure Runtime Provisioner`,
# library variable sets `Azure Runtime Provisioning` and `GitHub AISF Sample Apps`, Git credential
# `GitHub clearmeasure-aisf-sample-apps`, the automation user `aisf-service-account` and the built-in user roles.
#
# The live objects of the phase 0 preview are adopted, never recreated: moved.tf maps their old addresses, and
# docs/preview-octopus.md gives the apply procedure (§11.9).

terraform {
  # 1.7 or later: removed blocks (moved.tf).
  required_version = ">= 1.7.0"

  required_providers {
    octopusdeploy = {
      source  = "OctopusDeploy/octopusdeploy"
      version = "1.20.0"
    }
    # Read-only identity lookups (azure.tf); the same pin as the terraform/ layers.
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.6"
    }
  }

  # State octopus-space.tfstate in the global state account (ADR-IR34 §7.0, "Terraform layers"), completed at init:
  #   terraform init \
  #     -backend-config=resource_group_name=rg-platform-global \
  #     -backend-config=storage_account_name=<tfstate-storage-account-global> \
  #     -backend-config=container_name=tfstate \
  #     -backend-config=key=octopus-space.tfstate \
  #     -backend-config=use_azuread_auth=true
  # octopus/apply.ps1 instead runs a copy of this directory with a local-backend override on a given state file (the
  # preview state at P1-06); docs/preview-octopus.md then migrates that state here with terraform init -migrate-state.
  # Secrets in state: PlatformWake.OctopusApiKey and the step-scoped Platform.OctopusApiKey (the Space Manager key,
  # from TF_VAR_platform_octopus_api_key), and the optional Octopus.WorkerRegistrationToken
  # and E2E.GitHubAppPrivateKey. The provider has no write-only argument for them, so access to the state protects them.
  backend "azurerm" {}
}
