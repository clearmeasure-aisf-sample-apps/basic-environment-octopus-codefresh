# Feature loop reference

Read a section only when `SKILL.md` points here or a situation needs it. `B` is
`pwsh -NoProfile -File .claude/skills/feature-loop/board.ps1`.

## Moving cards

Cloud sessions cannot call GraphQL, org endpoints or the Actions API (the proxy refuses them),
so cards move through `.github/workflows/project-board.yml` of the environment repo (secret
`PROJECTS_PAT`; "Board automation" in that repo's `docs/tool-boundaries.md`). Its events fire
only for issues and PRs of the environment repo; GitHub Actions stays off in app repos.

| Event (environment repo only) | Board change |
|---|---|
| issue opened / reopened | Todo |
| issue closed, any reason | Done |
| PR opened / reopened / ready for review | the PR and every issue it *closes* -> In Review |
| PR merged | the same items -> Done |
| `repository_dispatch` `board-status` / `workflow_dispatch` (`repository`, `issue`, `status`) | the named item -> the named column; adds it to the board if missing |

- `$B move` sends the dispatch (`POST /repos/<env repo>/dispatches`, payload
  `{repository, issue, status}`); `204` is success. The token needs *Contents: write* on the
  environment repo. Dispatches are idempotent. Do not poll workflow runs (Actions API refused).
- Cheap confirmation when needed: `GET /repos/{o}/{r}/issues/{n}/timeline?per_page=5` shows
  `added_to_project_v2` or `project_v2_item_status_changed` after the request.
- Refused (any other status): `$B move` posts `board-status: <column> -- intended board column;
  the board automation could not be reached from this session, so the card needs a manual
  move.` Name the refused call and status in the next update and continue.
- **Locally with `gh` and the `project` scope**, GraphQL is allowed; resolve IDs at run time:

  ```bash
  gh api graphql -f query='query{organization(login:"clearmeasure-aisf-sample-apps"){projectV2(number:678){id fields(first:50){nodes{... on ProjectV2SingleSelectField{id name options{id name}}}}}}}'
  gh api graphql -f query='query($o:String!,$r:String!,$n:Int!){repository(owner:$o,name:$r){issue(number:$n){id projectItems(first:20){nodes{id project{number}}}}}}' -F o=<owner> -F r=<repo> -F n=<N>
  gh api graphql -f query='mutation($p:ID!,$i:ID!,$f:ID!,$v:String!){updateProjectV2ItemFieldValue(input:{projectId:$p,itemId:$i,fieldId:$f,value:{singleSelectOptionId:$v}}){projectV2Item{id}}}' -F p=<projectId> -F i=<itemId> -F f=<statusFieldId> -F v=<optionId>
  ```

  The item ID is the ProjectV2 item (project 678 in `projectItems`), not the issue node ID; an
  issue not on the board is added with `addProjectV2ItemById`.
- The optional project field `App` is set by the workflow when it adds an item; never by hand.

**Reading a card's column** in a cloud session (rightmost match wins): issue closed -> Done;
latest `board-status:` marker; app: a `Success` deployment of the carrying release -> that
environment's column (`$B deploy <merge-sha>`); a `Refs #N` PR open or merged -> In Review; a
branch or draft PR -> In Progress; otherwise Todo. Locally: GraphQL `fieldValueByName(name:"Status")`.

## Failures

| Fact | Action |
|---|---|
| `codefresh/ci` `failure`/`error` | read the build link from `$B status`, fix, re-run the gates, re-push |
| `codefresh/ci` missing 30 min after the push, or `pending` > 60 min | re-push (empty commit is fine) or ask the operator to re-run the Codefresh build |
| PR `mergeable=dirty` | merge the default branch in, re-run the gates, re-push |
| `codefresh/release` failed on the merge | file a child defect with the build link; stop the item |
| `$B deploy`: `Failed` / `Canceled` / `TimedOut` | file a child defect with the task ID; stop the item |
| `WAITING-ON-INTERVENTION`, `prod-weekend-freeze`, or `Queued`/`Executing` > 90 min | `STATUS: BLOCKED` with the task ID; never answer, override or run runbooks |
| branch protection refuses the merge | `STATUS: BLOCKED` with the exact refusal; never bypass |
| a GitHub/Octopus call refused (proxy, permission, classifier) | report the exact call and status; card moves use the fallback comment |
| worktree unusable | report at once; never improvise outside it |

A local build failure is diagnosed to root cause, never dismissed as environmental.

## Octopus (what `$B deploy` does)

Server `https://clearmeasure.octopus.app`, space `Spaces-335`, project `workorders`, lifecycle
`platform-continuous` (tdd -> uat -> prod automatically). Key `$OCTOPUS` goes only in the
`X-Octopus-ApiKey` header through standard input:

```bash
oct() { printf 'header = "X-Octopus-ApiKey: %s"\n' "$OCTOPUS" | curl -fsS --config - "https://clearmeasure.octopus.app/api/Spaces-335/$1"; }
oct "projects/<projectId>/releases?take=30"       # ReleaseNotes line "app-commit: <sha>"
oct "deployments?projects=<projectId>&environments=<envId>&take=10"
oct "tasks/<TaskId>"                              # State, HasPendingInterruptions
```

The carrying release is the one whose `app-commit` equals the merge SHA, or else the oldest
later release whose `app-commit` contains it (`GET /repos/{o}/{r}/compare/{merge}...{app_commit}`
-> `ahead` or `identical`). Name its version in every evidence comment.

## Argo CD (environment repo, `gitops/` changes)

After a few minutes: every Application rendered from the changed paths reports sync `Synced`
and health `Healthy` (`kubectl get applications.argoproj.io -n argocd -o json` on the tier's
cluster, with the read access that repo's rules grant; prod is read-only). Summarise with
`jq -r '.items[]|"\(.metadata.name) \(.status.sync.status) \(.status.health.status)"'`. An
asleep tier is not woken outside `env-wake` (TB17); if the check cannot run, the item stays In
Review with `STATUS: BLOCKED` and the reason.

## Children

```bash
# child_id is the numeric "id" of GET /repos/{o}/{r}/issues/{child}, NOT its number
POST /repos/{o}/{r}/issues/{N}/sub_issues   {"sub_issue_id": <child_id>}
$B tree <owner/repo#N>                      # verify
```

The GitHub MCP sub-issue tool does the same. An environment-repo child lands in Todo when
opened; an app-repo child needs `$B move <child> Todo`. Independent siblings may run in
parallel, each in its own worktree.

## Parent clamp

`parent = min(intended, min(column of each open child))` along `columnOrder`. A parent is never
closed while a child is open. A child filed from a late column pulls the parent back: reopen
it if closed (environment repo: the reopen lands in Todo by itself; app repo: `$B move <parent>
Todo`), then `$B move <parent> '<clamp column>'` if that is further right. Comment on the parent
naming the child that caused it. The parent advances again only as its children advance.

## Evidence comment (the close)

```
Verified and closing (board: Done).
- PR #<n>, merged as <merge-sha>
- codefresh/ci success on <head-sha>; codefresh/release success on <merge-sha>
- release <version>: tdd <Deployments-x>/<ServerTasks-y> Success; uat ...; prod ...
  (environment repo instead: gate summary; Argo CD <app> Synced/Healthy)
- children: #<c> (closed) | none
```

Closing as not planned or duplicate also lands in Done: only for a genuinely abandoned item,
said so in the comment. A merged PR moves the PR card, not a `Refs` issue.

## Communication

Plain software-team vocabulary: work item, defect, pull request, build, automated tests, test
run, release, deployment, board status. Not "evidence PR", "clamp", "lane", "loop", or agent
IDs in user-facing text: say "the parent work item stays open and its board status moves back
to match its least-finished open sub-item", "the work on item #N", "defect #X must be fixed
before we can re-run the tests and show item #N working".
