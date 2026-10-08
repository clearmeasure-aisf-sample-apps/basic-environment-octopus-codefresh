# Inputs of the foundation. foundation.tfvars.example holds the non-secret live values; the operator
# copies it to foundation.auto.tfvars (untracked) and replaces the <placeholders>. No input is secret:
# credentials come from the ARM_* environment variables of the provisioner session.

variable "tenant_id" {
  description = "Entra tenant ID (<AZURE_TENANT_ID>)."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", var.tenant_id))
    error_message = "tenant_id must be a GUID."
  }
}

variable "subscription_id" {
  description = "Subscription that holds every platform resource group (<AZURE_SUBSCRIPTION_ID>)."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", var.subscription_id))
    error_message = "subscription_id must be a GUID."
  }
}

variable "location" {
  description = "Azure region of every platform resource (<azure-region>)."
  type        = string
}

variable "name_suffix" {
  description = <<-EOT
    Suffix of the globally unique names: registry acrplatform + suffix, state accounts sttfglobal,
    sttfnonprod and sttfprod + suffix, backup accounts stbkpnonprod and stbkpprod + suffix. Chosen once
    and never changed: a new value would replace the registry and the state and backup accounts, which
    prevent_destroy refuses.
  EOT
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{3,7}$", var.name_suffix))
    error_message = "name_suffix must be 4 to 8 lowercase letters and digits, starting with a letter (storage account names stay within 24 characters)."
  }
}

variable "octopus_url" {
  description = "Octopus Cloud URL (<OCTOPUS_URL>). It is the OIDC issuer of the Octopus accounts and feeds, so it has no trailing slash (E21)."
  type        = string

  validation {
    condition     = startswith(var.octopus_url, "https://") && !endswith(var.octopus_url, "/")
    error_message = "octopus_url must start with https:// and have no trailing slash (E21)."
  }
}

variable "octopus_space_slug" {
  description = "Slug of the platform space (<octopus-space-slug>): the first segment of every Octopus OIDC subject."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9][a-z0-9-]*$", var.octopus_space_slug))
    error_message = "octopus_space_slug must be an Octopus slug: lowercase letters, digits and dashes."
  }
}

variable "provisioner_object_id" {
  description = "Object ID of the provisioner's service principal (sp-automation-mvp-sub), which receives the self-grants: Storage Blob Data Contributor on the global state account and AKS RBAC Cluster Admin on rg-platform-build. Null takes the principal that runs Terraform, which is the provisioner by design."
  type        = string
  default     = null

  validation {
    condition     = var.provisioner_object_id == null || can(regex("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", var.provisioner_object_id))
    error_message = "provisioner_object_id must be null or a GUID."
  }
}

variable "platform_operators_group_object_id" {
  description = "Object ID of the Entra group platform-operators, created and populated by the provisioner with az before the first apply (entra.tf, docs/bootstrap.md P1-02). Terraform only assigns roles to it."
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", var.platform_operators_group_object_id))
    error_message = "platform_operators_group_object_id must be a GUID."
  }
}

variable "provisioner_app_cluster_admin" {
  description = "P1 only: the provisioner holds Azure Kubernetes Service RBAC Cluster Admin on rg-platform-<tier>-aks, for seeding vaults and checking Argo CD. Set false after P1-13."
  type        = bool
  default     = true
}

variable "conformance_node_group_reader" {
  description = <<-EOT
    true once the three clusters exist (after P1-08): sp-platform-conformance holds Reader on their AKS node resource
    groups also before P1-03, for the node-pool reads of the conformance suite (the build pool back at zero,
    CAP-CF-003). The node groups exist only with their clusters, so the first apply (P1-02) keeps it false;
    conformance_least_privilege = true implies it.
  EOT
  type        = bool
  default     = false
}

variable "conformance_least_privilege" {
  description = <<-EOT
    false (default, interim): sp-platform-conformance holds Azure Kubernetes Service RBAC Cluster Admin
    on rg-platform-build, rg-platform-nonprod-aks and rg-platform-prod-aks.
    true (P1-03): it holds Azure Kubernetes Service Cluster User Role and Azure Kubernetes Service RBAC
    Reader on the three clusters, Azure Kubernetes Service RBAC Writer on namespaces sandbox-tdd,
    sandbox-uat and sandbox-prod, and Reader on the three AKS node resource groups. Set true only after
    the user re-ran the changed Owner script (those roles are not assignable before) and after
    aks-platform-build, aks-platform-nonprod and aks-platform-prod exist (the scopes must exist).
    The switch deletes the interim assignment in rg-platform-build. Role assignments are extension
    resources and inherit locks, so once the Owner script's -ApplyLocks has locked rg-platform-build
    Azure refuses that delete (ScopeLocked): switch before the lock, or let the Owner lift it for the
    apply (az lock delete --name platform-cannot-delete --resource-group rg-platform-build) and run
    -ApplyLocks again.
  EOT
  type        = bool
  default     = false
}

variable "budgets_enabled" {
  description = "Create the three budgets. Null (default) creates them unless the subscription's offer is one that Cost Management does not support (for example Microsoft Azure Sponsorship, quota ID Sponsored_2016-01-01), where the Budgets API refuses them [VERIFY]. true forces them; false skips them."
  type        = bool
  default     = null
}

variable "budget_amounts" {
  description = "Monthly budget per scope in the billing currency: about 1.2 times the §3.5 sleeping estimate at one app (R18). build covers rg-platform-global, rg-platform-build and rg-platform-build-aks-nodes. Raise them by pull request as apps are onboarded."
  type        = map(number)
  default = {
    build   = 100
    nonprod = 110
    prod    = 60
  }

  validation {
    condition     = length(setsubtract(toset(["build", "nonprod", "prod"]), toset(keys(var.budget_amounts)))) == 0 && length(var.budget_amounts) == 3
    error_message = "budget_amounts needs exactly the keys build, nonprod and prod."
  }

  validation {
    condition     = alltrue([for v in values(var.budget_amounts) : v > 0])
    error_message = "Every budget amount must be greater than zero."
  }
}

variable "budget_contact_emails" {
  description = "Extra recipients of budget notifications. The subscription's Owners are always notified (contact role Owner), so this may stay empty."
  type        = list(string)
  default     = []
}

variable "backup_retention_days" {
  description = "Age in days after which the lifecycle policy of the backup accounts deletes database backups (RPO 24 hours, restores from the latest backup, ADR-IR34 risk 7)."
  type        = number
  default     = 30

  validation {
    condition     = var.backup_retention_days >= 7
    error_message = "backup_retention_days must be at least 7."
  }
}

variable "tags" {
  description = "Extra tags on every tagged resource. The cost tags platform-tier and platform-component (§7.0) and managed-by are always set and win over these."
  type        = map(string)
  default     = {}
}

variable "dashboard_repository" {
  description = "GitHub repository (<owner>/<name>) of the health dashboard. The workflows of its main branch sign in as id-dashboard-status (federation.tf)."
  type        = string
  default     = "clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh-dashboard"

  validation {
    condition     = can(regex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", var.dashboard_repository))
    error_message = "dashboard_repository must be <owner>/<name>."
  }
}

variable "dashboard_repository_ids" {
  description = "GitHub's numeric IDs of the dashboard repository and of its owner. The repository presents an immutable subject in its Actions token (repo:<owner>@<owner id>/<name>@<repository id>:..., GitHub's default for new repositories), and the federated credential has to name exactly that (federation.tf). Read them with: gh api repos/<owner>/<name> --jq '{owner: .owner.id, repository: .id}'."
  type = object({
    owner      = number
    repository = number
  })
  default = {
    owner      = 267457105
    repository = 1408108211
  }

  validation {
    condition     = var.dashboard_repository_ids.owner > 0 && var.dashboard_repository_ids.repository > 0
    error_message = "dashboard_repository_ids.owner and .repository must be GitHub's positive numeric IDs."
  }
}
