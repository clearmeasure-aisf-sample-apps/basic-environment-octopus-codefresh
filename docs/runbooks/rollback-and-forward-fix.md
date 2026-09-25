# Rollback and forward fix

How to recover from a bad release of an app in `tdd`, `uat` or `prod`: redeploy the previous release (rollback), or
ship a corrected release (forward fix). Octopus owns both; Argo CD only applies the result. The steps hold for every
app; the examples use `workorders` (project `workorders`, deployable `app`, Kustomize pins).

Contracts: ADR-D4 and ADR-D5 (promotion writer, sync policy and rollback), ADR-IR34 (decision 1: PreSync migrations;
decision 20: tag pins; decision 23: separation of duties; §7.0 pins, channels and step templates), ADR-D13 (human
gates), ADR-IR33 (sleep and wake). CAP-OCT-007 proves the rollback path every night on the sandbox.

![Dynamic: pin and sync through the Argo CD gateway](../../design/diagrams/dyn-pin-and-sync.png)

*Dynamic, pin and sync. The step Update Argo CD image tags finds the Applications annotated with the deployment's project and environment through the Octopus Argo CD gateway (outbound gRPC from the cluster), then commits `images[].newTag` to `gitops/apps/<app>/envs/<env>/<deployable>/kustomization.yaml` on main as Octopus, without triggering a sync. Argo CD's next poll (scoped by `manifest-generate-paths`) syncs, runs the PreSync `db-migrate` and rolls out; the gateway reports Synced and Healthy at the pin commit, which ends the step's 900-second wait; Verify version and Smoke test follow. A failed migration fails the sync while the old pods keep serving; a rollback redeploys the previous release; `platform-pin-writer` is the fallback writer.*

## Roles

| Role | Who | Decides or does |
|---|---|---|
| Incident lead | Team `SRE On-call` | Detects, gathers evidence, runs the Octopus redeploy |
| Release Manager | Team `Release Managers` | Chooses rollback or forward fix; creates Hotfix-channel releases |
| Prod approver | Team `Prod Approvers` | Answers the prod go/no-go. Under `Platform.SoDMode` = `single-operator` the creator may approve with a recorded reason; under `enforce`, `platform-sod-guard` fails when the creator approves |
| App team | Maintainers of the app's repository (for `workorders`: `clearmeasure-aisf-sample-apps/20260923-001`) | Writes the fix; judges schema compatibility |

## Rules

- Roll back only by redeploying an earlier release in Octopus. The redeploy writes the earlier image tags into the
  app's pin files (`gitops/apps/<app>/envs/<env>/<deployable>/`: `images[].newTag` for Kustomize, the values at
  `argo.octopus.com/image-replace-paths` for Helm, `image:` for raw manifests), and Argo CD syncs them.
- Never run `argocd app rollback`: Argo CD cannot roll back an application with automated sync (E40), and a manual
  sync away from Git is reverted by self-heal.
- Never edit a pin by hand; only Octopus writes pins (bot-path audit). The one exception is an Octopus outage, handled
  as a break-glass change by pull request (`break-glass.md`).
- Migrations are forward-only and run as the PreSync Job `db-migrate` before the new pods start. An older release runs
  against the newer schema. A rollback is safe only when the bad release's migrations were additive (expand). When
  they removed or renamed something the older code uses (contract), fix forward or restore
  (`database-backup-and-restore.md`).
- Every image comes from `apps/<app>/…`, signed by the app's own release pipeline; prod admits nothing else
  (CAP-AZ-001, CAP-AZ-002). A rollback therefore uses an earlier signed, tag-locked release, never a rebuilt image.

## Preconditions

- The incident record states the environment, the bad release number and the first bad time.
- The previous good release is known: Octopus, the app's project, the environment's deployment history.
- The bad release's migrations are known: build information, or the migration scripts in the release commit.
- A sleeping tier needs no manual wake for a redeploy: step 0 deploys `platform-wake`, which wakes the cluster and
  waits. Reading Argo CD or Kyverno reports before the redeploy needs the tier awake (`sleep-and-wake.md`).

## Decide

| Question | Yes | No |
|---|---|---|
| Did the bad release add only additive migrations (or none)? | Rollback is possible | Forward fix or restore |
| Is the fault in configuration (the app's `envs/<env>/…` files other than the pins) rather than the image? | Revert that pull request; no Octopus action. A sleeping tier applies the revert at its next wake, so force-wake it | Continue |
| Is data damaged? | `database-backup-and-restore.md` first, then continue here | Continue |
| Can a fix be merged and released within the error budget (`slo-fast-burn.md`)? | Forward fix | Rollback now, fix forward later |

## Steps: rollback

1. In Octopus, open the app's project, environment `<env>`, and select the last release that was healthy there.
2. Choose Redeploy. The app's process runs again. For `workorders`:
   - step 0: `platform-wake` (Deploy a Release);
   - in prod: the go/no-go intervention, `platform-sod-guard`, and `platform-db-backup` before the pin;
   - the image-tag step commits the older tags; Argo CD syncs; the PreSync Job `db-migrate` finds every script of the
     older release already applied and changes nothing;
   - the app's own checks (`verify-version`, `smoke-test`).
3. If `prod-weekend-freeze` is active, a Release Manager overrides it with the reason `<incident-id>`.
4. Watch the app's Applications (`<app>-<deployable>-<env>`) reach Synced and Healthy at the new pin commit. Octopus
   verifies the same.
5. Keep the bad release out of the environment: tell approvers not to promote it, or add a deployment freeze for the
   app's projects, until the fix exists.

## Steps: forward fix

1. The app team merges the fix through the app repository's normal pull request and its required CI status. The app's
   Codefresh release pipeline builds, signs and creates the next release on channel `Default`, which reaches `tdd`
   automatically.
2. Normal path: promote through `uat` (sign-off) to `prod`.
3. Urgent path, skipping `tdd`: a Release Manager creates a release on channel `Hotfix` (lifecycle `platform-hotfix`:
   `uat`, then `prod`) with the same package versions; the app's hotfix justification step records the reason. The
   images still come only from the app's release pipeline, so prod admits them.
4. Deploy to `prod` with the go/no-go answered by a prod approver.

## Verification

- Octopus: the deployment finished and the app's version check saw the expected version (for `workorders`,
  `/_version`).
- Argo CD: the app's Applications are Synced and Healthy at the commit Octopus wrote.
- The app's smoke check passed.
- The fast-burn alert `slo-fast-burn-<app>-<env>` has resolved (`slo-fast-burn.md`).
- Kyverno reports no new failures: `kubectl get policyreports -n <app>-<env>`.

## Audit evidence

- The Octopus deployment of the rollback or fix, with the intervention answers, the `platform-sod-guard` result and
  the freeze override reason.
- The pin commit in the environment repository: `git log -p -- gitops/apps/<app>/envs/<env>/`.
- The Argo CD sync history of the app's Applications.
- For a forward fix: the app pull request, the Codefresh build (Octopus build information) and the release notes.
- The incident record with the decision (rollback or forward fix) and the schema assessment.
