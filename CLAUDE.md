# CLAUDE.md

Guidance for Claude Code sessions in this repository. The platform's own rules live in [README.md](README.md),
[docs/tool-boundaries.md](docs/tool-boundaries.md) (TB01-TB24) and [docs/scripting.md](docs/scripting.md); this file
adds only the work-item workflow.

## Feature Loop

Work items live on the shared project board (org `clearmeasure-aisf-sample-apps`, project 678):
https://github.com/orgs/clearmeasure-aisf-sample-apps/projects/678 - one board for this repository and the app
repository `clearmeasure-aisf-sample-apps/20260923-001`.

Columns: Todo (design) -> In Progress (implement) -> In Review (pull request, local gates, merge; for `gitops/` changes
Argo CD Synced/Healthy) -> Done. The Deployed to TDD / UAT / Prod columns are for app-repository items. Closing an issue
is the move to Done, so pull requests use `Refs #N` (never `Closes #N`) and the issue is closed only after its
verification.

- `/feature-loop N` - drive ONE work item across the board end-to-end, one column at a time:
  `.claude/skills/feature-loop/SKILL.md`
- `/feature-loop-dispatch N1 N2 ...` - orchestrate a batch: children-first tree resolution, one sub-session per item,
  parent clamp, stall watchdog: `.claude/skills/feature-loop-dispatch/SKILL.md`
- Commit and PR text: no model identifier and no co-author trailer in any commit message, PR title or PR body
  (guard CAP-KIT-011; rule, matcher and limits in "Commit and pull request text" in docs/tool-boundaries.md)
- Board, per-repo gates, CI/deployment signals and board-move transport: `.claude/factory-loop.json` (`defaultRepo` is
  this repository; the app repository holds the same file with its own default)
- Card moves: `.github/workflows/project-board.yml` reacts to this repository's issue and pull-request events; every
  other move is a `board-status` `repository_dispatch` to this repository ("Board automation" in
  docs/tool-boundaries.md). Cloud sessions cannot use GraphQL or the Actions API.
- PR CI: `codefresh/env-checks` runs on every branch push except `main` and must be green on the PR head before a merge.
  Local gates before pushing: `dotnet test tests/Platform.Conformance.Offline` (the consistency failure C09 is
  pre-existing) and `pwsh -NoProfile -File scripts/checks/validate-all.ps1 <checks>`.
  Every change goes through a pull request; only Octopus pin commits go straight to `main`.
- Board helper: `.claude/skills/feature-loop/board.ps1` (`move`, `status`, `deploy`, `wait`, `tree`, `lane`) - one command
  per card move, CI check, deployment check or bounded wait; the dispatch orchestrator's lane state lives in
  `<git common dir>/feature-loop/lanes.json`
- Rarely needed detail (card-move transport, failure recovery, Octopus/Argo CD calls, clamp, watchdog findings):
  `reference.md` next to each skill's `SKILL.md`
- Stall watchdog: `.claude/skills/feature-loop-dispatch/Check-StalledLanes.ps1` (read-only; exit 1 = stalls)
