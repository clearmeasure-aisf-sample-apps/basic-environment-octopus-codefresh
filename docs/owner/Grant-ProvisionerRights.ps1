<#
.SYNOPSIS
    One-time grant, run by an Azure subscription Owner, that lets the service principal behind the Octopus
    account "Azure Runtime Provisioner" apply terraform/foundation and terraform/environment itself.

.DESCRIPTION
    Part 1 (needs subscription Owner or User Access Administrator):
      - Registers the resource providers the platform uses.
      - Grants the service principal "Role Based Access Control Administrator" on the subscription with an
        ABAC condition: it may create or delete role assignments ONLY for the ten built-in roles that
        terraform/foundation and terraform/environment assign, and only to service principals (managed
        identities included) and groups. It can never grant Owner, User Access Administrator or RBAC
        Administrator, so it cannot escalate itself.
    Part 2 (needs an Entra role: Privileged Role Administrator, or Global Administrator):
      - Grants the service principal Microsoft Graph application permissions Group.Create and
        Application.ReadWrite.OwnedBy, with admin consent. It can then create the SQL-admin and Argo CD
        groups and app registrations in terraform/foundation, and manage only objects it owns.
      Skip Part 2 with -SkipEntra if an Entra admin creates those objects instead.

    Idempotent: re-running skips grants that already exist.

.EXAMPLE
    az login   # as the subscription Owner
    ./Grant-ProvisionerRights.ps1 -SubscriptionId <subscription-id> -ProvisionerAppId <app-id>

.NOTES
    Requires Azure CLI (az) 2.60 or later, and PowerShell 7 or Windows PowerShell 5.1.
    Revoke later with:
      az role assignment delete --assignee <app-id> --role "Role Based Access Control Administrator" --scope /subscriptions/<subscription-id>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SubscriptionId,
    [Parameter(Mandatory)] [string] $ProvisionerAppId,
    [switch] $SkipEntra
)

$ErrorActionPreference = 'Stop'

function Invoke-Az {
    # Runs az.exe, fails on a non-zero exit code, and returns stdout.
    $output = & az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed with exit code $LASTEXITCODE" }
    return $output
}

Write-Host "== Context"
Invoke-Az account set --subscription $SubscriptionId | Out-Null
$account = Invoke-Az account show --output json | ConvertFrom-Json
Write-Host "Signed in as $($account.user.name) on subscription '$($account.name)'"
$scope = "/subscriptions/$SubscriptionId"
$spObjectId = (Invoke-Az ad sp show --id $ProvisionerAppId --query id --output tsv).Trim()
Write-Host "Provisioner service principal object ID: $spObjectId"

Write-Host "== Part 1a: resource providers"
$providers = @(
    'Microsoft.ContainerService', 'Microsoft.ContainerRegistry', 'Microsoft.KeyVault', 'Microsoft.Sql',
    'Microsoft.OperationalInsights', 'Microsoft.Insights', 'Microsoft.Monitor', 'Microsoft.ManagedIdentity',
    'Microsoft.Storage', 'Microsoft.Network', 'Microsoft.Authorization', 'Microsoft.Dashboard'
)
foreach ($p in $providers) {
    Invoke-Az provider register --namespace $p --subscription $SubscriptionId | Out-Null
    Write-Host "registered (or registering): $p"
}

Write-Host "== Part 1b: constrained Role Based Access Control Administrator"
# The only roles terraform/foundation and terraform/environment assign.
$assignableRoles = @(
    'AcrPull', 'Reader', 'Contributor', 'Key Vault Secrets Officer', 'Key Vault Secrets User',
    'Storage Blob Data Contributor', 'Azure Kubernetes Service RBAC Cluster Admin', 'SQL DB Contributor',
    'Network Contributor', 'Managed Identity Operator'
)
$roleGuids = foreach ($name in $assignableRoles) {
    $guid = (Invoke-Az role definition list --name $name --query '[0].name' --output tsv).Trim()
    if (-not $guid) { throw "Built-in role '$name' not found" }
    Write-Host ("  {0,-45} {1}" -f $name, $guid)
    $guid
}
$guidList = $roleGuids -join ', '

# ABAC condition: writes limited to the role list and to service-principal or group assignees; deletes
# limited to the role list. Syntax: https://learn.microsoft.com/azure/role-based-access-control/delegate-role-assignments-examples
$condition = '((!(ActionMatches{''Microsoft.Authorization/roleAssignments/write''})) OR (' +
    '@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {' + $guidList + '} AND ' +
    '@Request[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {''ServicePrincipal'', ''Group''})) AND ' +
    '((!(ActionMatches{''Microsoft.Authorization/roleAssignments/delete''})) OR (' +
    '@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {' + $guidList + '}))'

$existing = Invoke-Az role assignment list --assignee $spObjectId --scope $scope --role 'Role Based Access Control Administrator' --output json | ConvertFrom-Json
if ($existing.Count -gt 0) {
    Write-Host "Already granted (condition not re-checked): Role Based Access Control Administrator"
} else {
    Invoke-Az role assignment create `
        --assignee-object-id $spObjectId `
        --assignee-principal-type ServicePrincipal `
        --role 'Role Based Access Control Administrator' `
        --scope $scope `
        --condition $condition `
        --condition-version '2.0' `
        --description 'Work Orders platform provisioner: may assign only the roles used by terraform/foundation and terraform/environment' `
        --output none
    Write-Host "Granted: Role Based Access Control Administrator (constrained) on $scope"
}

if ($SkipEntra) {
    Write-Host "== Part 2 skipped (-SkipEntra): an Entra admin creates the groups and app registrations."
} else {
    Write-Host "== Part 2: Microsoft Graph application permissions (needs Privileged Role Administrator)"
    $graphAppId = '00000003-0000-0000-c000-000000000000'
    $graphSp = Invoke-Az ad sp show --id $graphAppId --output json | ConvertFrom-Json
    $current = Invoke-Az rest --method GET `
        --url "https://graph.microsoft.com/v1.0/servicePrincipals/$spObjectId/appRoleAssignments" --output json | ConvertFrom-Json
    foreach ($permission in @('Group.Create', 'Application.ReadWrite.OwnedBy')) {
        $appRoleId = ($graphSp.appRoles | Where-Object { $_.value -eq $permission -and $_.allowedMemberTypes -contains 'Application' }).id
        if (-not $appRoleId) { throw "Graph app role '$permission' not found" }
        if ($current.value | Where-Object { $_.appRoleId -eq $appRoleId }) {
            Write-Host "Already granted: $permission"
            continue
        }
        $body = @{ principalId = $spObjectId; resourceId = $graphSp.id; appRoleId = $appRoleId } | ConvertTo-Json -Compress
        $bodyFile = New-TemporaryFile
        Set-Content -Path $bodyFile -Value $body -Encoding ascii
        try {
            Invoke-Az rest --method POST `
                --url "https://graph.microsoft.com/v1.0/servicePrincipals/$spObjectId/appRoleAssignments" `
                --headers 'Content-Type=application/json' --body "@$bodyFile" --output none
        } finally {
            Remove-Item $bodyFile -Force
        }
        Write-Host "Granted with admin consent: $permission"
    }
}

Write-Host "== Verify"
Invoke-Az role assignment list --assignee $spObjectId --scope $scope --output table
Write-Host "Done. The provisioner can now apply terraform/foundation and terraform/environment (docs/bootstrap.md)."
