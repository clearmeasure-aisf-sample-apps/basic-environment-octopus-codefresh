---
name: feature-loop-dispatch
description: >
  Authorize a batch of work items for full autonomous implementation on the shared board
  https://github.com/orgs/clearmeasure-aisf-sample-apps/projects/678 (bare `#N` = an issue
  in clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh; `owner/repo#N` for the other repo on the board). Resolves each
  item's epic/child tree, computes a children-first execution order, and dispatches one
  dedicated sub-session (subagent) per work item that runs the /feature-loop skill
  end-to-end - code, tests, PR, API-verified Codefresh/Octopus/Argo CD evidence, merge,
  board moves through the board workflow, and the closing move to Done - with parent
  clamp rules enforced. Use when asked to "dispatch the feature loop on ...", "implement
  these work items", or when /feature-loop-dispatch is invoked with a list of issue
  numbers. The invoking session stays alive as the orchestrator until every authorized
  item is Done or hard-blocked.
---

# Feature-Loop Dispatch

This session is the **orchestrator** of an authorized batch: it runs unattended to completion
and never edits code. Per-item rules: `.claude/skills/feature-loop/SKILL.md` and
`.claude/factory-loop.json` (read once; do not re-read). Rare detail (finding playbook,
prompt rationale, resumption): `reference.md` next to this file. A user's own global rules may
add to but never weaken these.


`B='pwsh -NoProfile -File .claude/skills/feature-loop/board.ps1'`,
`W='pwsh -NoProfile -File .claude/skills/feature-loop-dispatch/Check-StalledLanes.ps1'`.

**Input:** issue numbers (`N` for clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh, `owner/repo#N` for the other repo). None
given: ask once for the list; that is the only permitted question.

## Lane state: the single source of truth

`$B lane` keeps one record per work item in `<git common dir>/feature-loop/lanes.json`
(outside the tree, shared by all worktrees). The orchestrator is its only writer. Update it on
every transition, and on resumption read it first (`$B lane`) instead of re-deriving state.

```bash
$B lane 123 status=queued parent=100 column=Todo
$B lane 123 status=running agent=<agent-id> pr=145
$B lane 123 column='Deployed to TDD' merge=<sha> release=2.5.750
$B lane 123 status=done            # or status=blocked blocked='<reason>'
$B lane                             # one line per item
```

`status` is `queued | running | blocked | done`; other fields per `laneState` in
`factory-loop.json`. After a resumption, verify only the `running` lanes with one
`$B status <pr>` / `$B deploy <merge>` each.

## Phases

| Phase | Do | Done when |
|---|---|---|
| 0 Watchdog | `$W -Repo <owner/repo>` once per repo in the set (exit 0 none, 1 stalls, 2 usage). Then a background heartbeat: `sleep 900; $W -Repo ...` per repo (add `-TasksDir <tasks dir> -ActiveIds <ids>`), exiting unconditionally; re-arm it every turn it fired | armed for the whole session |
| 1 Tree | `$B tree <item>` for each authorized item (descendants join the set; authorizing an epic authorizes its open descendants); write a lane per item; post the tree and order on each top-level item | every lane `queued` with `parent` |
| 2 Dispatch | per item whose open descendants are all done: one background subagent, `subagent_type: "claude"`, `model: "sonnet"`, `isolation: "worktree"` (other repo: its own fresh clone); cap 3 running; prompt below | lane `running` with `agent` |
| 3 Verify + clamp | on each report: verify with `$B status <pr>` and `$B deploy <merge>` (never the subagent's word); new children join as lanes and clamp their ancestors (`ancestor = min(intended, min(open child columns))`, `$B move`, a comment naming the child); promote an epic one column at a time, each by its own subagent, only after its last child is done | lane updated; next wave dispatched |
| 4 Finish | continue until every lane is `done` or `blocked`; a failed sub-session gets a new subagent, a corrected prompt, or a child defect | all lanes final |

Watchdog findings and their actions: `reference.md` "Findings" (short form: `GREEN_UNMERGED`
-> tell the owner to triage and merge, else a closer subagent; `DIRTY` -> merge the default
branch in; `CI_*` -> fix and re-push; `RELEASE_*`/`DEPLOY_*` -> child defect or BLOCKED;
`*_ISSUE_OPEN` -> finish the closeout; `LOCAL_STALL` -> SendMessage, take over after 20 min).

## Sub-session prompt (verbatim, fill in N and the repo)

> Run the feature loop on work item #N in <owner/repo>, board
> https://github.com/orgs/clearmeasure-aisf-sample-apps/projects/678. Follow
> `.claude/skills/feature-loop/SKILL.md` exactly (per-column loop, gates, hard rules; open its
> `reference.md` only for the section a situation needs). Use
> `.claude/skills/feature-loop/board.ps1` for every card move, status check, deployment check
> and wait. PRs say `Refs #N`, never a closing keyword. Close the issue only after its last
> verification column is proven. Discovered work becomes a child sub-issue of #N.
> ANTI-STALL: (1) spawn at most one hop of column subagents; they do work, never re-delegate.
> (2) Once CI is green, triage and merge in the same turn; once prod (or Argo CD) is verified,
> close in the same turn. (3) Wait with `board.ps1 wait ...` in the background; after every
> resumption re-check state with one command. (4) A refused call (proxy, permission, HTTP
> status) is reported exactly; card moves fall back to the `board-status:` comment. (5) An
> unusable worktree is reported at once. (6) No progress for 20 minutes (CI 60, a deployment
> 90): check state, then take over or report the blockage. (7) Never print or put on a
> command line GITHUB_SAMPLE_APPS_PAT, GH_TOKEN, OCTOPUS or CODEFRESH.
> REPORT at most 15 lines, starting `STATUS: COMPLETE` or `STATUS: BLOCKED`: final column, PR,
> merge SHA, evidence (status contexts, release version, deployment/task IDs or Argo CD
> state), card moves that fell back to a comment, children created.

## Session-end gate

Never end a turn (or send a user-facing final message) while any lane is `queued` or
`running`, a PR of the set is open, a status or deployment is pending, or a sub-session's
report is unverified. "Waiting on CI", "deploying", "monitoring", "will follow up" are
forbidden final messages. Before the only final message, verify:

- [ ] `$B lane`: every lane `done` (issue closed after its last verification column) or
  `blocked` with the reason also on the issue
- [ ] every PR merged, or why not documented
- [ ] app items: `codefresh/ci` and `codefresh/release` success, tdd/uat/prod `Success`
  (`$B deploy <merge>`); environment items: merged, gate summary, Argo CD for `gitops/`
- [ ] `$W -Repo <repo>` for every repo in the set: no finding for the set

The final message begins with **`STATUS: COMPLETE`** or **`STATUS: BLOCKED`** (the exact
blocker), then one short block per item: final column, PR, merge SHA, evidence, fallback card
moves, children and their outcomes. Updates follow the communication rules of the feature-loop
`reference.md` (what happened, what it means, what happens next; no orchestration jargon).

## Hard rules

- One subagent = one work item's current column step; the orchestrator never edits code.
- Every writing subagent: `model: "sonnet"` + its own worktree; at most 3 running.
- A parent never outranks its least-advanced open child; closing is the terminal move.
- CI is verified by commit statuses, deployments by the Octopus API, cards by `$B move`.
- `GET /rate_limit` before each dispatch wave; GitHub MCP reads with `minimal_output: true`.
