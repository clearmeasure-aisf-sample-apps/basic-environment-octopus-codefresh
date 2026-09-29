# Design diagrams

Every level of the platform design has a picture next to its text. The sources are PlantUML
(C4-PlantUML for structure, plain PlantUML for sequences, activities, classes and trees). Each
`<name>.puml` has its rendered `<name>.png` beside it, and the PNG is embedded in the Markdown file
that holds the text of that level. `design/architecture-views.md` walks through them top-down.

## Render

```powershell
pwsh scripts/diagrams/render.ps1                        # every diagram; writes design/diagrams/manifest.sha256
pwsh scripts/diagrams/render.ps1 c4-1-system-context    # one diagram
```

The script uses the `plantuml/plantuml` image (pinned by digest) when a Docker daemon answers, and
otherwise PlantUML 1.2026.8 from Maven Central (pinned by SHA-256) with a local Java 11 or later.
Both are the same PlantUML build, and `include/palette.puml` sets the Smetana layout engine,
which is built into PlantUML, so neither path needs Graphviz and both draw the same layout.

`pwsh scripts/checks/validate-all.ps1 diagrams` fails when a source changed without a new render, when
a PNG was edited by hand, when a PNG is embedded nowhere, or when an image link is broken; env-checks
also runs `plantuml -checkonly` over every source in the `plantuml/plantuml` image. Commit the `.puml`,
the `.png` and `manifest.sha256` together.

## Conventions

- **One file per view.** Name by level:

  | Prefix | Level | PlantUML |
  |---|---|---|
  | `c4-1-` | System context | C4 context |
  | `c4-2-` | Containers and their deployment | C4 container, C4 deployment |
  | `c4-3-` | Components of one container or area | C4 component, deployment or dynamic |
  | `c4-4-` | Code: classes, schemas, file trees | class, object, WBS |
  | `dyn-` | Behaviour: flows, step graphs, decisions | sequence, activity |
  | `view-` | Other design views: responsibilities, topology, roadmap, packages | any |

- **First lines.** `@startuml <name>`, then `!include include/c4.puml` for C4 diagrams or
  `!include include/palette.puml` for every other kind. Never include remote URLs.
- **Title.** `Level 1: …`, `Level 2: …`, `Level 3: …`, `Code: …`, `Dynamic: …` or `View: …`, with the
  design reference in parentheses, for example `(ADR-IR34, §7.2)`.
- **Colours.** Tag each element with the tool that owns it (`github`, `codefresh`, `octopus`,
  `argocd`, `azure`, `k8s`, `app`, `saas`, `legacy`, `deferred`) and each relationship with the
  verb's tool (`build`, `release`, `sync`, `wake`, `identity`, `forbidden`). Tier boundaries use
  `tier_build`, `tier_nonprod`, `tier_prod`, `tier_global`; deployment nodes use `node_build`,
  `node_nonprod`, `node_prod`, `node_global`. Plain diagrams use the `$C_*` colours of
  `include/palette.puml`. C4 diagrams end with `SHOW_LEGEND()`.
- **Names.** Use the binding names of design §7.0 and the placeholders of §7.0 and §7.1
  (`<app>`, `<env>`, `<tier>`, `<acr-name>`, `<cf-runtime>` and so on). Never draw a secret, a key,
  a live IP address or a generated resource suffix. Exception: the as-built views (`c4-2-as-built-*`,
  `dyn-as-built-*`) name the provisioned resources as the runbooks do (for example `acrplatformi3aldz`),
  still without secrets, keys or IP addresses.
- **Size.** At most about 25 elements. Split a busier view into two files with `-a` and `-b`.
- **Text.** Short descriptions (two lines at most); no "I", "we" or "you".
