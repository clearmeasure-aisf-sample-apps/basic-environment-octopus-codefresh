#!/usr/bin/env bash
# Sourced by the conformance scripts: AKS power state of the app clusters through Azure Resource
# Manager, read with sp-platform-conformance (context platform-conformance; read access to the
# clusters). Cluster names and groups come from tests/platform.settings.json (Tiers.<tier>);
# PLATFORM_SETTINGS_FILE, AZURE_SUBSCRIPTION_ID and AZURE_TENANT_ID override it as in the harness.
#
#   aks_power_init <private dir>   0 when ARM is readable; 1 (with a warning) when settings hold
#                                  placeholders or credentials are missing
#   aks_power_state <tier>         prints <powerState>/<provisioningState>, for example
#                                  Stopped/Succeeded; "unconfigured" while Tiers.<tier> holds
#                                  placeholders; "unknown" when ARM is not readable
# The client secret and the token only ever sit in files of the private dir (mode 0600).

aks_power_settings="${PLATFORM_SETTINGS_FILE:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)/tests/platform.settings.json}"
aks_power_dir=""
aks_power_subscription=""

aks_power_warn() {
  printf 'aks-power: WARN %s\n' "$1" >&2
}

aks_power_init() {
  local dir="${1:?aks_power_init needs a private directory}" tenant subscription token
  [ -r "$aks_power_settings" ] || { aks_power_warn "no $aks_power_settings"; return 1; }
  tenant="${AZURE_TENANT_ID:-$(jq -r '.AzureTenantId // empty' "$aks_power_settings")}"
  subscription="${AZURE_SUBSCRIPTION_ID:-$(jq -r '.AzureSubscriptionId // empty' "$aks_power_settings")}"
  if [ -z "$tenant" ] || [ -z "$subscription" ] || [ -z "${AZURE_CLIENT_ID:-}" ] || [ -z "${AZURE_CLIENT_SECRET:-}" ]; then
    aks_power_warn "no Azure credentials or IDs (platform-conformance, settings)"
    return 1
  fi
  case "${tenant}${subscription}${AZURE_CLIENT_ID}" in
    *'<'*'>'*) aks_power_warn "Azure settings hold placeholders"; return 1 ;;
  esac
  (umask 077 && printf '%s' "$AZURE_CLIENT_SECRET" >"$dir/azure-secret")
  token="$(curl -fsS --max-time 60 -X POST "https://login.microsoftonline.com/${tenant}/oauth2/v2.0/token" \
    --data-urlencode grant_type=client_credentials --data-urlencode "client_id=${AZURE_CLIENT_ID}" \
    --data-urlencode "client_secret@$dir/azure-secret" \
    --data-urlencode scope=https://management.azure.com/.default | jq -r '.access_token // empty')" || token=""
  rm -f -- "$dir/azure-secret"
  [ -n "$token" ] || { aks_power_warn "Entra ID token request failed"; return 1; }
  (umask 077 && printf 'Authorization: Bearer %s\n' "$token" >"$dir/arm-headers")
  aks_power_dir="$dir"
  aks_power_subscription="$subscription"
}

aks_power_state() {
  local tier="$1" group name out
  [ -n "$aks_power_dir" ] || { printf 'unknown\n'; return 0; }
  group="$(jq -r --arg t "$tier" '.Tiers[$t].ResourceGroup // empty' "$aks_power_settings")"
  name="$(jq -r --arg t "$tier" '.Tiers[$t].ClusterName // empty' "$aks_power_settings")"
  case "$group$name" in '' | *'<'*'>'*) printf 'unconfigured\n'; return 0 ;; esac
  out="$(curl -fsS --max-time 60 -H @"$aks_power_dir/arm-headers" \
    "https://management.azure.com/subscriptions/${aks_power_subscription}/resourceGroups/${group}/providers/Microsoft.ContainerService/managedClusters/${name}?api-version=2024-10-01" |
    jq -r '"\(.properties.powerState.code // "unknown")/\(.properties.provisioningState // "unknown")"' 2>/dev/null)" || out=""
  printf '%s\n' "${out:-unknown}"
}
