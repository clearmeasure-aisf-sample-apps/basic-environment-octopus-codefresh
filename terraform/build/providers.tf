provider "azurerm" {
  subscription_id = var.subscription_id
  tenant_id       = var.tenant_id

  # terraform/foundation registers every resource provider (Microsoft.ContainerService among them).
  resource_provider_registrations = "none"

  features {}
}
