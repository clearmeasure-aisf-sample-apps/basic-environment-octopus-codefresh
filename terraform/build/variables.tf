# Inputs of the build cluster. build.tfvars.example holds the non-secret live values; the operator copies
# it to build.auto.tfvars (untracked) and replaces the <placeholders>.

variable "tenant_id" {
  description = "Entra tenant ID (<AZURE_TENANT_ID>); also the tenant of the cluster's Entra integration."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", var.tenant_id))
    error_message = "tenant_id must be a GUID."
  }
}

variable "subscription_id" {
  description = "Subscription of the platform (<AZURE_SUBSCRIPTION_ID>)."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", var.subscription_id))
    error_message = "subscription_id must be a GUID."
  }
}

variable "kubernetes_version" {
  description = "AKS version of the control plane and both pools, for example 1.34. Null takes the default at creation. Automatic upgrades are off: an upgrade is a change of this value, applied while the builds pool is at zero."
  type        = string
  default     = null
}

variable "system_pool" {
  description = "Default pool 'system': the Codefresh Runner agent and the add-ons; always on, untainted. The OS disk is managed (a Standard_B2s has no temporary disk for an ephemeral one); 64 GiB keeps it at a small disk tier."
  type = object({
    vm_size         = string
    node_count      = number
    os_disk_size_gb = number
  })
  default = {
    vm_size         = "Standard_B2s"
    node_count      = 1
    os_disk_size_gb = 64
  }

  validation {
    condition     = var.system_pool.node_count >= 1
    error_message = "system_pool.node_count must be at least 1: the runner agent lives there."
  }
}

variable "builds_pool" {
  description = "User pool 'builds': engine and dind pods only. Scales from zero on the first job and back to zero after scale_down_unneeded (ADR-IR34). The OS disk is ephemeral on the VM's temporary disk (150 GiB on Standard_D4ds_v5), which holds the dind volumes."
  type = object({
    vm_size      = string
    min_count    = number
    max_count    = number
    os_disk_type = string
  })
  default = {
    vm_size      = "Standard_D4ds_v5"
    min_count    = 0
    max_count    = 2
    os_disk_type = "Ephemeral"
  }

  validation {
    condition     = var.builds_pool.min_count == 0 && var.builds_pool.max_count >= 1 && var.builds_pool.max_count <= 2
    error_message = "builds_pool scales 0 to at most 2 nodes: min_count must be 0 (sleep by default) and max_count 1 or 2 (vCPU budget of ADR-IR34 decision 9)."
  }

  validation {
    condition     = contains(["Ephemeral", "Managed"], var.builds_pool.os_disk_type)
    error_message = "builds_pool.os_disk_type must be Ephemeral or Managed."
  }
}

variable "scale_down_unneeded" {
  description = "Idle time after which the autoscaler removes a builds node (ADR-IR34: 10 minutes)."
  type        = string
  default     = "10m"
}

variable "api_server_authorized_ip_ranges" {
  description = "Public ranges allowed to reach the API server. Empty (P1, Q7) leaves it open; Entra ID with Azure RBAC and disabled local accounts still apply. The runner agent connects outbound to Codefresh and needs no inbound range."
  type        = list(string)
  default     = []
}

variable "tags" {
  description = "Extra tags. The cost tags platform-tier = build and platform-component = build-cluster (§7.0) and managed-by are always set and win."
  type        = map(string)
  default     = {}
}
