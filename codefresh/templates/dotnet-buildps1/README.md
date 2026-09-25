# Codefresh starter: dotnet-buildps1

App #1's pipelines (`codefresh/apps/workorders/`) as a starter, for apps built from the same
bootcamp template: `build.ps1` with `Build`, `Build -UseSqlite`, `Invoke-AcceptanceTests` and
`Package-Everything`, solution `src/ChurchBulletin.sln`, SQL Server in the build's Docker daemon.
It is an example, not a reference: after `Platform.Onboarding scaffold <app> --codefresh
dotnet-buildps1` the copy under `codefresh/apps/<app>/` belongs to the app (directive §9).

What the scaffold replaces: `<app>`, `<app-repo>` (owner/name), `<app-branch>` (default branch).
What to review after scaffolding:
- the image names in `pipelines/release.yml` and `scripts/stage-built.ps1` against
  `deployables[].images` of `apps/<app>.yaml`, and the Dockerfiles under `containers/apps/<app>/`;
- the Octopus packages of `octopus_release` against the app's Octopus process;
- `version.env` (MAJOR, MINOR);
- the optional context `app-<app>-ci` (`integrations.yaml`).

The handshake (contract §7.0) stays mandatory: images under `apps/<app>/`, a keyless signature
and an SBOM from a release pipeline named `release` or `release-<x>`, the Octopus release through
`octopus_release` with explicit packages, never a deploy, fork events off.
