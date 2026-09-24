# terraform/apps/descriptor: reads apps/<app>.yaml (schema apps/schema.json, design §7.0) and returns the values
# terraform/apps/tier and terraform/apps/grants act on, for one tier. A pure module: no provider, no resource.
#
# Keys read (every other key is ignored here; the tenant chart, octopus/terraform and the onboarding tool read
# them):
#   name                          must equal var.app
#   environments                  default [tdd, uat, prod]; intersected with the tier's fixed environments
#   database.engine               a database: SQL login passwords, disk-<app>-<env>-db, backup container
#   secrets[].name, .generate     app keys in the vault: random once, or a stand-in an operator replaces
#   octopus.projects[].name       subjects of id-<app>-<env>-deploy (one federated credential each, at most 20)
#   octopus.azureAccount          id-<app>-<env>-deploy (grants)
#   azure.resourceGroup           rg-app-<app>-<tier> (grants)
#   azure.workloadIdentity        id-<app>-<env>-app (grants) and its federated credential (tier)
#   azure.serviceAccount          subject system:serviceaccount:<app>-<env>:<serviceAccount>
#   azure.roles                   roles of id-<app>-<env>-app on rg-app-<app>-<tier>, from the allowed list
# `status: frozen` changes nothing here: disks, vaults and identities stay (ADR-IR34 decision 28).
#
# Vault name (ADR-IR34 decision 8): kv-<app>-<e>-<hash4>, <e> = t, u or p, hash4 = the first four hex digits of
# sha1("<AZURE_SUBSCRIPTION_ID>/<app>/<env>") with the subscription ID as a lowercase GUID. The tenant chart
# computes the same value; CAP-KIT-003 checks that both agree.

terraform {
  required_version = ">= 1.11.0"
}

variable "app" {
  description = "App slug (ADR-IR34 decision 10); apps/<app>.yaml must exist unless descriptor is given."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{2,11}$", var.app)) && !contains(["apps", "argo", "argocd", "cert", "default", "external", "infra", "kube", "kyverno", "octopus", "platform", "system"], var.app)
    error_message = "app must match ^[a-z][a-z0-9]{2,11}$ and not be a reserved word (ADR-IR34 decision 10); sandbox is the conformance fixture."
  }
}

variable "tier" {
  description = "nonprod or prod."
  type        = string

  validation {
    condition     = contains(["nonprod", "prod"], var.tier)
    error_message = "tier must be nonprod or prod."
  }
}

variable "subscription_id" {
  description = "Subscription ID (<AZURE_SUBSCRIPTION_ID>); part of the vault-name hash."
  type        = string
}

variable "descriptor" {
  description = "The decoded descriptor. Null reads apps/<app>.yaml of this repository; tests pass fixtures here."
  type        = any
  default     = null
}

locals {
  raw = var.descriptor != null ? var.descriptor : yamldecode(file("${path.module}/../../../apps/${var.app}.yaml"))

  all_envs  = ["tdd", "uat", "prod"]
  tier_envs = var.tier == "nonprod" ? ["tdd", "uat"] : ["prod"]
  env_code  = { tdd = "t", uat = "u", prod = "p" }

  declared_envs = try(tolist(local.raw.environments), local.all_envs)
  environments  = [for e in local.all_envs : e if contains(local.declared_envs, e) && contains(local.tier_envs, e)]

  subscription = lower(var.subscription_id)
  hash4        = { for e in local.environments : e => substr(sha1("${local.subscription}/${var.app}/${e}"), 0, 4) }
  vault_names  = { for e in local.environments : e => "kv-${var.app}-${local.env_code[e]}-${local.hash4[e]}" }

  database_engine = try(local.raw.database.engine, null)
  has_database    = local.database_engine != null

  secrets = [for s in try(tolist(local.raw.secrets), []) : {
    name     = s.name
    generate = try(tobool(s.generate), false)
  }]

  octopus_projects = distinct([for p in try(tolist(local.raw.octopus.projects), []) : try(p.name, p)])
  azure_account    = try(tobool(local.raw.octopus.azureAccount), false)

  resource_group    = try(tobool(local.raw.azure.resourceGroup), false)
  workload_identity = try(tobool(local.raw.azure.workloadIdentity), false)
  service_account   = try(local.raw.azure.serviceAccount, null)
  app_roles         = distinct(try(tolist(local.raw.azure.roles), []))

  # Roles the provisioner may assign (the ABAC condition of R6, ADR-IR34 Owner-script change) and the schema allows.
  allowed_app_roles = ["Reader", "Contributor", "Key Vault Secrets User", "Key Vault Secrets Officer", "Storage Blob Data Contributor"]
}

output "name" {
  description = "The app slug."
  value       = var.app

  precondition {
    condition     = try(local.raw.name, null) == var.app
    error_message = "The descriptor's name must equal the app (apps/<app>.yaml)."
  }
}

output "tier" {
  description = "The tier."
  value       = var.tier
}

output "environments" {
  description = "The app's environments in this tier, in the order tdd, uat, prod."
  value       = local.environments
}

output "status" {
  description = "active or frozen."
  value       = try(local.raw.status, "active")
}

output "vault_names" {
  description = "Vault name per environment: kv-<app>-<e>-<hash4>."
  value       = local.vault_names
}

output "database_engine" {
  description = "Database engine, or null without a database."
  value       = local.database_engine
}

output "has_database" {
  description = "true when the app declares a database."
  value       = local.has_database
}

output "secrets" {
  description = "App keys of the vault: name and whether Terraform generates the value."
  value       = local.secrets

  precondition {
    condition     = alltrue([for s in local.secrets : can(regex("^[a-z][a-z0-9-]{1,62}$", s.name)) && !startswith(s.name, "db-") && !contains(["appinsights-connection-string", "azure-client-id"], s.name)])
    error_message = "secrets[].name must match ^[a-z][a-z0-9-]{1,62}$ and must not use the db- prefix or a platform key."
  }
}

output "octopus_projects" {
  description = "Octopus project slugs of the app."
  value       = local.octopus_projects

  precondition {
    condition     = length(local.octopus_projects) <= 20
    error_message = "At most 20 Octopus projects per app: one federated credential each on id-<app>-<env>-deploy (ADR-IR34 decision 14)."
  }
}

output "azure_account" {
  description = "true: id-<app>-<env>-deploy and the Octopus account azure-<app>-<env>."
  value       = local.azure_account

  precondition {
    condition     = !local.azure_account || length(local.octopus_projects) > 0
    error_message = "octopus.azureAccount needs at least one octopus.projects entry."
  }
}

output "resource_group" {
  description = "true: rg-app-<app>-<tier> for app-owned Azure services."
  value       = local.resource_group
}

output "workload_identity" {
  description = "true: id-<app>-<env>-app for the app's pods."
  value       = local.workload_identity

  precondition {
    condition     = !local.workload_identity || can(regex("^[a-z]([a-z0-9-]{0,61}[a-z0-9])?$", local.service_account))
    error_message = "azure.workloadIdentity needs azure.serviceAccount, a valid Kubernetes name."
  }
}

output "service_account" {
  description = "Service account of the app's pods for the workload identity, or null."
  value       = local.service_account
}

output "app_roles" {
  description = "Roles of id-<app>-<env>-app on rg-app-<app>-<tier>."
  value       = local.app_roles

  precondition {
    condition     = alltrue([for r in local.app_roles : contains(local.allowed_app_roles, r)])
    error_message = "azure.roles must come from the allowed list: Reader, Contributor, Key Vault Secrets User, Key Vault Secrets Officer, Storage Blob Data Contributor."
  }

  precondition {
    condition     = length(local.app_roles) == 0 || (local.resource_group && local.workload_identity)
    error_message = "azure.roles needs azure.resourceGroup and azure.workloadIdentity."
  }
}
