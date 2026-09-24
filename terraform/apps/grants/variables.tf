# Inputs of the provisioner's per-app layer: -var-file=grants.tfvars (copied from grants.tfvars.example in the
# operator session, identifiers only) and -var=app=<app>.

variable "app" {
  description = "App slug; apps/<app>.yaml must exist (ADR-IR34 decision 10)."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{2,11}$", var.app))
    error_message = "app must match ^[a-z][a-z0-9]{2,11}$."
  }
}

variable "tenant_id" {
  description = "Entra tenant ID (<AZURE_TENANT_ID>)."
  type        = string
}

variable "subscription_id" {
  description = "Subscription ID (<AZURE_SUBSCRIPTION_ID>) as a lowercase GUID; part of the vault-name hash."
  type        = string
}

variable "location" {
  description = "Azure region (<azure-region>) of the identities and of rg-app-<app>-<tier>."
  type        = string
}

variable "octopus_url" {
  description = "Issuer of the Octopus OIDC tokens: <OCTOPUS_URL>, without a trailing slash."
  type        = string

  validation {
    condition     = startswith(var.octopus_url, "https://") && !endswith(var.octopus_url, "/")
    error_message = "octopus_url must start with https:// and have no trailing slash (it must equal the token issuer)."
  }
}

variable "octopus_space_slug" {
  description = "Slug of the platform space (<octopus-space-slug>), the first part of every deploy subject."
  type        = string
}

variable "conformance_principal_object_id" {
  description = "Object ID of the service principal of sp-platform-conformance (foundation output). Null skips the conformance grants."
  type        = string
  default     = null
}

variable "tags" {
  description = "Extra tags merged into every resource."
  type        = map(string)
  default     = {}
}

variable "descriptor" {
  description = "Test hook: a decoded descriptor used instead of apps/<app>.yaml. Leave null in real runs."
  type        = any
  default     = null
}
