provider "azurerm" {
  subscription_id = var.subscription_id
  tenant_id       = var.tenant_id

  # State and backup accounts disable shared keys, so any data-plane call must use Entra ID.
  storage_use_azuread = true

  # azurerm 5.x registers no resource provider by default. This layer registers every provider the
  # platform layers need, because only the provisioner holds */register/action at subscription scope
  # (Contributor); the tier identities hold Contributor on their resource groups only. Registration runs
  # when the provider starts and skips providers that are already registered (live fact 2026-09-24: all
  # are, except Microsoft.AlertsManagement, which apr-sleep-<tier> needs). Microsoft.Consumption serves
  # the budgets.
  resource_providers_to_register = [
    "Microsoft.AlertsManagement",
    "Microsoft.Authorization",
    "Microsoft.Compute",
    "Microsoft.Consumption",
    "Microsoft.ContainerRegistry",
    "Microsoft.ContainerService",
    "Microsoft.Insights",
    "Microsoft.KeyVault",
    "Microsoft.ManagedIdentity",
    "Microsoft.Network",
    "Microsoft.OperationalInsights",
    "Microsoft.OperationsManagement",
    "Microsoft.Storage",
  ]

  features {
    resource_group {
      # The groups hold resources of other layers (tier, apps, AKS); never delete a non-empty group.
      prevent_deletion_if_contains_resources = true
    }

    storage {
      # Containers and management policies go through Azure Resource Manager. No data-plane call is
      # needed, so the first apply works before the provisioner's data-plane grant has propagated.
      data_plane_available = false
    }
  }
}

# Entra objects: sp-platform-conformance and platform-operators. With Application.ReadWrite.OwnedBy and
# Group.Create the provisioner manages only objects it owns, so every Entra object names it as owner
# (entra.tf).
provider "azuread" {
  tenant_id = var.tenant_id
}
