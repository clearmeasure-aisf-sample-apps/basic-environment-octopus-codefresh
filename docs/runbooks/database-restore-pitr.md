# Database restore to a point in time

Restore `<sqldb-workorders-{env}>` to a moment before data was damaged, using Azure SQL
point-in-time restore (PITR) through the Octopus runbook `db-restore-pitr`. The restore creates a
new database; nothing is overwritten until a person approves the swap.

Contracts: §7.2 (runbooks `db-backup`, `db-restore-pitr`), §5.2 (`id-octopus-deploy-<env>`),
ADR-C2 (forward-only migrations), ADR-D9 (SQL authentication), ADR-IR13 (SQL DB Contributor in
`uat` and `prod`), ADR-IR28 (swap with the app stopped), ADR-IR33 (sleep and wake), R18
(retention).

## Roles

| Role | Who | Does |
|---|---|---|
| Operator | Team `SRE On-call` | Runs the runbook, validates the restored data |
| Data owner | App team lead | Chooses the restore point, accepts the data loss after it |
| Approver | `Release Managers` or `Platform Engineers` (the responsible teams of the swap intervention in `db-restore-pitr.ocl`) | Answers the swap manual intervention; never the operator |
| Azure Owner | PIM Owner or User Access Administrator | Lifts the prod lock only if the swap or clean-up is blocked (`break-glass.md`, path D) |

## Retention and limits

| Environment | Restore window | Set in |
|---|---|---|
| `tdd`, `uat` | 7 days (Basic or S0 tier) | `terraform/environment/nonprod.tfvars` (`pitr_days`) |
| `prod` | 35 days, plus long-term backups (weekly, monthly, yearly) | `terraform/environment/prod.tfvars` |

- Restore granularity is one second within the window; the newest restorable point is a few
  minutes old.
- The restored database contains the contained users and passwords of the restore point
  (`workorders_migrator`, `workorders_acceptance`, the workload-identity user of
  `id-workorders-<env>-app`).
- `tdd` is reset by every acceptance run and is not restored.

## Preconditions

- An incident record with the damaging event, its first known time in UTC and the data owner's
  choice of restore point (just before that time).
- The app is stopped, so no session stays on the live database and the restored data cannot
  diverge (ADR-IR28):
  - One pull request sets `replicas: 0` for `ui-server` and `worker` in
    `gitops/workorders/envs/<env>/config` (the Worker is already 0 unless it was enabled), and
    Argo CD shows `workorders-<env>` Synced with no running pods.
  - Announce the maintenance window. Without this, pooled connections would keep writing to the
    renamed database after the swap, and `/_diagnostics/*` returns 404 outside `tdd` after WI-02,
    so no pool reset exists.
- Wake the cluster first. The runbook's first step, `wake-environment`, only waits (up to 30
  minutes) for a sleeping cluster, because project `workorders` holds no key (ADR-IR33): run
  `env-wake` in `infra-nonprod` or `infra-prod` before the runbook (`sleep-and-wake.md`,
  force-wake). Merge the replica pull request before running the runbook: after a wake, pods
  restored from etcd start with the old replica count until Argo CD applies the pull request, so
  the approver's zero-pod check in step 4 stays the gate. Checking Argo CD before the run needs
  the cluster awake too.
- Deployments to the environment are paused: an Octopus deployment freeze for `workorders` and
  `<env>` for the planned window.
- The operator can sign in to SQL with Entra ID as a member of `<sql-admins-{class}>` to run
  validation queries (PIM or group membership approved for the window).

## Steps

1. In Octopus, project `workorders`, run runbook `db-restore-pitr` in `<env>`. At the prompt,
   enter `RestorePointInTime` in UTC, ISO 8601 (for example `2026-09-24T08:15:00Z`).
2. The runbook restores to a new database on the same server (`<sqldb-workorders-{env}>` with a
   restore suffix) using `#{Azure.DeployAccount}`, then pauses at a manual intervention.
3. Validate the restored database before approving:
   - Row counts and the latest business records around the restore point, agreed with the data
     owner.
   - Schema version: `SELECT TOP 5 ScriptName, Applied FROM dbo.SchemaVersions ORDER BY Applied DESC`.
     If the restored journal is older than the running release, the release applied migrations
     after the restore point: plan the redeploy in step 6.
4. The approver (not the operator) confirms zero running pods and answers the manual
   intervention. The runbook then swaps the names (the current database gets a suffix and stays
   for forensics; the restored database takes `<sqldb-workorders-{env}>`).
   If the prod lock blocks the rename [VERIFY whether a rename counts as a delete under
   `CanNotDelete`], stop and follow `break-glass.md` path D.
5. Passwords: if `rotate-sql-passwords` ran after the restore point, the restored contained users
   still have the old passwords. Run `rotate-sql-passwords` in `infra-nonprod` or `infra-prod`
   now, so Key Vault and the database agree again.
6. Schema: redeploy the release whose migrations match the restored journal, or redeploy the
   current release so `migrate-database` re-applies the newer scripts (forward-only, ADR-C2).
   Choose with the app team (`rollback-and-forward-fix.md`).
7. Start the app again by reverting the replica pull request; new pods open fresh connections to
   the restored database. If the cluster went back to sleep meanwhile, force-wake it first, or
   Argo CD applies the revert only at the next wake (`sleep-and-wake.md`). Lift the deployment
   freeze.
8. Keep the old database for the agreed forensic period. Deleting it in prod needs the lock
   lifted (`break-glass.md`, path D); the `uat` copy can be deleted by `id-octopus-deploy-uat`,
   which holds SQL DB Contributor (ADR-IR13).

## Verification

- `#{App.BaseUrl}/_healthcheck` reports Healthy. The redeploy in step 6 also runs
  `verify-version` and `smoke-test`.
- The data owner confirms the business checks in the incident record.
- `SELECT DB_NAME(), DATABASEPROPERTYEX(DB_NAME(), 'Updateability')` on the live database returns
  the original name and `READ_WRITE`.
- The fast-burn SLO alert (`slo-fast-burn.md`) is quiet for 30 minutes after writes resume.

## Audit evidence

- The Octopus runbook run: prompted `RestorePointInTime`, the manual-intervention answer and who
  gave it.
- The Azure activity log entries of the restore and rename on `<sql-workorders-{env}>`.
- SQL audit events for the window from `log-workorders` (legacy `AzureDiagnostics` table):

  ```kusto
  AzureDiagnostics
  | where Category == "SQLSecurityAuditEvents"
  | where TimeGenerated between (datetime(<start UTC>) .. datetime(<end UTC>))
  | project TimeGenerated, server_principal_name_s, database_name_s, action_name_s, statement_s
  | order by TimeGenerated asc
  ```

- The incident record with the restore point, the achieved data loss (RPO) and the time from
  decision to writes re-enabled (RTO). The phase-4 exit criterion needs a drill under the agreed
  RTO (§9).
