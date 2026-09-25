# Codefresh starter: minimal

One image built from the app repository's own root `Dockerfile`, one test step, and the
handshake of contract §7.0. Any language. After `Platform.Onboarding scaffold <app> --codefresh
minimal`, the copy under `codefresh/apps/<app>/` belongs to the app (directive §9).

| File | Content |
|---|---|
| `pipelines/ci.yml` | Branch pushes: the `test` step, then an image build without a push |
| `pipelines/release.yml` | Pushes to `<app-branch>`: `wake_nonprod`, `test`, `web_image` (`apps/<app>/web`, keyless signature), `supply_chain` (SBOM, provenance, tag lock), and the Octopus release of project `<app>` |
| `specs/*.yml` | Triggers, runtime `aks-platform-build/codefresh`, contexts (`platform-registry`, `platform-octopus` for the release) |
| `scripts/` | `version.ps1` (`MAJOR.MINOR.<height>`, `-ci.<sha7>` off `<app-branch>`), `supply-chain.sh`, `buildinfo.ps1` |

Edit after scaffolding: the `test` command, the image name if the descriptor's
`deployables[].images` is not `[web]`, and `version.env`. Keep the handshake: images under
`apps/<app>/`, the signature and SBOM, explicit Octopus packages, no deploy, fork events off.
