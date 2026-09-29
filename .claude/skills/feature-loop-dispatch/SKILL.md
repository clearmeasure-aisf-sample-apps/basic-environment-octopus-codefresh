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

You are the **orchestrator**. The user has authorized the listed work items for full,
unattended implementation. From this point the session runs to completion without asking
anything further - every decision is made autonomously under the rules below.

The per-item rules are defined by `.claude/skills/feature-loop/SKILL.md` and
`.claude/factory-loop.json` (board 678, column map, per-repo gates, CI and deployment
signals, board-move transport). Those two files are the contract; a user's own global
rules may add to but never weaken them. Everything there about moving cards (automatic
events, `board-status` dispatch to the environment repo, fallback comment, GraphQL only
locally), closing as the terminal move, and API-verified evidence applies to the
orchestrator too.

## Phase 0 - Start the stall watchdog (before any dispatch)

Sub-sessions stall silently: they spawn a background poller and end their turn, and its
notification routes to whoever is listening (often the orchestrator, not the stopped
work) - so a PR can sit green and unmerged, or a release can sit in prod with its issue
open, for hours. Detection must be EXTERNAL and MECHANICAL:

1. Baseline once per repo in the work set (read-only; GitHub REST plus Octopus reads when
   `$OCTOPUS` is set; exit 0 = no stalls, 1 = stalls found, 2 = usage error):

   ```bash
   pwsh -NoProfile -File .claude/skills/feature-loop-dispatch/Check-StalledLanes.ps1 -Repo <owner/repo>
   ```

   `-Repo` defaults to `defaultRepo` (clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh); the script reads the repo's kind,
   CI contexts and Octopus project from `factory-loop.json`. Add `-Json` for machine
   output.
2. Run it as a HEARTBEAT, not an alarm: a background command that sleeps ~15 minutes,
   runs ONE check per repo (pass `-TasksDir <session tasks dir> -ActiveIds <ids>` so
   pre-PR local stalls show up through output-file staleness), and EXITS UNCONDITIONALLY
   so it wakes you every cycle regardless of findings. Re-arm it at the end of every turn
   in which it fired. NEVER wait open-ended on sub-session notifications alone.
3. Act on every finding immediately:
   - `GREEN_UNMERGED` (app: `codefresh/ci` success, PR open) -> SendMessage the owning
     work: "PR #N is green; triage bots, merge, report." If it cannot be resumed, do the
     merge-side finish yourself (verify status, triage/reply bot findings, merge) or spawn
     a fresh closer subagent.
   - `IDLE_PR` (environment repo: no PR CI, PR untouched) -> ask the owning work for its
     gate summary and merge, or spawn a closer that re-runs the gates and merges.
   - `DIRTY` -> order (or spawn) a conflict-resolution pass: merge the default branch into
     the branch, re-run the gates, re-push, re-verify.
   - `CI_FAILED` / `CI_STUCK` -> order a fix-and-repush, or ask the operator to re-run
     the Codefresh build (this session holds no Codefresh write path).
   - `RELEASE_FAILED` / `RELEASE_STUCK` (`codefresh/release` on the merge commit) -> file
     a child defect with the build link from the status, or report to the operator.
   - `DEPLOY_FAILED` / `DEPLOY_STUCK` / `DEPLOY_WAITING` (Octopus task of the carrying
     release) -> file a child defect with the task ID, or report `BLOCKED` for a manual
     intervention or freeze. Never answer interventions or override freezes.
   - `DEPLOYED_ISSUE_OPEN` (app: prod deployment succeeded, issue still open) and
     `MERGED_ISSUE_OPEN` (environment: merged, issue open) -> finish the closeout: for
     `gitops/` changes check Argo CD first, then close the issue with the evidence comment
     (then Done: automatic for environment-repo issues, a `Done` dispatch for app-repo
     issues). Verify sub-issues first: an issue held open behind open
     children is not a stall.
   - `LOCAL_STALL` -> SendMessage the owning work; if silent past 20 minutes, take over
     with a fresh subagent in a new worktree.

## Communication standard (every update, issue comment, and PR description)

Write like a software delivery leader briefing a stakeholder: plain software-team
vocabulary only - work item, defect, pull request, build, automated tests, test run,
release, deployment, board status, dependency. Never invent orchestration jargon:

- Don't say "evidence PR" - say "a pull request that commits the test-run results
  (logs/output) to the repository."
- Don't say "clamp / clamped" - say "the parent work item stays open and its board status
  moves back to match its least-finished open sub-item" (the rule itself is unchanged).
- Don't say "lane," "loop," "chain," or agent IDs in user-facing updates - say "the work
  on item #N."
- Don't say "blocks #N's passing evidence" - say "defect #X must be fixed before we can
  re-run the tests and show item #N working."
- Every status update states, in order: what happened, what it means for the work item,
  and what happens next. A reader who has not followed the session must understand it
  cold.

## Inputs

The argument is a list of work items (the "authorized set"): `#N` / `N` for
clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh, `owner/repo#N` for the other repo on the board. If no argument is given,
ask once for the list before starting; that is the only permitted question.

## Phase 1 - Resolve the tree (before any work)

1. `GET /rate_limit` - record the REST budget. There are no cached board IDs: cloud
   sessions move cards only through the board workflow; a local session with `gh` and the
   `project` scope resolves project/field/option IDs at runtime (feature-loop skill,
   "Moving cards").
2. For every authorized item, recursively resolve
   `GET /repos/{o}/{r}/issues/{n}/sub_issues` to the deepest descendant (children may live
   in the other repo). The full tree of every authorized item joins the work set -
   authorizing an epic authorizes its open descendants.
3. Build the execution order:
   - **Children first, depth-first.** A parent/epic enters the dispatch queue only after
     ALL of its open descendants are Done.
   - Open leaf items (no open children) are the initial dispatch wave.
   - Independent items run in parallel; explicitly ordered chains run sequentially.
4. Post the resolved tree and planned order as a comment on each authorized top-level
   item, and print it in the session before dispatching.

## Phase 2 - Dispatch one sub-session per work item

For each work item whose turn has arrived, launch **one dedicated subagent** via the
Agent tool:

- `subagent_type: "claude"` (general, full tools), `model: "sonnet"` - never Haiku.
- `isolation: "worktree"` - every writing sub-session gets its own git worktree (for an
  item of the other repo: its own fresh clone). No two sub-sessions share a checkout.
- Run in the background so independent items proceed concurrently. Cap concurrency at 3
  writing sub-sessions (`concurrency` in `factory-loop.json`).

The prompt must instruct it to **run the feature loop on exactly that one work item**,
including verbatim (fill in the repo):

> Run the feature loop on work item #N in <owner/repo>, on board
> https://github.com/orgs/clearmeasure-aisf-sample-apps/projects/678. Follow
> `.claude/skills/feature-loop/SKILL.md` and `.claude/factory-loop.json` exactly: work in
> your own worktree from the repo's default branch; one board column at a time along the
> repo's `columnPath` via a fresh subagent per column (design -> implement -> verify ->
> post-merge verification), never skipping columns, recording no-op justifications for
> non-applicable columns; merge the default branch into your branch and re-run the repo's
> gates before any push or PR (app: `pwsh -NoProfile ./PrivateBuild.ps1`, then
> `pwsh -NoProfile ./AcceptanceTests.ps1` before the PR; environment:
> `dotnet test tests/Platform.Conformance.Offline` with only the known C09 failure, and
> `pwsh -NoProfile -File scripts/checks/validate-all.ps1 <checks>`); reference the item
> with `Refs #N`, never a closing keyword; triage every bot review finding (fix or decline
> with a PR reply) before merge; app PRs are verified only by commit status
> `codefresh/ci` = success on the head SHA, then `codefresh/release` on the merge commit
> and the Octopus deployments of project workorders to tdd, uat and prod - never a shell
> exit code; environment PRs by the local gates, plus Argo CD Synced/Healthy for
> `gitops/` changes; follow the Testing Policy of the repo. Move cards only by
> `board-status` dispatches to
> clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh (every move for
> app-repo items; the moves the board workflow's own events do not make for
> environment-repo items; fallback: the `board-status:` issue comment; GraphQL only
> locally with the project scope). Close the issue only after its last verification
> column is proven - closing is the move to Done (plus a `Done` dispatch for app-repo
> issues).
> Any discovered follow-up work becomes a CHILD sub-issue of #N (POST the child's numeric
> id to `repos/{o}/{r}/issues/N/sub_issues`) - report every child you create. Final
> report: final board column, PR number, merge commit SHA, the CI/release/deployment (or
> Argo CD) evidence, any card move that fell back to a comment, children created.

**Anti-stall requirements - add these to every sub-session prompt verbatim:**

> ANTI-STALL RULES (mandatory): (1) NO DISPATCHER CHAINS - you may spawn subagents for
> column work, but a subagent you spawn must DO work, never merely re-delegate to another
> subagent; at most one delegation hop below you. (2) SYNCHRONOUS FINISH - once CI is
> green, do the bot-finding triage and merge in the SAME turn; once the last deployment
> (or Argo CD check) is verified, close the issue in the SAME turn; never end your turn
> between "green" and "merged", or between "deployed to prod" and "closed". (3) When
> waiting on CI or a deployment, poll with a bounded foreground loop or a background task
> you own, and after EVERY resumption re-check the PR, status and deployment state
> directly before assuming anything. (4) If any GitHub, Octopus or dispatch call is
> refused (proxy, permission hook, classifier, HTTP status), do not stop silently -
> report the exact call and status in your final message, and use the `board-status:`
> fallback comment for card moves. (5) If your worktree becomes unusable, report it
> immediately rather than improvising outside it. (6) Every wait must have a deadline: if
> a subagent, CI run or deployment has made no observable progress in 20 minutes (CI 60,
> each deployment 90), stop waiting, check state directly, and either take over or
> report the blockage. (7) Never print or pass on a command line the values of GH_TOKEN,
> OCTOPUS or CODEFRESH.

## Phase 3 - Parent clamp and promotion (orchestrator's job, after every completion)

When a sub-session reports:

1. Verify its claims independently: commit statuses (`codefresh/ci` on the PR head,
   `codefresh/release` on the merge commit), the Octopus deployment tasks, the Argo CD
   state it quotes, the issue state, and the card column (GraphQL locally; otherwise the
   REST-derived column of the feature-loop skill). Never take a subagent's word for CI or
   deployments.
2. **New children discovered** join the work set immediately, are dispatched under the
   same rules, and clamp their parent (below).
3. **Clamp every affected ancestor:** `ancestor = min(intended, min(column of each open
   child))` along `columnOrder`. An ancestor is pulled BACK (and reopened if closed; the
   reopen lands it in Todo - by itself in the environment repo, by a `Todo` dispatch in the
   app repo - then dispatch the clamp column) when a child appears behind
   it. Record each clamp as a comment on the ancestor naming the child that caused it.
   Clamp moves use the `board-status` dispatch.
4. **Promote parents only by clamp release:** when the last open child of an epic is
   Done, advance the epic one column at a time - each transition by its own subagent (a
   no-op justification pass is still a pass) - until its own verification is complete,
   then close it (and, for an app-repo epic, dispatch `Done`). An epic never advances in the same action
   that closed its child.
5. Dispatch the next queued item(s) whose prerequisites are now met.

## Phase 4 - Walk-away completion

The orchestrator continues until every item in the (grown) work set is Done or hard-
blocked. Use background subagents and wait for their notifications - but NEVER on
notifications alone: keep the Phase 0 heartbeat running on its ~15-minute cadence for the
whole session, because a grandchild's notification may route to you instead of its
stopped parent. Relay such results to the owning work via SendMessage yourself. If a
sub-session fails, read its output, fix the dispatch (new subagent, corrected prompt, or a
filed child defect), and continue; a local build failure is diagnosed to root cause, never
dismissed as environmental.

**Phase 4 does not authorize ending the session.** Session termination requires Phase 5.

## Phase 5 - Completion heartbeat (mandatory - never hand off while pending)

The orchestrator MUST NOT end a turn - or send a final user-facing message - while ANY of
these is unresolved:

- An authorized work item not verified Done (issue closed after its last verification
  column, or hard-blocked with the reason recorded on the issue)
- A PR of the work set still open
- An app PR head or merge commit whose `codefresh/ci` / `codefresh/release` status is
  `pending`, or not `success`
- An app item whose carrying release has not been verified deployed to tdd, uat and prod
- An environment item with `gitops/` changes whose Argo CD Applications have not been
  verified Synced/Healthy
- A dispatched sub-session whose outcome has not been independently verified

### Forbidden terminal messages

"CI is still running" / "waiting on CI" / "deploying" / "in progress" / "monitoring" /
"will follow up" / "polling started", or any partial summary that leaves verification
unfinished. Keep working - poll, SendMessage, merge, close, or fix - until the exit
criteria are met or a hard block is documented.

### Polling heartbeat (60-90 seconds)

Whenever a status or deployment is pending, poll every 60-90 seconds (foreground bounded
loop or a background task that exits when done):

```bash
printf 'header = "Authorization: Bearer %s"\n' "$GH_TOKEN" | curl -sS --config - \
  "https://api.github.com/repos/<owner>/<repo>/commits/<sha>/status" \
  | python3 -c 'import json,sys; d=json.load(sys.stdin); print([(s["context"], s["state"]) for s in d["statuses"]])'
```

then the Octopus deployment tasks (feature-loop skill, "Post-merge verification"). The
~15-minute watchdog and this poll both run for the whole session.

### Session-end gate (independent verification)

Before the ONLY permitted final message, verify ALL of:

- [ ] Every work item: closed after its last verification column, or hard-blocked with a
  documented reason
- [ ] Every PR: merged (or documented why merge was impossible)
- [ ] App items: `codefresh/ci` success on the merged head, `codefresh/release` success on
  the merge commit, deployment tasks `Success` in tdd, uat and prod
- [ ] Environment items: merge on `origin/main`, gate summary in the PR, Argo CD
  Synced/Healthy for `gitops/` changes
- [ ] `Check-StalledLanes.ps1` for every repo in the work set: no finding for the work set

The final message MUST begin with **`STATUS: COMPLETE`** or **`STATUS: BLOCKED`** (with
the exact blocker). Then, per item: final column, PR, merge SHA, the evidence (status
contexts, release version, deployment/task IDs or Argo CD state), card moves that fell
back to a comment, children created (and outcomes), and any item left blocked and exactly
why.

## Hard rules (restated, non-negotiable)

- One subagent = one work item's current column step; no subagent carries an item across
  multiple columns, and the orchestrator itself never edits code.
- Every writing subagent: Sonnet + own worktree.
- A parent never outranks its least-advanced open child on the board.
- Closing an issue is the terminal move; nothing is closed before its verification.
- CI is verified via commit statuses and deployments via the Octopus API only.
- Cards move by the board workflow's events and `board-status` dispatches (GraphQL only
  locally); REST-first; check `rate_limit` before each dispatch wave.
