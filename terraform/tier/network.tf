# Network of the tier in rg-platform-<tier>-shared (§7.0 "Network"): vnet-platform-<tier> with subnet
# snet-aks-<tier>, and two static public IPs. No peering, no private endpoint: the tiers share nothing on the
# network, Argo CD, ESO, the Octopus workers and the gateway connect outbound, and both clusters pull from the
# public endpoint of the shared registry (ADR-IR34, directive §§11-12).
#
# The foundation grants id-aks-<tier>-controlplane Network Contributor on this group, which covers joining the
# subnet and both IPs (outbound rules and the gateway's load-balancer frontend).

resource "azurerm_virtual_network" "this" {
  name                = "vnet-platform-${var.tier}"
  location            = var.location
  resource_group_name = local.rg_shared
  address_space       = var.network.address_space

  tags = merge(local.base_tags, { "platform-component" = "network" })
}

# Nodes only: pods and services use the CNI overlay ranges of aks.tf.
resource "azurerm_subnet" "aks" {
  name                 = "snet-aks-${var.tier}"
  resource_group_name  = local.rg_shared
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = [var.network.aks_subnet_prefix]
}

# Egress: the load balancer's only outbound IP, so vault and storage firewalls can name the tier.
resource "azurerm_public_ip" "egress" {
  name                = "pip-platform-${var.tier}-egress"
  location            = var.location
  resource_group_name = local.rg_shared
  allocation_method   = "Static"
  sku                 = "Standard"

  tags = merge(local.base_tags, { "platform-component" = "network" })

  # Kept through a rebuild (versions.tf, "Rebuild"): allow-lists name this address.
  lifecycle {
    prevent_destroy = true
  }
}

# Ingress: gateway platform-gateway in namespace platform-ingress claims it with the Service annotations
# service.beta.kubernetes.io/azure-pip-name: pip-platform-<tier>-ingress and
# service.beta.kubernetes.io/azure-load-balancer-resource-group: rg-platform-<tier>-shared (gitops). Host names
# default to <app>-<env>.<ingress-ip-dashed-<tier>>.sslip.io (ADR-IR34 decision 21; output apps_domain).
# The optional Azure DNS label (R35) adds the one host <app>-<env>.<azure-region>.cloudapp.azure.com (output ingress_fqdn).
# Terraform alone owns the label: the Envoy Service carries no service.beta.kubernetes.io/azure-dns-label-name
# annotation (gitops/platform/ingress). Setting, changing or removing domain_name_label is an in-place update
# (azurerm 5.x: not ForceNew); domain_name_label_scope, which would force a replacement once set, stays unset.
resource "azurerm_public_ip" "ingress" {
  name                = "pip-platform-${var.tier}-ingress"
  location            = var.location
  resource_group_name = local.rg_shared
  allocation_method   = "Static"
  sku                 = "Standard"
  domain_name_label   = var.ingress_domain_name_label

  tags = merge(local.base_tags, { "platform-component" = "ingress" })

  # Kept through a rebuild (versions.tf, "Rebuild"): the apps domain is derived from this address.
  lifecycle {
    prevent_destroy = true
  }
}
