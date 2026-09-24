provider "azurerm" {
  subscription_id = var.subscription_id
  tenant_id       = var.tenant_id

  # terraform/foundation registers every resource provider. The lifecycle identity holds Contributor on resource
  # groups only and cannot register providers, so the 5.x default "none" stays.
  resource_provider_registrations = "none"

  # The state account and the vault are reached with Entra ID only.
  storage_use_azuread = true

  features {
    key_vault {
      # Purge protection is on: a destroyed vault is only soft-deleted, and the next apply recovers it. env-destroy
      # keeps the vault (versions.tf, "Rebuild").
      purge_soft_delete_on_destroy          = false
      recover_soft_deleted_key_vaults       = true
      purge_soft_deleted_secrets_on_destroy = false
      recover_soft_deleted_secrets          = true
    }
  }
}

# --- Kubernetes and Helm: Entra ID tokens, never a local account ----------------------------------------------------
# Local accounts are disabled (aks.tf), so there is no client certificate or static token. The providers run an exec
# credential plugin that exchanges the runbook's Azure identity for an Entra token for the AKS server application
# 6dae42f8-4368-4678-94ff-3960e28e3630 (the same for every AKS cluster). The lifecycle identity holds Azure
# Kubernetes Service RBAC Cluster Admin on rg-platform-<tier>-aks (foundation).
#
# The runbooks run on Octopus dynamic workers, which reach the API server directly: no TLS-re-terminating proxy
# sits in between, so both providers trust the cluster's own CA from kube_config. (Operator sessions behind the
# proxy use the conformance harness's PLATFORM_TLS_SYSTEM_TRUST or `az aks command invoke`, never this layer.)
#
# kubelogin_login_mode must match how Terraform authenticates to Azure (variables.tf):
#   octopus-oidc      the Octopus Terraform step with an OIDC account (the committed tfvars). Calamari sets
#                     ARM_CLIENT_ID, ARM_TENANT_ID and ARM_OIDC_TOKEN and leaves the Azure CLI logged out
#                     (Calamari.Terraform TerraformDeployBehaviour, checked 2026-09-24), so scripts/aks-token.sh
#                     exchanges that token for an AKS token itself (curl and python3; no kubelogin needed).
#   azurecli          an operator session where the Azure CLI is logged in (break-glass runs of this layer).
#   workloadidentity  kubelogin reads AZURE_CLIENT_ID, AZURE_TENANT_ID and AZURE_FEDERATED_TOKEN_FILE.
#   spn, msi          kept for other hosts.
# The kubelogin modes need kubelogin on PATH (`az aks install-cli` installs it).
#
# The providers read the endpoint from azurerm_kubernetes_cluster.this, so the first apply of a tier may need
# two passes: -target=azurerm_kubernetes_cluster.this, then a full apply [VERIFY in P1-07]. The API server's IP
# address may change on a start; the FQDN in kube_config does not.

locals {
  kube_host = azurerm_kubernetes_cluster.this.kube_config[0].host
  kube_ca   = base64decode(azurerm_kubernetes_cluster.this.kube_config[0].cluster_ca_certificate)

  kube_exec = var.kubelogin_login_mode == "octopus-oidc" ? {
    command = "sh"
    args    = ["${path.module}/scripts/aks-token.sh"]
    } : {
    command = "kubelogin"
    args = [
      "get-token",
      "--login", var.kubelogin_login_mode,
      "--server-id", "6dae42f8-4368-4678-94ff-3960e28e3630",
      "--tenant-id", var.tenant_id,
    ]
  }
}

provider "kubernetes" {
  host                   = local.kube_host
  cluster_ca_certificate = local.kube_ca

  exec {
    api_version = "client.authentication.k8s.io/v1beta1"
    command     = local.kube_exec.command
    args        = local.kube_exec.args
  }
}

provider "helm" {
  kubernetes = {
    host                   = local.kube_host
    cluster_ca_certificate = local.kube_ca

    exec = {
      api_version = "client.authentication.k8s.io/v1beta1"
      command     = local.kube_exec.command
      args        = local.kube_exec.args
    }
  }
}
