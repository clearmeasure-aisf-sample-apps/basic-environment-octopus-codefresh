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
5. **Codefresh is idle.** No builds running or queued (Builds view). The account runs three builds at a time on the hybrid runner (PRO_1);
   pause the scheduled conformance crons for the demo window so they cannot take a slot. A rehearsal right before the
   demo runs as runbook `e2e-pass` (below), which takes no Codefresh slot; let it finish before the demo starts.
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
| 5 | Release `2.5.<height>` in project `workorders`, channel `Default`; its release notes end with `### CI summary` (commit, PR, builds, per-suite test counts, image digests) | Octopus, Projects → workorders → Releases → `<version>` | Codefresh builds; Octopus decides where it goes. The counts are the tests this release build ran; gates it reused from `codefresh/ci` are named, not counted. |
| 6 | tdd deploys by itself: wake, pin commit, Argo CD sync with PreSync `db-migrate`, verify, smoke, acceptance tests | Octopus task log; the pin commit in this repo (`Pin workorders <version> in tdd`); Argo CD | Octopus deploys by committing a tag; Argo CD applies it. |
| 7 | The change on `workorders-tdd` | Browser | |
| 8 | uat deploys by itself once tdd succeeds | Octopus → workorders → Overview | Lifecycle `platform-continuous`: every phase automatic, a failed phase stops the release. |
| 9 | prod deploys by itself once uat succeeds: pre-release database backup, then the pin | Octopus task log | No approvals: `Platform.ApprovalsRequired = false` skips sign-off, go/no-go and their guards. |
| 10 | The change on `workorders-prod` | Browser | Same image digest as tdd: nothing is rebuilt. |

## If a step stalls

| Symptom | Cause | Action |
|---|---|---|
| A Codefresh build stays `delayed` | Three builds already running (`platform-env/env-checks` starts on every push to this repo, Octopus pin commits included) | Terminate the `env-checks` build; re-run it after the demo |
| A deployment waits at `wake-environment` | Cluster starting (about 5 minutes) | Wait; `env-wake` reports Running |
| "Argo CD Application is healthy" never arrives | Gateway Unavailable | Run `env-wake` in that environment, then retry the step |
| The browser shows an older version | Rollout in progress | `verify-version` in the task log names the version each replica reports |
| TLS error in nonprod | Certificate not yet issued after a rebuild (ZeroSSL issuer) | [credential-rotation.md](credential-rotation.md) §5 |

## After the demo

Release the holds (`Sleep.HoldMinutes=0` with the same `Sleep.HoldBy`), re-enable the conformance crons, and re-run
any `env-checks` build that was terminated.

## Rehearse unattended (CAP-KIT-009)

`EndToEndTests` runs this script without a presenter: a harmless commit (`src/UI/Server/e2e-marker.txt`) through ci,
release, tdd, uat and prod. Run it where it holds no Codefresh build slot, so that its own ci and release builds get
one ([conformance.md](conformance.md#end-to-end-pass-without-a-codefresh-slot)):

- **Runbook `e2e-pass`** of `platform-infrastructure`: Operations, Runbooks, `e2e-pass`, Run, branch `main`,
  environment `infra-nonprod`, `App.Name` empty. Or from a shell with the Space Manager key:

  ```bash
  export OCTOPUS_URL=https://clearmeasure.octopus.app OCTOPUS_SPACE_ID=Spaces-335 OCTOPUS_API_KEY=...
  pwsh -NoProfile -File codefresh/platform/scripts/octopus-runbook.ps1 -Project platform-infrastructure \
    -Runbook e2e-pass -Environment infra-nonprod -Notes "demo rehearsal" -WaitMinutes 300
  ```

  The task log shows `progress: stage n/5` (ci, release, tdd, uat, prod); `summary.md`, the TRX file and the task log
  of each deployment are attached to the run. The run holds one of the 5 Octopus task slots; start no other deployment
  meanwhile.
- **An operator's machine** with pwsh, the .NET 10 SDK and a clone of this repository:
  `OCTOPUS_API_KEY=... GITHUB_TOKEN=... pwsh -NoProfile -File codefresh/platform/scripts/conformance-e2e.ps1`.
- **`platform-env/conformance`** with `TEST_FILTER=FullyQualifiedName~EndToEndTests` and `CONFORMANCE_SLEEP_AFTER=false`
  still works, and holds a Codefresh slot for the whole pass.

Sleep holds are not needed for a rehearsal: the pass wakes each tier through its deployments, and a running `e2e-pass`
keeps nonprod up. Set the holds of "Before the demo" only for the demo itself.

## Rehearsal runs (CAP-KIT-009, 2026-09-27)

Each run: `platform-env/conformance` with `TEST_FILTER=FullyQualifiedName~EndToEndTests`, `CONFORMANCE_SLEEP_AFTER=false`.

| Run | Build | Version | Result | Total | ci / release / tdd / uat / prod (min) | Notes |
|---|---|---|---|---|---|---|
| 1 | `6ab96e45ed4f16121a71beb6` | 2.5.724 | Passed | 1:26 | 27 / 29 / 15 / 8 / 7 | Lifecycle platform-standard; the test promoted uat and prod |
| 2 | `6ab98369a1ea1b5606f15d32` | 2.5.725 | Passed | 0:59 | 20 / 24 / 11 / 2 / 2 | platform-continuous, no approvals, warm build node, Argo CD 30s, wake skipped |

Streak after the cycle-time changes of 2026-09-28 (release skips the static gates CI passed on the same tree, BuildKit
cache, parallel Octopus steps, 5 NUnit workers in tdd, warm build node, Argo CD 30 s):

| Run | Build | Version | Result | Total | ci / release / tdd / uat / prod (min) | Notes |
|---|---|---|---|---|---|---|
| S1 | `6ab9eba3e9dd6ae4ee87f91c` | 2.5.733 | Passed | 0:53 | 23 / 12 / 11 / 2 / 2.5 | Release `CI_TREE_VERIFIED=true`; first BuildKit build seeds the cache |
| S2 | `6ab9f83f1b4ef2df5dea7a8b` | | Passed | 0:53 | | Release 7.9 min, BuildKit cache hits |
| S3 | `6aba03db5cda4c247a26924d` | | Passed | 0:45 | | |
| S4 | `6aba19aa00797f5a9e68ebb9` | | Passed | 0:37 | | Re-run: `6aba0e6130b1c671d6f21c6a` reached prod (2.5.737), then Codefresh engine-inactivity ended the driver |
| S5 | `6aba251f0940fe44e5ad10c8` | | Passed | 0:54 | | |
| S6 | `6aba31e559adb8ee2a3d685b` | | Passed | 0:49 | | |
| S7 | `6aba4561dd6552b2fca4eb7c` | | Passed | 0:47 | | Re-run: `6aba3d48dd6552b2fc9cc808` ended by engine-inactivity |
| S8 | `6aba504de5f938e8ec2e6ef5` | | Passed | 0:53 | | |
| S9 | `6aba5c9de19aff0f10ec8813` | | Passed | 0:55 | | |
| S10 | `6aba698ec4b244ce4e65d4aa` | | Passed | 0:51 | | Ten consecutive green passes to prod |
