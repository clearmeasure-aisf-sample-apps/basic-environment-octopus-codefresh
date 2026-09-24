# Outputs of one tier. None is secret. Consumers:
#   platform-infrastructure env-wake and env-sleep   cluster and rule names (also fixed by §7.0)
#   gitops (by pull request)                          apps_domain, ingress_ip_address, platform_key_vault_uri
#   terraform/apps/tier                               finds the workspace, the action group and the cluster by name
#   the conformance harness                           the same names, from tests/platform.settings.json

output "cluster_name" {
  description = "AKS cluster name (aks-platform-<tier>)."
  value       = azurerm_kubernetes_cluster.this.name
}

output "cluster_id" {
  description = "AKS cluster resource ID."
  value       = azurerm_kubernetes_cluster.this.id
}

output "cluster_resource_group_name" {
  description = "Resource group of the cluster (rg-platform-<tier>-aks), for the power-state commands of env-wake and env-sleep."
  value       = azurerm_kubernetes_cluster.this.resource_group_name
}

output "node_resource_group" {
  description = "Node resource group managed by AKS (rg-platform-<tier>-aks-nodes)."
  value       = azurerm_kubernetes_cluster.this.node_resource_group
}

output "oidc_issuer_url" {
  description = "Cluster OIDC issuer. Changes on rebuild: env-apply recreates the platform workload credentials, apps-apply those of app identities."
  value       = azurerm_kubernetes_cluster.this.oidc_issuer_url
}

output "platform_key_vault_uri" {
  description = "URI of <kv-platform-<tier>>, for ClusterSecretStore platform-keyvault."
  value       = azurerm_key_vault.platform.vault_uri
}

output "log_analytics_workspace_id" {
  description = "Resource ID of log-platform-<tier>."
  value       = azurerm_log_analytics_workspace.this.id
}

output "oncall_action_group_id" {
  description = "Resource ID of ag-platform-oncall in rg-platform-<tier>-aks."
  value       = azurerm_monitor_action_group.oncall.id
}

output "egress_ip_address" {
  description = "Static outbound IP of the cluster (pip-platform-<tier>-egress), for firewalls and authorized ranges."
  value       = azurerm_public_ip.egress.ip_address
}

output "ingress_ip_address" {
  description = "Static IP of gateway platform-gateway (pip-platform-<tier>-ingress)."
  value       = azurerm_public_ip.ingress.ip_address
}

output "apps_domain" {
  description = "<apps-domain-<tier>>: <ingress-ip-dashed-<tier>>.sslip.io, the default host suffix of <app>-<env> (ADR-IR34 decision 21)."
  value       = "${replace(azurerm_public_ip.ingress.ip_address, ".", "-")}.sslip.io"
}

# Sleep and wake (ADR-IR33): the names env-wake and env-sleep act on.
output "sleep_alert_suppression_rule_name" {
  description = "Alert processing rule apr-sleep-<tier>: env-sleep enables it before the stop, env-wake disables it after the start."
  value       = azurerm_monitor_alert_processing_rule_suppression.sleep.name
}

output "sleep_alert_suppression_rule_resource_group_name" {
  description = "Resource group of apr-sleep-<tier> (rg-platform-<tier>-aks)."
  value       = azurerm_monitor_alert_processing_rule_suppression.sleep.resource_group_name
}

output "octopus_worker_script_service_accounts" {
  description = "Service accounts of the script pods of pools k8s-<env> (namespace/name), for RoleBindings in platform-backup."
  value       = { for e in local.envs : e => "octopus-worker-${e}/octopus-worker-${e}-scripts" }
}
