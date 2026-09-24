# Inputs of one tier. The runbooks run Terraform from the environment repository and pass
# -var-file=<tier>.tfvars, which is committed next to this file (ADR-IR14). It holds identifiers and sizing
# only. The two sensitive, ephemeral variables at the end arrive as TF_VAR_<name> from Octopus sensitive
# variables of platform-infrastructure.

variable "tier" {
  description = "Tier: nonprod (tdd, uat, previews) or prod (prod). Selects the names, the environments and argocd/bootstrap/*-<tier>.yaml."
  type        = string

  validation {
    condition     = contains(["nonprod", "prod"], var.tier)
    error_message = "tier must be nonprod or prod."
  }
}

variable "tenant_id" {
  description = "Entra tenant ID (<AZURE_TENANT_ID>)."
  type        = string
}

variable "subscription_id" {
  description = "Subscription ID (<AZURE_SUBSCRIPTION_ID>)."
  type        = string
}

variable "location" {
  description = "Azure region (<azure-region>); the same as the foundation."
  type        = string
}

variable "platform_key_vault_name" {
  description = "Platform vault of the tier (<kv-platform-<tier>>): globally unique, 3 to 24 characters."
  type        = string

  validation {
    condition     = can(regex("^[a-zA-Z][a-zA-Z0-9-]{1,22}[a-zA-Z0-9]$", var.platform_key_vault_name))
    error_message = "platform_key_vault_name must be a valid Key Vault name (3 to 24 letters, digits and dashes)."
  }
}

# --- Network (rg-platform-<tier>-shared) -------------------------------------------------------------------

variable "network" {
  description = "vnet-platform-<tier> and its subnet snet-aks-<tier>. The tiers use non-overlapping ranges and are never peered, so a VPN stays possible."
  type = object({
    address_space     = list(string)
    aks_subnet_prefix = string
  })
}

variable "cluster_network" {
  description = "Azure CNI overlay ranges. They stay inside the cluster, so both tiers may use the same values."
  type = object({
    pod_cidr       = string
    service_cidr   = string
    dns_service_ip = string
  })
  default = {
    pod_cidr       = "192.168.0.0/16"
    service_cidr   = "172.16.0.0/16"
    dns_service_ip = "172.16.0.10"
  }
}

variable "api_server_authorized_ip_ranges" {
  description = "Public ranges allowed to reach the API server. Empty (P1, Q7) leaves the endpoint open; Entra ID with Azure RBAC and disabled local accounts still apply."
  type        = list(string)
  default     = []
}

# --- Cluster (ADR-IR34 "Clusters and capacity") ------------------------------------------------------------

variable "kubernetes_version" {
  description = "AKS version, for example 1.34. Null takes the current default at creation. Automatic upgrades are off: an upgrade is a pull request that changes this value, applied while the builds pool is at zero."
  type        = string
  default     = null
}

variable "system_node_pool" {
  description = "Default pool 'system': platform add-ons only (taint CriticalAddonsOnly=true:NoSchedule), fixed size."
  type = object({
    vm_size    = string
    node_count = number
  })
  default = {
    vm_size    = "Standard_D4as_v6"
    node_count = 1
  }
}

variable "apps_node_pool" {
  description = "User pool 'apps': app workloads, databases and the Octopus Kubernetes workers. The maximum is 7 in nonprod and 4 in prod (ADR-IR34 decision 9: 62 of 65 regional vCPUs with the build cluster)."
  type = object({
    vm_size   = string
    min_count = number
    max_count = number
  })
  default = {
    vm_size   = "Standard_D4as_v6"
    min_count = 1
    max_count = 4
  }

  validation {
    condition     = var.apps_node_pool.min_count >= 1 && var.apps_node_pool.min_count <= var.apps_node_pool.max_count
    error_message = "apps_node_pool needs 1 <= min_count <= max_count."
  }

  validation {
    condition     = var.apps_node_pool.max_count <= (var.tier == "prod" ? 4 : 7)
    error_message = "apps_node_pool.max_count exceeds the vCPU budget of ADR-IR34 decision 9: at most 7 nodes in nonprod and 4 in prod. More needs the R34 quota request."
  }
}

variable "os_disk_size_gb" {
  description = "Managed OS disk size of both pools (Standard SSD billing continues while a cluster is stopped; 64 GiB keeps it small)."
  type        = number
  default     = 64
}

# --- Platform vault ----------------------------------------------------------------------------------------

variable "key_vault_allowed_ip_ranges" {
  description = "Public ranges allowed to the platform vault's data plane. Empty leaves the firewall open (Entra RBAC still applies). When set, the tier's egress IP is added so ESO keeps working."
  type        = list(string)
  default     = []
}

# --- Monitoring ----------------------------------------------------------------------------------------------

variable "log_retention_days" {
  description = "Retention of log-platform-<tier> in days (31 days are included in the ingestion price)."
  type        = number
  default     = 30
}

variable "oncall_email_receivers" {
  description = "Receivers of ag-platform-oncall in this tier: name => e-mail address."
  type        = map(string)
  default     = {}
}

# --- Octopus Kubernetes workers (ADR-D14; pools k8s-<env>, shared by every app, ADR-IR34 decision 18) --------

variable "octopus_url" {
  description = "Octopus Cloud URL (<OCTOPUS_URL>), no trailing slash."
  type        = string
}

variable "octopus_space" {
  description = "Name of the platform space (<octopus-space>); the chart registers the workers there."
  type        = string
}

variable "octopus_worker_chart_version" {
  description = "Initial version of the Octopus kubernetes-agent chart (<kubernetes-agent-chart-version>; 3.15.1 on 2026-09-24). Octopus upgrades the workers afterwards, so later changes are ignored."
  type        = string
}

variable "octopus_worker_machine_policy" {
  description = "Machine policy the workers register with (chart value agent.machinePolicyName): the sleep-tolerant policy of octopus/terraform (ADR-IR33, S9)."
  type        = string
  default     = "Sleep-tolerant Kubernetes workers"
}

# --- Argo CD bootstrap (ADR-D3) ------------------------------------------------------------------------------

variable "argocd_chart_version" {
  description = "argo/argo-cd chart version (<argo-cd-chart-version>; 10.9.2 carries Argo CD 3.5.3). Must equal the pin in argocd/clusters/<tier>/addons/argocd.yaml so self-management does not change the version."
  type        = string
}

variable "argocd_apps_chart_version" {
  description = "argo/argocd-apps chart version (<argocd-apps-chart-version>; 2.0.5 on 2026-09-24)."
  type        = string
}

variable "env_repo_url" {
  description = "Environment repository (<ENV_REPO_URL>)."
  type        = string
}

variable "argocd_repo_private" {
  description = "The environment repository is private, so Argo CD needs a credential. Keep this constant: switching it to false deletes the bootstrap Secret."
  type        = bool
  default     = true
}

variable "kubelogin_login_mode" {
  description = "How the Kubernetes and Helm providers get Entra tokens (providers.tf): octopus-oidc in the Octopus Terraform steps, azurecli in operator sessions."
  type        = string
  default     = "octopus-oidc"

  validation {
    condition     = contains(["octopus-oidc", "azurecli", "spn", "workloadidentity", "msi"], var.kubelogin_login_mode)
    error_message = "kubelogin_login_mode must be octopus-oidc, azurecli, spn, workloadidentity or msi."
  }
}

variable "tags" {
  description = "Extra tags merged into every resource. platform-tier and platform-component are always set (§7.0)."
  type        = map(string)
  default     = {}
}

# --- Sensitive, ephemeral inputs -----------------------------------------------------------------------------
# Ephemeral variables never enter the plan or the state; they feed write-only arguments only. Supply them on the
# runs that install something, from Octopus sensitive variables of platform-infrastructure.

variable "octopus_worker_registration_token" {
  description = "Octopus.WorkerRegistrationToken (TF_VAR_octopus_worker_registration_token): short-lived bearer token that registers the Kubernetes workers. Needed only on the run that installs or replaces a worker; null or empty sends nothing."
  type        = string
  default     = null
  sensitive   = true
  ephemeral   = true
}

variable "argocd_repo_read_credential" {
  description = "ArgoCD.RepoReadCredential (TF_VAR_argocd_repo_read_credential): the environment-repo read credential, used once to seed Secret argocd/argocd-repo-creds before ESO exists. The same JSON as vault secret argocd-repo-read-credential: {\"username\": ..., \"password\": ...} for a token (the stored PAT until R11, ADR-IR34 risk 4) or the GitHub App fields. Null or empty skips it."
  type        = string
  default     = null
  sensitive   = true
  ephemeral   = true
}

locals {
  # The tier-to-environment map is fixed (§7.0).
  envs = var.tier == "nonprod" ? ["tdd", "uat"] : ["prod"]

  # §7.0 names.
  rg_shared    = "rg-platform-${var.tier}-shared"
  rg_aks       = "rg-platform-${var.tier}-aks"
  rg_apps      = "rg-platform-${var.tier}-apps"
  rg_nodes     = "rg-platform-${var.tier}-aks-nodes"
  cluster_name = "aks-platform-${var.tier}"

  # Tags of §7.0: platform-tier and platform-component on every platform resource.
  base_tags = merge(var.tags, {
    "platform-tier" = var.tier
    "managed-by"    = "terraform-tier"
  })

  federation_audience = "api://AzureADTokenExchange"
}
