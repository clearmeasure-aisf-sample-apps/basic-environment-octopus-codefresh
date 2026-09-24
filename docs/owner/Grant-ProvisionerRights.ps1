<#
.SYNOPSIS
    Owner-run grant that lets the provisioner service principal (sp-automation-mvp-sub) apply
    terraform/foundation, terraform/build and terraform/apps/grants itself (ADR-IR34).

.DESCRIPTION
    Part 1 (needs subscription Owner or User Access Administrator):
      - Registers the resource providers the platform uses.
      - Grants the service principal "Role Based Access Control Administrator" on the subscription with an
        ABAC condition: it may create or delete role assignments ONLY for the twelve built-in roles that
        terraform/foundation and terraform/apps/grants assign, and only to service principals (managed
        identities included) and groups. It can never grant Owner, User Access Administrator or RBAC
        Administrator, so it cannot escalate itself.
      - If that assignment already exists with a different condition (for example the earlier list of ten
        roles), its condition is replaced in place.
    Part 2 (needs an Entra role: Privileged Role Administrator, or Global Administrator):
      - Grants the service principal Microsoft Graph application permissions Group.Create and
        Application.ReadWrite.OwnedBy, with admin consent. It can then create group platform-operators and
        the app registration sp-platform-conformance in terraform/foundation, and manage only objects it owns.
      Skip Part 2 with -SkipEntra when those permissions exist (granted since R6).
    Part 3 (-ApplyLocks; needs subscription Owner or User Access Administrator):
      - CanNotDelete locks on rg-platform-global (Terraform state), rg-platform-build (registry and build
        cluster) and rg-platform-prod-data (prod database disks). Neither Contributor nor the constrained
        RBAC Administrator may write locks, so terraform/foundation cannot create them.
      - A group that does not exist yet is skipped with a warning, so the switch can run any time after
        terraform/foundation (P1-02). Run it again to lock a group created later.
      - A lock also refuses deletes inside the group: role assignments (extension resources), AKS node
        pools and federated credentials. Lift it for such a change (see NOTES), for example the switch
        of terraform/foundation to conformance_least_privilege = true, which deletes an interim
        assignment in rg-platform-build, or a VM size change of the builds pool.

    Idempotent: re-running skips grants and locks that exist and changes only a differing condition.

.EXAMPLE
    & 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' login   # as the subscription Owner
    & 'C:\Temp\Grant-ProvisionerRights.ps1' -SubscriptionId <subscription-id> -ProvisionerAppId <app-id> -SkipEntra
    & 'C:\Temp\Grant-ProvisionerRights.ps1' -SubscriptionId <subscription-id> -ProvisionerAppId <app-id> -SkipEntra -ApplyLocks

.NOTES
    Requires Azure CLI (az) 2.60 or later, and PowerShell 7 or Windows PowerShell 5.1.
    Revoke later with:
      az role assignment delete --assignee <app-id> --role "Role Based Access Control Administrator" --scope /subscriptions/<subscription-id>
    Lift a lock for a planned change with:
      az lock delete --name platform-cannot-delete --resource-group <resource-group>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SubscriptionId,
    # Application (client) ID of the provisioner service principal.
    [Parameter(Mandatory)] [string] $ProvisionerAppId,
    [switch] $SkipEntra,
    # Also apply the CanNotDelete locks of Part 3.
    [switch] $ApplyLocks,
    # Absolute path to the Azure CLI. Default: the standard Windows install location.
    [string] $AzPath = 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $AzPath)) { throw "Azure CLI not found at $AzPath. Pass -AzPath with its absolute path." }

function Invoke-Az {
    # Runs az, fails on a non-zero exit code, and returns stdout.
    $output = & $AzPath @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed with exit code $LASTEXITCODE" }
    return $output
}

function ConvertFrom-AzJsonArray {
    # Parses az JSON list output into an array. Windows PowerShell 5.1 returns a JSON array from
    # ConvertFrom-Json as one object and PowerShell 7 enumerates it; this gives both the same shape.
    param([object[]] $Lines)
    $text = ($Lines -join "`n").Trim()
    if (-not $text) { return , @() }
    $parsed = $text | ConvertFrom-Json
    return , @($parsed | ForEach-Object { $_ })
}

function Get-NormalizedCondition {
    # Conditions compare without whitespace and case, so a reformatted copy does not count as a change.
    param([string] $Text)
    if (-not $Text) { return '' }
    return ($Text -replace '\s+', '').ToLowerInvariant()
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
    'Microsoft.ContainerService', 'Microsoft.ContainerRegistry', 'Microsoft.KeyVault', 'Microsoft.AlertsManagement',
    'Microsoft.OperationalInsights', 'Microsoft.Insights', 'Microsoft.Monitor', 'Microsoft.ManagedIdentity',
    'Microsoft.Storage', 'Microsoft.Network', 'Microsoft.Authorization', 'Microsoft.Dashboard'
)
foreach ($p in $providers) {
    Invoke-Az provider register --namespace $p --subscription $SubscriptionId | Out-Null
    Write-Host "registered (or registering): $p"
}

Write-Host "== Part 1b: constrained Role Based Access Control Administrator"
# The only roles terraform/foundation and terraform/apps/grants assign. The three AKS roles serve the
# least-privilege grants of sp-platform-conformance (conformance_least_privilege = true, P1-03).
$assignableRoles = @(
    'AcrPull', 'Reader', 'Contributor', 'Key Vault Secrets Officer', 'Key Vault Secrets User',
    'Storage Blob Data Contributor', 'Azure Kubernetes Service RBAC Cluster Admin',
    'Network Contributor', 'Managed Identity Operator',
    'Azure Kubernetes Service Cluster User Role', 'Azure Kubernetes Service RBAC Reader',
    'Azure Kubernetes Service RBAC Writer'
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
$description = 'Platform provisioner: may assign only the roles used by terraform/foundation and terraform/apps/grants'

$existing = ConvertFrom-AzJsonArray (Invoke-Az role assignment list --assignee $spObjectId --scope $scope --role 'Role Based Access Control Administrator' --output json)
if ($existing.Count -gt 1) {
    Write-Warning "Found $($existing.Count) Role Based Access Control Administrator assignments for the provisioner at $scope; checking the first. Remove the others by hand."
}
if ($existing.Count -gt 0) {
    $assignment = $existing[0]
    if ((Get-NormalizedCondition $assignment.condition) -eq (Get-NormalizedCondition $condition)) {
        Write-Host "Already granted with the same condition: Role Based Access Control Administrator"
    } else {
        Write-Host "Existing assignment has a different condition; replacing it in place ($($assignment.name))"
        $assignment | Add-Member -NotePropertyName condition -NotePropertyValue $condition -Force
        $assignment | Add-Member -NotePropertyName conditionVersion -NotePropertyValue '2.0' -Force
        $assignment | Add-Member -NotePropertyName description -NotePropertyValue $description -Force
        # The JSON goes through a file: Windows PowerShell 5.1 strips the double quotes of arguments that
        # it passes to az.cmd. UTF-8 without a byte order mark suits both PowerShell versions.
        $assignmentFile = New-TemporaryFile
        try {
            [System.IO.File]::WriteAllText($assignmentFile.FullName, ($assignment | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding $false))
            Invoke-Az role assignment update --role-assignment $assignmentFile.FullName --output none
        } finally {
            Remove-Item $assignmentFile -Force
        }
        Write-Host "Replaced: the condition now admits the $($assignableRoles.Count) roles above"
    }
} else {
    Invoke-Az role assignment create `
        --assignee-object-id $spObjectId `
        --assignee-principal-type ServicePrincipal `
        --role 'Role Based Access Control Administrator' `
        --scope $scope `
        --condition $condition `
        --condition-version '2.0' `
        --description $description `
        --output none
    Write-Host "Granted: Role Based Access Control Administrator (constrained) on $scope"
}

if ($SkipEntra) {
    Write-Host "== Part 2 skipped (-SkipEntra): the Graph permissions are already granted, or an Entra admin creates the group and the app registration."
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

$lockName = 'platform-cannot-delete'
$lockedGroups = @('rg-platform-global', 'rg-platform-build', 'rg-platform-prod-data')
if ($ApplyLocks) {
    Write-Host "== Part 3: CanNotDelete locks (-ApplyLocks)"
    $lockNotes = 'Platform (ADR-IR34): protects Terraform state, the registry and build cluster, and prod data. Lift only for a planned change.'
    foreach ($group in $lockedGroups) {
        $exists = ("$(Invoke-Az group exists --name $group --subscription $SubscriptionId)").Trim()
        if ($exists -ne 'true') {
            Write-Warning "Resource group $group does not exist yet; lock skipped. Run -ApplyLocks again after terraform/foundation creates it."
            continue
        }
        $locks = ConvertFrom-AzJsonArray (Invoke-Az lock list --resource-group $group --subscription $SubscriptionId --output json)
        if ($locks | Where-Object { $_.name -eq $lockName -and $_.level -eq 'CanNotDelete' }) {
            Write-Host "Already locked: $group ($lockName)"
            continue
        }
        Invoke-Az lock create --name $lockName --lock-type CanNotDelete --resource-group $group `
            --subscription $SubscriptionId --notes $lockNotes --output none
        Write-Host "Locked: $group (CanNotDelete, $lockName)"
    }
} else {
    Write-Host "== Part 3 skipped: pass -ApplyLocks to lock $($lockedGroups -join ', ')."
}

Write-Host "== Verify"
Invoke-Az role assignment list --assignee $spObjectId --scope $scope --output table
if ($ApplyLocks) {
    foreach ($group in $lockedGroups) {
        $exists = ("$(Invoke-Az group exists --name $group --subscription $SubscriptionId)").Trim()
        if ($exists -eq 'true') { Invoke-Az lock list --resource-group $group --subscription $SubscriptionId --output table }
    }
}
Write-Host "Done. The provisioner can now apply terraform/foundation, terraform/build and terraform/apps/grants (docs/bootstrap.md)."
