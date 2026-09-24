# SLO fast burn

Response to alert `slo-fast-burn-<app>-<env>`: an app is spending its error budget fast enough to exhaust it in about
two days. Every app-environment gets the alert from `terraform/apps/tier/monitoring.tf`; an app that sends no request
telemetry never fires it. The examples use `workorders`.

Contracts: ADR-D15 (observability and SLOs), ADR-D12 (health endpoints), ADR-IR34 (per-app App Insights and alerts,
§7.0 "Monitoring"), ADR-IR33 (sleep and wake), §9 (nonprod alerts go live in phase 3).

## The alert

| Item | Value |
|---|---|
| SLO | 99.5 % of the app's requests complete without a 5xx over 28 days (error budget 0.5 %) |
| Condition | Burn rate at least 13.44 over the last hour and over the last 5 minutes, with at least 50 requests in the hour. 13.44 = 2 % of the 28-day budget in one hour (0.02 x 672 hours) |
| Excluded paths | `/alive`, `/health`, `/healthz`, `/ready`, `/readyz`, `/livez`, `/_healthcheck`, `/_healthcheck/detailed`, `/_version`, `/version` (probes and deployment checks) |
| Source | `appi-<app>-<env>` in `rg-platform-<tier>-apps` (workspace `log-platform-<tier>`), evaluated every 5 minutes |
| Severity | 1 in `prod` (page), 3 in `tdd` and `uat` (ticket) |
| Receivers | Action group `ag-platform-oncall` of the tier (`terraform/tier`) |
| Resolution | Automatic once the condition is false (auto-mitigation) |
| While the tier sleeps | `apr-sleep-<tier>` suppresses the notification; the alert still records in the alert history. `env-wake` disables the rule after each start (`sleep-and-wake.md`) |

## Roles

| Role | Who | Does |
|---|---|---|
| Responder | Team `SRE On-call` | Acknowledges within 15 minutes (prod), triages, leads the incident |
| Release Manager | Team `Release Managers` | Decides rollback or forward fix; freezes deployments |
| App team | Owners of the app repository | Diagnoses code and dependency faults |

## Preconditions

- Read access to `appi-<app>-<env>` and `log-platform-<tier>` (Reader or Monitoring Reader on
  `rg-platform-<tier>-apps` and `rg-platform-<tier>-shared`).
- Octopus read access to the app's project; Argo CD read access.
- For cluster reads beyond Argo CD: `platform-operators` (`break-glass.md`, path C).
- The tier is awake (`sleep-and-wake.md`, check the state). A sleeping environment serves no requests, so a burn right
  after a wake points at the start-up (warm-up, secrets, the database pod). Keep the tier awake for the investigation.

## Steps

1. Acknowledge the alert and open an incident record with the alert time.
2. Confirm the burn with the alert's own query (App Insights `appi-<app>-<env>`, Logs), then locate it:

   ```kusto
   let excludedPaths = dynamic(["/alive", "/health", "/healthz", "/ready", "/readyz", "/livez", "/_healthcheck", "/_healthcheck/detailed", "/_version", "/version"]);
   requests
   | where timestamp > ago(2h)
   | extend path = tostring(parse_url(url).Path)
   | where path !in (excludedPaths)
   | summarize total = count(), failed = countif(toint(resultCode) >= 500) by bin(timestamp, 5m), name
   | extend failureRate = round(100.0 * failed / total, 2)
   | where failed > 0
   | order by timestamp desc, failed desc
   ```

   ```kusto
   exceptions
   | where timestamp > ago(2h)
   | summarize occurrences = count() by type, outerMessage, operation_Name
   | top 10 by occurrences
   ```

   ```kusto
   dependencies
   | where timestamp > ago(2h) and success == false
   | summarize failures = count() by type, target, resultCode
   | top 10 by failures
   ```

3. Correlate with change, newest first:
   - Octopus: the last deployment of the app to `<env>` and its time against the start of the burn.
   - Argo CD: the sync history of `<app>-<deployable>-<env>` and `<app>-db-<env>`; any `OutOfSync` or `Degraded`
     resource.
   - Environment repository: recent merges to `gitops/apps/<app>/envs/<env>/`.
   - ESO: `kubectl get externalsecrets -n <app>-<env>` shows `SecretSynced` (a failed sync after a password rotation
     breaks database logins).
   - The database: `kubectl get pods -n <app>-<env> -l app.kubernetes.io/component=database`; restarts or Pending
     point at the disk or memory.
   - Azure status for the region.
4. Mitigate:
   - The burn started with a deployment: follow `rollback-and-forward-fix.md` now; diagnose after.
   - A configuration merge: revert the pull request.
   - A failing dependency: mitigate it; a 5xx storm from one dependency is a finding for the app team.
   - Capacity (CPU or memory saturation, restarts): check `kubectl top pods -n <app>-<env>` and the quota
     (`kubectl describe resourcequota tenant -n <app>-<env>`); scale by pull request, never with `kubectl scale`
     (self-heal reverts it). The apps pool autoscales up to its tier maximum (7 nodes in nonprod, 4 in prod).
5. Stop further change: in prod, a Release Manager adds an Octopus deployment freeze for the app's projects until the
   burn stops, except for the fix itself.

## Verification

- The alert resolves (auto-mitigation) and stays quiet for 30 minutes.
- `apr-sleep-<tier>` is disabled while the tier runs; an enabled rule on a running cluster hides every later alert.
- The locating query shows the failure rate back under 0.5 % per 5-minute bin.
- Budget left over the 28-day window, recorded in the incident:

  ```kusto
  let excludedPaths = dynamic(["/alive", "/health", "/healthz", "/ready", "/readyz", "/livez", "/_healthcheck", "/_healthcheck/detailed", "/_version", "/version"]);
  requests
  | where timestamp > ago(28d)
  | extend path = tostring(parse_url(url).Path)
  | where path !in (excludedPaths)
  | summarize total = count(), failed = countif(toint(resultCode) >= 500)
  | extend availability = 1.0 - todouble(failed) / total
  | extend budgetLeft = 1.0 - (todouble(failed) / total) / 0.005
  ```

## Audit evidence

- The alert history of `slo-fast-burn-<app>-<env>` (Azure Monitor, Alerts) with fired and resolved times.
- The incident record: timeline, cause, mitigation, budget left, and the decision on the deployment freeze.
- Octopus and Argo CD history of any rollback or fix (`rollback-and-forward-fix.md`).
- A post-incident review within five business days when prod burned more than 10 % of the 28-day budget.
