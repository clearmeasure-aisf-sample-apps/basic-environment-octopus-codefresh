# Platform conformance harness

A .NET-native test harness that proves every platform capability with automated tests. Each capability is an entry in a machine-readable catalogue; each test declares the capability it proves with `[Capability("CAP-...")]`; `CatalogueConsistencyTests` fails the build when the two drift apart. Tests are NUnit 4 with Shouldly on .NET 10, results are TRX, and the report tool turns TRX into a Markdown and JSON summary. There is no JUnit output anywhere.

## Layout

```text
catalogue/                                  (repository root)
├── capabilities.yaml                       optional main file
└── capabilities.d/*.yaml                   one fragment per owning role; harness.yaml belongs to the harness
tests/
├── Platform.Conformance.sln
├── global.json  Directory.Build.props  Directory.Packages.props  .gitignore
├── platform.settings.json                  non-secret settings; placeholders until provisioning fills them in
├── Platform.Conformance.Harness/           shared framework (class library)
│   ├── CapabilityAttribute.cs  Categories.cs  PlatformTestBase.cs  PlatformPrerequisiteException.cs
│   ├── Catalogue/                          catalogue model, loader, test discovery, consistency rules
│   ├── Settings/                           settings file, secrets from the environment, prerequisite checks
│   ├── Support/                            Poll, IClock, TestRunContext, ICleanupRegistry, RepositoryRoot
│   └── Clients/                            IOctopusApi, ICodefreshApi, IAzureApi, IKubernetesApi, IGitHubApi
├── Platform.Conformance.Offline/           offline tests: catalogue consistency and unit tests of the harness
├── Platform.Conformance.Tests/             live tests against the real platform
└── Platform.Conformance.Report/            console tool: map, report, render-catalogue
```

## Prerequisites

The .NET SDK 10 (`tests/global.json` pins 10.0.100 with `rollForward: latestFeature`). Nothing else is needed for the offline tests. Package versions are managed centrally in `tests/Directory.Packages.props`; every project builds with warnings as errors.

```bash
dotnet build tests/Platform.Conformance.sln -c Release -warnaserror
```

## Running the offline tests

Offline tests need no network and no secret. Every one of them carries the category `Offline`.

```bash
dotnet test tests/Platform.Conformance.sln --filter "TestCategory=Offline" \
  --logger "trx;LogFileName=offline.trx" --results-directory tests/TestResults
```

At solution level every test project writes the TRX file, so the live-test project also writes an (empty) `offline.trx` and the last writer wins. Offline tests live only in `Platform.Conformance.Offline`, which normally finishes last. To keep one file per project, use `--logger "trx;LogFilePrefix=offline"` and pass the folder to the report tool, or run the project on its own:

```bash
dotnet test tests/Platform.Conformance.Offline --logger "trx;LogFileName=offline.trx" --results-directory tests/TestResults
```

## Running the live tests

Live tests carry the category `Live` and derive from `PlatformTestBase`. They read settings from `tests/platform.settings.json` and secrets from the environment only:

| Variable | Needed by | Notes |
|---|---|---|
| `OCTOPUS_API_KEY` | `IOctopusApi` | Sent only as the `X-Octopus-ApiKey` header. Also needs `OctopusUrl` and `OctopusSpaceId`. |
| `CODEFRESH_API_KEY` | `ICodefreshApi` | Sent only as the `Authorization` header. |
| `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `AZURE_TENANT_ID` | `IAzureApi`, `IKubernetesApi` | A service principal. Without all three the harness falls back to `DefaultAzureCredential` (workload identity, managed identity, Azure CLI); when that finds nothing the test is Inconclusive. |
| `AZURE_SUBSCRIPTION_ID` | `IAzureApi`, `IKubernetesApi` | Overrides `AzureSubscriptionId` of the settings file. `AZURE_TENANT_ID` likewise overrides `AzureTenantId`. |
| `GITHUB_TOKEN` | `IGitHubApi` | Sent only as a bearer `Authorization` header. |
| `PLATFORM_TLS_SYSTEM_TRUST` | `IKubernetesApi` | `true` behind a TLS-re-terminating proxy whose CA is in the system trust store; see [TLS and cluster authentication](#tls-and-cluster-authentication). |
| `PLATFORM_SETTINGS_FILE` | all live tests | Another settings file instead of `tests/platform.settings.json`. |
| `PLATFORM_RUN_ID`, `PLATFORM_ARTIFACTS_DIR` | all live tests | Run identifier used in resource names, and the folder for artifacts such as task logs (default `tests/TestResults/artifacts/<run id>`). |

```bash
export OCTOPUS_API_KEY=... CODEFRESH_API_KEY=... AZURE_CLIENT_ID=... AZURE_CLIENT_SECRET=... AZURE_TENANT_ID=... GITHUB_TOKEN=...
dotnet test tests/Platform.Conformance.Tests --filter "TestCategory=Live&TestCategory!=Destructive" \
  --logger "trx;LogFileName=live.trx" --results-directory tests/TestResults
```

A live test whose secret, setting or credential is missing is **Inconclusive**: it neither passes nor fails, and its message names every missing item. The console shows it as "Skipped" and the TRX file as `NotExecuted`; the report counts it as inconclusive. Without any secret the three smoke tests (`CAP-HARNESS-002` to `004`) show exactly that.

Select a tier with its category, for example `TestCategory=Live&TestCategory=NonProd`. Destructive tests run only when asked for: `TestCategory=Destructive&TestCategory=NonProd`.

## Settings and secrets

`tests/platform.settings.json` holds only non-secret values: the Octopus URL and space ID, the Codefresh API base, the Azure subscription and tenant IDs, the registry login server, the GitHub organization and repositories, the resource group and cluster of each tier (`build`, `nonprod`, `prod`) and the time limits of waits. Values in angle brackets, such as `<OCTOPUS_URL>`, are placeholders that the provisioning step fills in; a placeholder counts as missing, so the tests that need it stay Inconclusive until then.

Secrets never go in the file. The loader rejects any property it does not know, and names secret-like properties (`...ApiKey`, `...Secret`, `...Token`, `...Password`) with the variable to use instead. A malformed settings file fails the fixture; a missing file only makes live tests Inconclusive. `PlatformSecrets.ToString()` never prints a value.

## Adding a capability and its test

1. Pick the fragment of the owning role in `catalogue/capabilities.d/` (for example `octopus.yaml`), or create it with the header of `harness.yaml`. Fragments are merged in file-name order; never define an ID that another file defines.
2. Add the entry:

   ```yaml
   capabilities:
     - id: CAP-SLEEP-001
       statement: "An idle nonprod cluster is stopped outside the working window"
       owner: octopus
       adr: "ADR-IR33"
       observed_by: "ARM power state of aks-platform-nonprod after runbook env-sleep"
       tests:
         - Platform.Conformance.Tests.Sleep.SleepTests.WhenEnvSleep_IdleOutsideWindow_StopsTheCluster
       live: true
       destructive: true
       tier: nonprod
   ```

3. Write the test in `Platform.Conformance.Tests` (live) or `Platform.Conformance.Offline` (offline). Name it `When<Method>_<Scenario>_<Result>` or `Should...`, use Shouldly, and follow Arrange-Act-Assert without section comments:

   ```csharp
   [TestFixture]
   [Category(Categories.Live)]
   public class SleepTests : PlatformTestBase
   {
       [Test]
       [Capability("CAP-SLEEP-001")]
       [Category(Categories.NonProd)]
       [Category(Categories.Destructive)]
       [Category(Categories.Slow)]
       public async Task WhenEnvSleep_IdleOutsideWindow_StopsTheCluster()
       {
           var tier = Settings.Tier(PlatformTier.NonProd);
           Settings.Check("the sleep test").Setting("Tiers.nonprod.ClusterName", tier.ClusterName).ThrowIfMissing();
           Cleanup.Register("wake aks-platform-nonprod", token => Octopus.RunRunbookAsync(
               new OctopusRunbookRunRequest { Project = "platform-infrastructure", Runbook = "env-wake", Environment = "infra-nonprod" },
               Settings.TimeLimits.WakeTimeout, token));

           var run = await Octopus.RunRunbookAsync(
               new OctopusRunbookRunRequest
               {
                   Project = "platform-infrastructure",
                   Runbook = "env-sleep",
                   Environment = "infra-nonprod",
                   PromptedVariables = new Dictionary<string, string> { ["Sleep.Force"] = "True" },
                   Comments = $"Conformance run {Run.RunId}",
               },
               Settings.TimeLimits.RunbookTimeout);

           run.Task.FinishedSuccessfully.ShouldBeTrue(run.Task.ErrorMessage);
           var state = await Azure.GetClusterStateAsync(tier.ResourceGroup!, tier.ClusterName!);
           state.PowerState.ShouldBe("Stopped");
       }
   }
   ```

4. Run the offline tests. `CatalogueConsistencyTests` names anything that does not match: a capability without a test, a test without a capability, a stale or missing `tests` entry, or a category that contradicts the flags.
5. Re-render `docs/capabilities.md` (see [Rendering the catalogue](#rendering-the-catalogue)).

## Catalogue schema and rules

Each catalogue file is a mapping with one key, `capabilities`, holding a list of entries:

| Key | Required | Meaning |
|---|---|---|
| `id` | yes | `CAP-<AREA>-<NNN>` (uppercase, ending in three digits), unique across all files |
| `statement` | yes | One sentence: what the platform guarantees |
| `owner` | yes | `codefresh`, `octopus`, `argocd`, `azure`, `kyverno`, `onboarding` or `platform` |
| `adr` | yes | Design reference: an ADR, a design section or a document path |
| `observed_by` | yes | How the capability is observed: the API, object or signal the tests read |
| `tests` | yes | Fully qualified test names, `Namespace.Class.Method` without arguments (nested classes use `+`) |
| `live` | yes | `true` when a test needs the live platform; `false` when every test is offline |
| `destructive` | yes | `true` when proving it changes platform state |
| `tier` | yes | `build`, `nonprod`, `prod` or `all` |
| `why_offline` | when `live: false` | Why the capability is proven offline; not allowed when `live: true` |

`CapabilityCatalogue.LoadDirectory(repoRoot)` merges `catalogue/capabilities.yaml` (optional) with `catalogue/capabilities.d/*.yaml` in file-name order. Loading reports every problem at once with its file and line: unknown keys, missing keys, invalid values, a destructive capability with tier `prod`, a `.yml` fragment (not loaded until renamed) and an ID defined twice, naming both places. `PLATFORM_CATALOGUE_FILE` checks a single file instead, and `PLATFORM_REPO_ROOT` overrides the repository root that is otherwise found from the test assembly location.

`CatalogueConsistencyTests` reflects over both test assemblies (`[Test]`, `[TestCase]`, `[TestCaseSource]` and any other NUnit test builder) and asserts:

- every capability has at least one test carrying its ID;
- every test carries at least one `[Capability]`, and every ID it carries is in the catalogue;
- each `tests` list matches the attributes exactly: no stale entry, no missing entry;
- every capability with `live: false` has `why_offline`;
- every destructive test is categorised `Destructive` and `NonProd` and never `Prod`;
- every test is categorised exactly one of `Live` and `Offline`; a `live: false` capability has no live test and a `live: true` capability has at least one; a live test of a `build`, `nonprod` or `prod` capability carries the category `Build`, `NonProd` or `Prod`.

## Categories and the destructive rule

| Category | Meaning |
|---|---|
| `Live` | Talks to a real platform system. Exactly one of `Live` and `Offline` per test. |
| `Offline` | Needs no network and no secret. |
| `Destructive` | Changes or disrupts platform state (stops a cluster, deletes a resource). |
| `Slow` | Takes minutes (runbooks, deployments, wakes). |
| `NonProd`, `Prod`, `Build` | The tier a live test targets. |

A test is destructive when it carries `Destructive` or proves a capability with `destructive: true`. Such a test must carry `Destructive` and `NonProd` and must never carry `Prod`, and a destructive capability cannot have tier `prod`: destructive tests never run against production. Register a cleanup for anything a test creates or changes.

## Harness building blocks

- `PlatformTestBase` loads the settings in `[OneTimeSetUp]`, exposes `Octopus`, `Codefresh`, `Azure`, `GitHub` and `KubernetesAsync(tier)`, and runs `Cleanup` actions in reverse order in `[OneTimeTearDown]`, even when a test failed; failed cleanups are reported together.
- `IOctopusApi` covers tasks by project, environment and state; runbook runs, including config-as-code runbooks at a Git reference (`/api/{space}/projects/{id}/{gitRef}/runbooks/{runbookId}/run/v1`) with prompted variables mapped by name; releases and deployments through the executions API; task state and raw logs; manual interventions (take responsibility, then submit `Result=Proceed` with notes); variable sets; environments by name.
- `ICodefreshApi` runs pipelines, reads and waits for builds (terminal: success, error, terminated, denied), terminates them through their progress ID, lists builds of a pipeline, runtime environments and agents.
- `IAzureApi` reads the subscription, AKS power and provisioning state and node pools, resource groups and tags, managed disks, alert processing rules (generic ARM reads) and registry repository and tag attributes (ACR data plane after an Entra token exchange).
- `IKubernetesApi` reads namespaces, pods, Deployments, StatefulSets, PVCs, resource quotas, network policies, Argo CD Applications, Kyverno policies and ExternalSecrets, and creates or deletes pods (with server-side dry run) for policy and network tests.
- `IGitHubApi` reads branch heads, commits, files and comparisons, and creates branches, commits and pull requests for the end-to-end test.
- `Poll.UntilAsync(condition, timeout, interval, description)` waits with a deadline; its failure names what was awaited, the attempts and the last value or error. It takes an `IClock` so unit tests never wait.
- `TestRunContext.Current` gives the run ID, start time and artifacts folder; `ResourceName("purpose")` names resources after the run.
- Every client sits behind an interface, so unit tests use `Stub` doubles; `PlatformPrerequisiteException` makes any missing prerequisite Inconclusive wherever it is thrown.

## TLS and cluster authentication

The clusters use Entra ID with Azure RBAC and have local accounts disabled. `IKubernetesApi` reads the cluster's user kubeconfig through ARM only for the API server address and CA, and authenticates every request with an Entra token for the AKS server application (`6dae42f8-4368-4678-94ff-3960e28e3630/.default`). It never uses client certificates or the kubeconfig's user.

By default the cluster CA is pinned. With `PLATFORM_TLS_SYSTEM_TRUST=true` (for example behind a TLS-re-terminating proxy whose CA is in the system trust store) the server certificate is validated against the system trust store instead. TLS verification is never disabled in any mode. The REST clients always use the system trust store and honour `HTTPS_PROXY`.

## Producing the report

`Platform.Conformance.Report` reads TRX files with `System.Xml.Linq`, maps each result to its capabilities (by reflecting over the test assemblies, or from a capability map), and writes `summary.md` and `summary.json`:

```bash
dotnet run --project tests/Platform.Conformance.Report -c Release -- report \
  --trx tests/TestResults/offline.trx --trx tests/TestResults/live.trx \
  --assembly tests/Platform.Conformance.Offline/bin/Release/net10.0/Platform.Conformance.Offline.dll \
  --assembly tests/Platform.Conformance.Tests/bin/Release/net10.0/Platform.Conformance.Tests.dll \
  --out tests/TestResults/report
```

- `--trx` takes files or folders (searched for `*.trx`) and may repeat.
- Instead of `--assembly`, pass `--map` with the JSON written by `map`: `... -- map --assembly <dll> --assembly <dll> --out capability-map.json`.
- The catalogue is the merged `catalogue/` of the repository found from the current folder; `--repo-root` or `--catalogue <file>` override it.
- The summary holds the totals by outcome, the status of every capability grouped by owner, the failures with truncated messages, the inconclusive tests with their reasons, the slowest tests and any result that maps to no capability. A capability **passes** only when all its tests passed, **fails** when any failed, is **not run** when none ran, and is **inconclusive** otherwise.
- Exit code 0 when no test failed, 1 when any test failed, 2 for invalid input.

## Rendering the catalogue

```bash
dotnet run --project tests/Platform.Conformance.Report -c Release -- render-catalogue
```

Renders the merged catalogue into `docs/capabilities.md`: a summary table and one table per owner. The output is deterministic; edit the catalogue, not the generated file. `--out <file>` writes elsewhere.

## Known limits

- `GET /api/user` (Codefresh smoke test) is not listed in Codefresh's published OpenAPI document; verify it on the first live run.
- The Octopus task `states` filter is sent comma-separated, as Octopus's own API client does; the Swagger document declares repeated parameters.
- Kyverno readiness is read from `status.conditionStatus.ready`, `status.ready` or a `Ready` condition, whichever the installed version reports.
- Generic test fixtures (open generic classes) are not reflected by the consistency check.
