# Toggles

Marker files that the conformance suite commits to switch a fixture failure on. Both are absent
on `main` of `<sandbox-app-repo>`; `platform-env/conformance-arm` adds them on throwaway commits.

| Marker | Effect | Capability |
|---|---|---|
| `toggles/failing-test` | `FailingTestToggleTests` fails, so `sandbox/ci` fails its gate | CAP-CF-004 |
| `toggles/failing-migration` | `sandbox/release` copies `db/toggles/9000_failing_migration.sql` into the migrator image, so the PreSync Job fails | CAP-GIT-010 |
