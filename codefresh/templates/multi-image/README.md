# Codefresh starter: multi-image

N images, one build step each (the example shows `web` and `migrator`, matching the
`deploy-with-db` Octopus starter), each from `containers/apps/<app>/<image>/Dockerfile` of the
environment repo over the app checkout, and one Octopus release that lists them all. The app
repository stays untouched (ADR-D18). After `Platform.Onboarding scaffold <app> --codefresh
multi-image`, the copy under `codefresh/apps/<app>/` belongs to the app (directive §9).

Add or remove images in three places: `stage_dockerfiles`, the `<image>_image` build steps
(and `supply_chain`'s `when` and repository list), and the `--package-id` and `--package`
arguments of the handoff. Write each Dockerfile under `containers/apps/<app>/<image>/`.
The sandbox fixture (`codefresh/apps/sandbox/`) is this starter with .NET build, test and
staging steps filled in.
