# Every platform role assignment (§7.0 "Identities"; ADR-IR34 decision 3). The provisioner is the only
# identity that creates role assignments; terraform/build, terraform/tier and terraform/apps/tier hold
# none, and the boundary lint rejects one there.
#
# Rules:
#   - Grants that must reach resources created later (clusters, vaults, disks, IPs, workspaces) are made
#     at resource-group scope in advance (§5.4).
#   - Grants go to identities directly, never through groups, because group membership of managed
#     identities lags. The one group is platform-operators, whose member is the user.
#   - principal_type is always set: the Owner script's ABAC condition admits a request only when its
#     principal type is ServicePrincipal or Group, and a stated type lets Azure accept a principal
#     created seconds earlier (the provider retries PrincipalNotFound meanwhile).
#   - Only roles in local.assignable_roles; a precondition stops the plan before Azure refuses one.
#   - The build cluster's own identities get nothing (CAP-CF-012, TB2).

locals {
  provisioner_object_id = coalesce(var.provisioner_object_id, data.azurerm_client_config.current.object_id)

  # The Owner script's role list today (live fact 2026-09-24, ten roles).
  roles_assignable_today = [
    "AcrPull",
    "Reader",
    "Contributor",
    "Key Vault Secrets Officer",
    "Key Vault Secrets User",
    "Storage Blob Data Contributor",
    "Azure Kubernetes Service RBAC Cluster Admin",
    "SQL DB Contributor",
    "Network Contributor",
    "Managed Identity Operator",
  ]

  # The list after the ADR-IR34 Owner-script change (P1-03): SQL DB Contributor out, three AKS roles in.
  roles_assignable_after_p103 = concat(
    [for r in local.roles_assignable_today : r if r != "SQL DB Contributor"],
    [
      "Azure Kubernetes Service Cluster User Role",
      "Azure Kubernetes Service RBAC Reader",
      "Azure Kubernetes Service RBAC Writer",
    ],
  )

  assignable_roles = var.conformance_least_privilege ? local.roles_assignable_after_p103 : local.roles_assignable_today

  rg_id = { for name, rg in azurerm_resource_group.this : name => rg.id }

  tier_principal = {
    for t in local.tiers : t => {
      for role, name in local.tier_identity_names[t] : role => azurerm_user_assigned_identity.tier[name].principal_id
    }
  }

  conformance_principal = azuread_service_principal.conformance.object_id
  operators_principal   = var.platform_operators_group_object_id

  # The sandbox namespaces the conformance principal may write (§7.0), with their cluster.
  conformance_writer_namespaces = {
    "sandbox-tdd"  = "nonprod"
    "sandbox-uat"  = "nonprod"
    "sandbox-prod" = "prod"
  }

  grants = merge(
    # --- Provisioner: the two self-grants of §7.0 ------------------------------------------------------
    {
      # foundation.tfstate, build.tfstate, app-grants-<app>.tfstate and octopus-space.tfstate.
      "provisioner-state-global-blob-contributor" = {
        scope     = azurerm_storage_account.tfstate["global"].id
        role      = "Storage Blob Data Contributor"
        principal = local.provisioner_object_id
        type      = "ServicePrincipal"
      }
      # Helm install of cf-runtime on aks-platform-build (P1-04), inherited by the cluster.
      "provisioner-build-aks-cluster-admin" = {
        scope     = local.rg_id[local.rg_build]
        role      = "Azure Kubernetes Service RBAC Cluster Admin"
        principal = local.provisioner_object_id
        type      = "ServicePrincipal"
      }
    },

    # --- Provisioner on the app clusters, P1 only (var.provisioner_app_cluster_admin) -------------------
    # Seeding platform vaults and checking Argo CD during provisioning. Membership of platform-operators
    # does not work for a service principal: AKS Azure RBAC reads group claims from the token, and service
    # principal tokens carry none (first live run, 2026-09-24). Set false after P1-13.
    { for t in(var.provisioner_app_cluster_admin ? local.tiers : []) :
      "provisioner-${t}-aks-cluster-admin" => {
        scope     = local.rg_id[local.rg_tier[t].aks]
        role      = "Azure Kubernetes Service RBAC Cluster Admin"
        principal = local.provisioner_object_id
        type      = "ServicePrincipal"
      }
    },

    # --- id-platform-lifecycle-<tier>: all tier automation, its own tier only ---------------------------
    merge([
      for t in local.tiers : {
        for part in ["shared", "aks", "data", "apps"] : "lifecycle-${t}-contributor-${part}" => {
          scope     = local.rg_id[local.rg_tier[t][part]]
          role      = "Contributor"
          principal = local.tier_principal[t].lifecycle
          type      = "ServicePrincipal"
        }
      }
    ]...),
    merge([
      for t in local.tiers : {
        # Helm and Kubernetes providers of terraform/tier (Argo CD bootstrap, Octopus workers).
        "lifecycle-${t}-aks-cluster-admin" = {
          scope     = local.rg_id[local.rg_tier[t].aks]
          role      = "Azure Kubernetes Service RBAC Cluster Admin"
          principal = local.tier_principal[t].lifecycle
          type      = "ServicePrincipal"
        }
        # Contributor cannot write secrets into an RBAC vault: the platform vault (-aks) and the app vaults
        # with their generated passwords (-apps).
        "lifecycle-${t}-kv-secrets-officer-aks" = {
          scope     = local.rg_id[local.rg_tier[t].aks]
          role      = "Key Vault Secrets Officer"
          principal = local.tier_principal[t].lifecycle
          type      = "ServicePrincipal"
        }
        "lifecycle-${t}-kv-secrets-officer-apps" = {
          scope     = local.rg_id[local.rg_tier[t].apps]
          role      = "Key Vault Secrets Officer"
          principal = local.tier_principal[t].lifecycle
          type      = "ServicePrincipal"
        }
        # tier-<tier>.tfstate and apps-<app>.tfstate.
        "lifecycle-${t}-state-blob-contributor" = {
          scope     = azurerm_storage_account.tfstate[t].id
          role      = "Storage Blob Data Contributor"
          principal = local.tier_principal[t].lifecycle
          type      = "ServicePrincipal"
        }
      }
    ]...),

    # --- AKS control plane and kubelet of aks-platform-<tier> -------------------------------------------
    merge([
      for t in local.tiers : {
        # Subnet join and the static egress and ingress IPs in -shared.
        "controlplane-${t}-network-contributor-shared" = {
          scope     = local.rg_id[local.rg_tier[t].shared]
          role      = "Network Contributor"
          principal = local.tier_principal[t].controlplane
          type      = "ServicePrincipal"
        }
        "controlplane-${t}-mi-operator-kubelet" = {
          scope     = azurerm_user_assigned_identity.tier["id-aks-${t}-kubelet"].id
          role      = "Managed Identity Operator"
          principal = local.tier_principal[t].controlplane
          type      = "ServicePrincipal"
        }
        # The disk CSI driver runs as the control-plane identity and needs Contributor on a disk group
        # outside the node group (verification §9).
        "controlplane-${t}-contributor-data" = {
          scope     = local.rg_id[local.rg_tier[t].data]
          role      = "Contributor"
          principal = local.tier_principal[t].controlplane
          type      = "ServicePrincipal"
        }
        # Node image pulls; prod's pull is the one recorded cross-tier read (ADR-IR34).
        "kubelet-${t}-acrpull" = {
          scope     = azurerm_container_registry.this.id
          role      = "AcrPull"
          principal = local.tier_principal[t].kubelet
          type      = "ServicePrincipal"
        }
      }
    ]...),

    # --- Platform workload identities -----------------------------------------------------------------------
    merge([
      for t in local.tiers : {
        # Kyverno reads signatures and SBOMs from the registry.
        "kyverno-${t}-acrpull" = {
          scope     = azurerm_container_registry.this.id
          role      = "AcrPull"
          principal = local.tier_principal[t].kyverno
          type      = "ServicePrincipal"
        }
        # ESO reads the platform vault (-aks) and every app vault of the tier (-apps), only through the
        # per-app ClusterSecretStores (ADR-IR34 decision 6).
        "eso-${t}-kv-secrets-user-aks" = {
          scope     = local.rg_id[local.rg_tier[t].aks]
          role      = "Key Vault Secrets User"
          principal = local.tier_principal[t].eso
          type      = "ServicePrincipal"
        }
        "eso-${t}-kv-secrets-user-apps" = {
          scope     = local.rg_id[local.rg_tier[t].apps]
          role      = "Key Vault Secrets User"
          principal = local.tier_principal[t].eso
          type      = "ServicePrincipal"
        }
        # Backup and restore Jobs in platform-backup: every <app>-<env> container of the tier's account.
        "db-backup-${t}-blob-contributor" = {
          scope     = azurerm_storage_account.backup[t].id
          role      = "Storage Blob Data Contributor"
          principal = local.tier_principal[t].db_backup
          type      = "ServicePrincipal"
        }
      }
    ]...),

    # --- Octopus feed acr-apps --------------------------------------------------------------------------------
    {
      "octopus-acr-pull-acrpull" = {
        scope     = azurerm_container_registry.this.id
        role      = "AcrPull"
        principal = azurerm_user_assigned_identity.octopus_acr_pull.principal_id
        type      = "ServicePrincipal"
      }
    },

    # --- sp-platform-conformance -------------------------------------------------------------------------------
    # Reads every platform group; the node groups follow once the clusters exist (conformance_node_group_reader,
    # or least-privilege mode). The sandbox tdd vault grant comes from terraform/apps/grants (P1-09).
    {
      for name in keys(local.resource_groups) : "conformance-reader-${name}" => {
        scope     = local.rg_id[name]
        role      = "Reader"
        principal = local.conformance_principal
        type      = "ServicePrincipal"
      }
    },
    {
      "conformance-acrpull" = {
        scope     = azurerm_container_registry.this.id
        role      = "AcrPull"
        principal = local.conformance_principal
        type      = "ServicePrincipal"
      }
    },
    # Interim (conformance_least_privilege = false): cluster admin on the three cluster groups.
    {
      for k, c in local.clusters : "conformance-aks-cluster-admin-${k}" => {
        scope     = local.rg_id[c.resource_group_name]
        role      = "Azure Kubernetes Service RBAC Cluster Admin"
        principal = local.conformance_principal
        type      = "ServicePrincipal"
      } if !var.conformance_least_privilege
    },
    # Least privilege (P1-03): cluster-scope reads, the namespace-scoped Writer, node-group reads.
    {
      for k, c in local.clusters : "conformance-aks-cluster-user-${k}" => {
        scope     = local.cluster_ids[k]
        role      = "Azure Kubernetes Service Cluster User Role"
        principal = local.conformance_principal
        type      = "ServicePrincipal"
      } if var.conformance_least_privilege
    },
    {
      for k, c in local.clusters : "conformance-aks-rbac-reader-${k}" => {
        scope     = local.cluster_ids[k]
        role      = "Azure Kubernetes Service RBAC Reader"
        principal = local.conformance_principal
        type      = "ServicePrincipal"
      } if var.conformance_least_privilege
    },
    {
      # Q47 [VERIFY]: the scope may precede the namespace; otherwise apply after the tenant creates it.
      for ns, k in local.conformance_writer_namespaces : "conformance-aks-rbac-writer-${ns}" => {
        scope     = "${local.cluster_ids[k]}/namespaces/${ns}"
        role      = "Azure Kubernetes Service RBAC Writer"
        principal = local.conformance_principal
        type      = "ServicePrincipal"
      } if var.conformance_least_privilege
    },
    {
      # Also before P1-03 once the clusters exist (conformance_node_group_reader).
      for k, c in local.clusters : "conformance-reader-${c.node_resource_group}" => {
        scope     = local.node_resource_group_ids[k]
        role      = "Reader"
        principal = local.conformance_principal
        type      = "ServicePrincipal"
      } if var.conformance_least_privilege || var.conformance_node_group_reader
    },

    # --- platform-operators (the user) --------------------------------------------------------------------------
    merge([
      for t in local.tiers : {
        for part in ["aks", "apps"] : "platform-operators-kv-secrets-officer-${t}-${part}" => {
          scope     = local.rg_id[local.rg_tier[t][part]]
          role      = "Key Vault Secrets Officer"
          principal = local.operators_principal
          type      = "Group"
        }
      }
    ]...),
    {
      for k, c in local.clusters : "platform-operators-aks-cluster-admin-${k}" => {
        scope     = local.rg_id[c.resource_group_name]
        role      = "Azure Kubernetes Service RBAC Cluster Admin"
        principal = local.operators_principal
        type      = "Group"
      }
    },
  )
}

# Role definitions by name, read once at subscription scope. The ID works at every scope, including the
# namespace scopes of the least-privilege Writer grants.
data "azurerm_role_definition" "this" {
  for_each = toset([for g in values(local.grants) : g.role])

  name  = each.key
  scope = local.subscription_resource_id
}

resource "azurerm_role_assignment" "this" {
  for_each = local.grants

  scope              = each.value.scope
  role_definition_id = data.azurerm_role_definition.this[each.value.role].id
  principal_id       = each.value.principal
  principal_type     = each.value.type
  description        = "platform: ${each.key} (terraform/foundation)"

  lifecycle {
    precondition {
      condition     = contains(local.assignable_roles, each.value.role)
      error_message = "Role '${each.value.role}' is not in the provisioner's assignable list. The three AKS roles of conformance_least_privilege need the changed Owner script first (P1-03)."
    }
  }
}
