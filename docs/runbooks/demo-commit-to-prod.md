# Demo: one change of app #1 from commit to prod

A presenter's script for showing the whole delivery path of `workorders` live: a code change, the build, the packages,
and the deployments to tdd, uat and prod. The path is the one Lab 18 traces
([../walkthroughs/01-follow-a-commit.md](../walkthroughs/01-follow-a-commit.md)) and Lab 20 gates
([../walkthroughs/03-promotion-and-hotfix.md](../walkthroughs/03-promotion-and-hotfix.md)); CAP-KIT-009
(`EndToEndTests`) runs the same path unattended.

Allow about 60 minutes: ci ~10, release ~15, tdd ~15 (acceptance tests), uat ~8, prod ~10.

## Before the demo (T-60 minutes)

1. **Sleep holds.** Both tiers stay up only while held. Runbook `sleep-hold` of `platform-infrastructure` in
   `infra-nonprod` and `infra-prod`: `Sleep.HoldMinutes=720`, `Sleep.HoldBy=demo:<date>`. The hold is the presenter's:
   since `sleep-hold` only lets the holder release a hold, a conformance teardown leaves it in place.
2. **Wake.** Runbook `env-wake` in both environments if either cluster is Stopped (Azure portal or the runbook's log).
3. **Hosts answer.** `https://workorders-<env>.<Platform.AppsDomain>/_healthcheck` returns 200 for tdd, uat and prod
   (`Platform.AppsDomain` in library set `Platform Environment`; nonprod and prod have their own address).
4. **Argo CD gateways Healthy** in Octopus, Infrastructure → Argo CD Instances (`argocd-nonprod`, `argocd-prod`). A
   gateway shown Unavailable: run `env-wake`, which requests its health check.
5. **Codefresh is idle.** No builds running or queued (Builds view). The account runs two builds at a time (PRO_1);
   pause the scheduled conformance crons for the demo window so they cannot take a slot.
6. **No approvals.** workorders sets `Platform.ApprovalsRequired = false` (`.octopus/apps/workorders/workorders/variables.ocl`)
   and uses lifecycle `platform-continuous`, so nothing waits for a person. To show the approval gates instead, set the
   variable to `true` and the descriptor's lifecycle back to `platform-standard`.
7. **Freeze.** `prod-weekend-freeze-workorders` blocks prod from Saturday 00:00 to Monday 00:00 UTC, weekly from
   2026-10-03. A weekday demo is outside it.

## The script

| # | Show | Where | What to say |
|---|---|---|---|
| 1 | Make a visible change on a branch, open a pull request to `master` | GitHub, `clearmeasure-aisf-sample-apps/20260923-001` | App repos never deploy; they only build. |
| 2 | `workorders/ci` runs; the `codefresh/ci` check turns green | Codefresh build log; the pull request's checks | The required check of `master`; GitHub Actions stays off. |
| 3 | Merge | GitHub | Only the default branch makes releases. |
| 4 | `workorders/release`: version `2.5.<height>`, images pushed, signed, SBOM, release created | Codefresh build log | One version ties image tag, package, release and the version the app reports. |
| 5 | Release `2.5.<height>` in project `workorders`, channel `Default` | Octopus, Projects → workorders → Releases | Codefresh builds; Octopus decides where it goes. |
| 6 | tdd deploys by itself: wake, pin commit, Argo CD sync with PreSync `db-migrate`, verify, smoke, acceptance tests | Octopus task log; the pin commit in this repo (`Pin workorders <version> in tdd`); Argo CD | Octopus deploys by committing a tag; Argo CD applies it. |
| 7 | The change on `workorders-tdd` | Browser | |
| 8 | uat deploys by itself once tdd succeeds | Octopus → workorders → Overview | Lifecycle `platform-continuous`: every phase automatic, a failed phase stops the release. |
| 9 | prod deploys by itself once uat succeeds: pre-release database backup, then the pin | Octopus task log | No approvals: `Platform.ApprovalsRequired = false` skips sign-off, go/no-go and their guards. |
| 10 | The change on `workorders-prod` | Browser | Same image digest as tdd: nothing is rebuilt. |

## If a step stalls

| Symptom | Cause | Action |
|---|---|---|
| A Codefresh build stays `delayed` | Two builds already running (`platform-env/env-checks` starts on every push to this repo, Octopus pin commits included) | Terminate the `env-checks` build; re-run it after the demo |
| A deployment waits at `wake-environment` | Cluster starting (about 5 minutes) | Wait; `env-wake` reports Running |
| "Argo CD Application is healthy" never arrives | Gateway Unavailable | Run `env-wake` in that environment, then retry the step |
| The browser shows an older version | Rollout in progress | `verify-version` in the task log names the version each replica reports |
| TLS error in nonprod | Certificate not yet issued after a rebuild (ZeroSSL issuer) | [credential-rotation.md](credential-rotation.md) §5 |

## After the demo

Release the holds (`Sleep.HoldMinutes=0` with the same `Sleep.HoldBy`), re-enable the conformance crons, and re-run
any `env-checks` build that was terminated.
