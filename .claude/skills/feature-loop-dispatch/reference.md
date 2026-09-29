# Feature-loop dispatch reference

Read a section only when `SKILL.md` points here. `B` and `W` as in `SKILL.md`.

## Why the watchdog is external

Sub-sessions stall silently: they start a background poller and end their turn, and its
notification routes to whoever is listening (often the orchestrator, not the stopped work).
A PR can then sit green and unmerged, or a release sit in prod with its issue open, for hours.
So detection is mechanical: `Check-StalledLanes.ps1` reads GitHub REST (and Octopus when
`$OCTOPUS` is set), and the heartbeat wakes the orchestrator every ~15 minutes whatever it
finds. Never wait open-ended on sub-session notifications alone; relay a notification that
reached the orchestrator to the owning work with SendMessage.

## Findings

| Finding | Meaning | Action |
|---|---|---|
| `GREEN_UNMERGED` | app: `codefresh/ci` success, PR open > 15 min | SendMessage the owner: "PR #N is green; triage bots, merge, report." Not resumable: verify status, triage/reply bot findings, merge yourself, or spawn a closer subagent |
| `IDLE_PR` | environment: no PR CI, PR untouched | ask the owner for its gate summary and merge, or a closer that re-runs the gates and merges |
| `DIRTY` | merge conflicts | order (or spawn) a pass that merges the default branch in, re-runs the gates, re-pushes |
| `CI_FAILED` / `CI_STUCK` | PR CI failed, pending too long, or missing | order a fix and re-push, or ask the operator to re-run the Codefresh build (no Codefresh write path here) |
| `RELEASE_FAILED` / `RELEASE_STUCK` | `codefresh/release` on the merge commit | file a child defect with the build link, or report to the operator |
| `DEPLOY_FAILED` / `DEPLOY_STUCK` / `DEPLOY_WAITING` | Octopus task of the carrying release | child defect with the task ID, or `BLOCKED` for an intervention or freeze; never answer or override |
| `DEPLOYED_ISSUE_OPEN` / `MERGED_ISSUE_OPEN` | verified but not closed | check sub-issues first (held open by open children is not a stall); `gitops/`: Argo CD first; close with the evidence comment; app issues: `$B move <item> Done` |
| `LOCAL_STALL` | a running sub-session's output file untouched | SendMessage; silent past 20 minutes: a fresh subagent in a new worktree takes over |

## Resumption

1. `$B lane` - the whole batch in a few lines.
2. For each `running` lane: one `$B status <pr>` (or `$B deploy <merge>` past the merge), then
   SendMessage its `agent` or dispatch a fresh subagent from that lane's column.
3. Re-arm the watchdog heartbeat.

Lane records never replace verification: they say where to look, the API says what is true.

## Clamp and promotion detail

- A new child behind its ancestor pulls the ancestor back: reopen it if closed (environment
  repo: lands in Todo by itself; app repo: `$B move <ancestor> Todo`), then
  `$B move <ancestor> '<clamp column>'` if further right, and comment naming the child.
- When the last open child of an epic is done, the epic advances one column at a time, each
  transition by its own subagent (a no-op justification pass is still a pass), until its own
  verification holds; then it is closed (app epic: `$B move <epic> Done`). An epic never
  advances in the same action that closed its child.

## Communication examples

- Not "evidence PR": "a pull request that commits the test-run results to the repository".
- Not "clamped": "the parent work item stays open and its board status moves back to match its
  least-finished open sub-item".
- Not "lane", "loop", "chain" or agent IDs: "the work on item #N".
- Not "blocks #N's passing evidence": "defect #X must be fixed before we can re-run the tests
  and show item #N working".
