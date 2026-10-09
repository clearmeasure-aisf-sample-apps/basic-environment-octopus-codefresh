---
name: feature-loop
description: >
  Run the feature loop on ONE GitHub work item: drive it across the shared project board
  https://github.com/orgs/clearmeasure-aisf-sample-apps/projects/678 one column at a time
  (Todo = design, In Progress = implement, In Review = verify, Deployed to TDD/UAT/Prod =
  post-merge verification for the app repo, Done = terminal) through code, tests, a
  conflict-free PR, API-verified Codefresh/Octopus/Argo CD evidence, bot-finding triage,
  merge, and board moves. Bare `#N` means an issue in clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh; `owner/repo#N`
  targets the other repo on the board. Use when asked to "run the feature loop on work
  item #N", "work issue #N through the board", or when /feature-loop N is invoked. For a
  batch of items, use /feature-loop-dispatch instead.
---

# Feature Loop (single work item)

Drives exactly one work item end-to-end. Settings: `.claude/factory-loop.json`. Rare detail
(card-move transport, failure recovery, Octopus/Argo CD calls, clamp, evidence template):
`reference.md` next to this file - read only the section a situation needs. These rules are
the contract; a user's own global rules may add to but never weaken them.

| Repo | Kind | Default branch | Column path |
|---|---|---|---|
| `clearmeasure-aisf-sample-apps/20260923-001` | app (`workorders`) | `master` | Todo, In Progress, In Review, Deployed to TDD, Deployed to UAT, Deployed to Prod, Done |
| `clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh` | environment (`platform`) | `main` | Todo, In Progress, In Review, Done |

`#N` is an issue in `defaultRepo` (clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh); `owner/repo#N` names the repo (a key of
`repos`). Work on the other repo happens in a fresh clone, under that repo's own `CLAUDE.md`.


## The helper (instead of hand-built curl/JSON)

`B='pwsh -NoProfile -File .claude/skills/feature-loop/board.ps1'`. `<item>` is `N` or
`owner/repo#N` (never a bare `#N` in bash: `#` starts a comment). Tokens come from
the GitHub App `aisf-board`, in this order: `AISF_BOARD_APP_TOKEN` (pre-minted installation token), an installation token
minted from `AISF_BOARD_APP_ID` plus the key in `AISF_BOARD_APP_PRIVATE_KEY_PATH` (file) or `AISF_BOARD_APP_PRIVATE_KEY` (PEM),
then `gh auth token` (honours `GH_TOKEN`); no personal access token variable exists. A call the App cannot make (it has
no Contents: write) is retried once with the `gh` token. Octopus: `OCTOPUS`. Never printed.

| Command | Does | Exit |
|---|---|---|
| `$B move <item> '<Column>'` | `board-status` dispatch to the environment repo; any answer but `204` posts the fallback `board-status:` comment | 0 moved, 1 refused (fallback posted) |
| `$B status <pr\|sha>` | PR state, head, mergeable state, commit statuses of the head (and the merge commit) | 0 |
| `$B deploy <merge-sha>` | the carrying Octopus release and its tdd/uat/prod task states | 0, 1 no release yet, 3 no key |
| `$B wait ci <pr> -Head <sha>` / `wait release <pr>` / `wait deploy <sha> <env>` | polls at the configured interval, prints only changes; `-Head` is the commit just pushed: the result of any other head is never the answer (reference.md "Waiting on the right head") | 0 success, 1 failure, 4 timeout |
| `$B tree <item>` | sub-issue tree (both repos) and the children-first order | 0 |

Exit 2 is a usage error. Run `wait` with `run_in_background: true`; its exit wakes the session.

## Per-column loop

Each row is done by its own fresh subagent (one delegation hop; it never re-delegates). The
item advances exactly one column of its `columnPath`, only after the "verify" fact holds. A
column that does not apply still gets a one-line issue comment saying why (a docs-only app
change still ships through a release, so the deployment columns apply).

| Column | Work | Verify by fact (one command) | Then |
|---|---|---|---|
| (start) | `$B tree <item>`; finish every open descendant first | the printed order has no open child of #N | app: `$B move <item> Todo` |
| Todo | one design comment: problem, approach, acceptance criteria, test plan per layer, risks; app: Onion layers; env: paths, `checksByPath` checks, TB rules at stake, `gitops/` yes/no | the comment exists | `$B move <item> 'In Progress'` |
| In Progress | branch `{username}/{branch-description}` from the default branch; code + tests; gates (below); merge the default branch in; push; PR body `Refs #N` | `$B status <pr>`: open, `mergeable=clean` | `$B move <item> 'In Review'` (app: the PR too) |
| In Review | CI; triage every bot finding (fix, or decline with a one-line PR reply); merge with `mergeMethod` | app: `$B wait ci <pr> -Head <pushed-sha>` exit 0, then merged. env: `codefresh/env-checks` success on the head, gate summary in the PR body, merged | app: `$B wait release <pr>` exit 0. env: Argo CD check if `gitops/` changed (reference.md), then close |
| Deployed to TDD | - | `$B wait deploy <merge-sha> tdd` exit 0 | `$B move <item> 'Deployed to TDD'` |
| Deployed to UAT | - | `$B wait deploy <merge-sha> uat` exit 0 | `$B move <item> 'Deployed to UAT'` |
| Deployed to Prod | - | `$B wait deploy <merge-sha> prod` exit 0 | `$B move <item> 'Deployed to Prod'`; close with the evidence comment; app: `$B move <item> Done` |
| Done | terminal | issue closed | final message |

Environment-repo items move automatically to Todo on open, to In Review only for issues a PR
*closes* (not `Refs`), and to Done on close: dispatch only the moves those events miss (In
Progress, In Review of the issue, parent moves). App-repo items: every move is a dispatch.
Record each move as `board-status: <column> (via dispatch | fallback)`, folded into the
column's comment when one is posted anyway. A refused move never stops the loop.

## Hard rules

- **No merge on red or missing CI.** App: `codefresh/ci`; env: `codefresh/env-checks`; success on the PR head, whatever a bypass allows.
- **Closing is the terminal move.** Never close, and never write `Closes/Fixes/Resolves #N` in
  a PR or commit, before the last verification column is proven (app: Deployed to Prod; env:
  merged, plus Argo CD Synced/Healthy for `gitops/`). The loop closes the issue itself, last,
  with the evidence comment.
- **Children first, depth-first.** A parent is never started while a descendant is open.
  Discovered work (follow-up, defect, deferred bot finding) becomes a child sub-issue of #N,
  never a sibling (reference.md "Children").
- **Parent clamp:** a parent's column is never right of its least-advanced open child; a child
  filed from a late column pulls the parent back (reference.md "Parent clamp").
- **Evidence is API facts only:** commit statuses, Octopus task states, Argo CD state. Never
  "PR created", a local build, or a shell exit code.
- **One git worktree per writing subagent** (`isolation: "worktree"`); every subagent runs on
  `model: "sonnet"` (`subagents` in `factory-loop.json`).
- **Secrets** (`AISF_BOARD_APP_TOKEN`, the App private key, `GH_TOKEN`, `OCTOPUS`, `CODEFRESH`) are never echoed,
  logged, written to repo files, or put on a command line. GraphQL, org endpoints and the
  Actions API are refused in cloud sessions: never retry them.
- Never answer an Octopus manual intervention, override a freeze, or start a runbook.

## Gates

**App (`clearmeasure-aisf-sample-apps/20260923-001`)** - plus the rules of its `CLAUDE.md` (Onion, no new NuGet, no `.octopus/`/build/pipeline edits, NUnit 4 + Shouldly, `Stub` doubles, test naming):
1. `pwsh -NoProfile ./PrivateBuild.ps1` passes before every commit.
2. Merge `origin/master`, resolve conflicts, re-run the private build.
3. `ACCEPTANCE_SKIP_CLEAN=true pwsh -NoProfile ./AcceptanceTests.ps1` before the PR (right after a passing private build).
4. Tests in the same PR at every applicable layer: unit (bUnit, AutoBogus), integration where a module boundary is crossed, Playwright acceptance for every UI change. A layer that does not apply is explained in the PR body.

**Environment (`clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh`)** - plus its `CLAUDE.md`, `docs/tool-boundaries.md` (TB01-TB24) and `docs/scripting.md`:
1. `dotnet test tests/Platform.Conformance.Offline` - only the known C09 failure.
2. `pwsh -NoProfile -File scripts/checks/validate-all.ps1 <checks>` - `always` plus `checksByPath`; a missing tool is a reported `SKIP`.
3. Commit and PR text (CAP-KIT-011): no model identifier, attribution footer or co-author trailer in any commit message, PR title or PR body. Before opening the PR (and after any title or body edit) run `$env:PLATFORM_PR_TITLE='<title>'; $env:PLATFORM_PR_BODY='<body>'; dotnet test tests/Platform.Conformance.Offline --filter "FullyQualifiedName~CommitAttributionGuardTests"`: it checks `origin/main..HEAD` and the PR text, which `codefresh/env-checks` cannot see (a squash merge builds its message from them). A reported `SKIP` (no `origin/main`) is not a pass: fetch and re-run.
4. Merge `origin/main`, re-run both, push; always a PR, never a direct push. The PR body carries the gate summary. A branch-protection refusal is `STATUS: BLOCKED` with the exact message.
5. `codefresh/env-checks` must be `success` on the PR head (`$B status <pr>`) before merging; it runs on every branch push except `main`. Pending: wait (`$B wait ci <pr> -Head <pushed-sha>`); red: fix, never merge over it. The `main` ruleset may let an admin bypass it, so this rule is the gate.

## Waiting and token budget

| Wait | Typical | Poll every | Deadline |
|---|---|---|---|
| `codefresh/ci` on the PR head | 10-20 min | 4 min | 60 min |
| `codefresh/release` on the merge commit | 10-15 min | 4 min | 60 min |
| Octopus deployment, per environment | 2-15 min | 3 min | 90 min |
| Argo CD sync after the merge | 3-5 min | 3 min | 20 min |

- Wait with `$B wait ...` in the background, never a per-minute hand loop; after every
  resumption re-check with one `$B status` / `$B deploy` before acting.
- After a push, wait with the commit you pushed (`$B wait ci <pr> -Head $(git rev-parse HEAD)`): for a moment GitHub
  still names the previous head, and a wait without `-Head` can exit with that head's finished result.
- No observable progress for 20 minutes, or a deadline passed: check state once, then act or
  report `STATUS: BLOCKED` (recovery: reference.md "Failures").
- GitHub MCP tools: `minimal_output: true` and `perPage` 5-10 unless a full body is needed.
- Never re-read a file already read in this session; read only the lines needed.
- Build and test logs: `grep -E 'error|FAIL|Passed!|Failed!' <log> | tail -40`, never a full dump.
- Subagents return at most 15 lines: outcome, PR/SHA, evidence, children, blockers.

## Completion

Never end a turn while CI, the merge, a deployment, the close or a card move is pending ("CI
still running", "monitoring", "will follow up" are forbidden final states). The final message
begins with **`STATUS: COMPLETE`** or **`STATUS: BLOCKED`**, then: final column, PR, merge SHA,
the evidence, card moves that fell back to a comment, children created. Every status update
says what happened, what it means for the work item, and what happens next, in software-team
vocabulary (work item, defect, pull request, build, release, deployment, board status).
