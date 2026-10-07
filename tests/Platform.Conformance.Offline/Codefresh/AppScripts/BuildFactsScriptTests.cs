using System.Text.Json;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-015: step <c>image_reuse</c> of <c>workorders/release</c> writes the record of the build into the staged UI
/// image context (<c>scripts/build-facts.ps1</c>), where the deployed app answers it at <c>GET /_build</c> and the health
/// dashboard's "Code" card reads it. Every fact comes from what the build already has (the Codefresh variables, the
/// application checkout, the gates' TRX, Cobertura, CRAP and Qodana files); a fact the build does not have is null,
/// never a number that only looks measured.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class BuildFactsScriptTests
{
    private const string Script = "codefresh/apps/workorders/scripts/build-facts.ps1";
    private const string Pipeline = "codefresh/apps/workorders/pipelines/release.yml";
    private const string BuildUrl = "https://g.codefresh.example.test/build/rel-9";
    private const string RepoUrl = "https://github.com/example-org/example-app";

    /// <summary>The document the dashboard's parser reads, in this order (its README, section "Code").</summary>
    private static readonly string[] Contract =
    [
        "version", "commit", "commitUrl", "builtAt", "buildUrl", "code", "tests", "coverage", "complexity", "crap", "analysis",
    ];

    /// <summary>
    /// The usual release, which reused the CI result: identity from the Codefresh variables, the code of the checkout by
    /// language, unit and integration counts, merged coverage, complexity and CRAP from build_sql; acceptance and the
    /// Qodana count, which this build did not produce, are null.
    /// </summary>
    [Test]
    [Capability("CAP-CF-015")]
    public void Should_RunBuildFacts_ReleaseThatReusedCi_WritesWhatTheBuildMeasuredAndNullsTheRest()
    {
        using var sandbox = AppScriptSandbox.Create();
        var (repository, head) = Repository(sandbox);
        var artifacts = Path.Combine(sandbox.Root, "artifacts");
        Trx(sandbox, "artifacts/build_sql/test/UnitTests/unit.trx", total: 10, passed: 9, failed: 0);
        Trx(sandbox, "artifacts/build_sql/test/IntegrationTests/a.trx", total: 5, passed: 5, failed: 0);
        Trx(sandbox, "artifacts/build_sql/test/IntegrationTests/b.trx", total: 3, passed: 2, failed: 0);
        Cobertura(sandbox, "artifacts/build_sql/test/UnitTests/run-1/coverage.cobertura.xml", UnitCoverage);
        Cobertura(sandbox, "artifacts/build_sql/test/UnitTests/attachment/In/host/coverage.cobertura.xml", UnitCoverage);
        Cobertura(sandbox, "artifacts/build_sql/test/IntegrationTests/run-2/coverage.cobertura.xml", IntegrationCoverage);
        Cobertura(sandbox, "artifacts/build_sql/crap-metrics/coverage.flattened.cobertura.xml", "<coverage><packages><package><classes><class name=\"Flattened\" filename=\"F.cs\"><methods /><lines><line number=\"1\" hits=\"0\" /></lines></class></classes></package></packages></coverage>");
        Crap(sandbox, "artifacts/build_sql/crap-metrics", violations: 0);
        SetEnvironment(sandbox, head, artifacts);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var run = sandbox.RunIn(repository, Script, "-Out", "context/ui/built/build-facts.json");

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.Error.ShouldContain("SKIP analysis: this build has no input for it");
        run.Error.ShouldContain($"wrote context/ui/built/build-facts.json (version 2.5.9, commit {head[..7]})");
        using var document = Read(Path.Combine(repository, "context", "ui", "built", "build-facts.json"));
        var root = document.RootElement;
        root.EnumerateObject().Select(property => property.Name).ShouldBe(Contract);
        root.GetProperty("version").GetString().ShouldBe("2.5.9");
        root.GetProperty("commit").GetString().ShouldBe(head);
        root.GetProperty("commitUrl").GetString().ShouldBe($"{RepoUrl}/commit/{head}");
        root.GetProperty("buildUrl").GetString().ShouldBe(BuildUrl);
        var builtAt = root.GetProperty("builtAt").GetString()!;
        builtAt.ShouldMatch(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$");
        DateTimeOffset.Parse(builtAt, System.Globalization.CultureInfo.InvariantCulture).ShouldBeInRange(before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));

        // The checkout: 4 non-blank C# lines in 2 files, 2 SQL lines, 1 Razor line; the generated, vendored, minified and
        // Markdown files and the untracked one are not code of the repository.
        Normalize(root.GetProperty("code")).ShouldBe(Compact("""
            { "linesOfCode": 7, "files": 4, "languages": [
              { "name": "C#", "lines": 4, "files": 2 },
              { "name": "SQL", "lines": 2, "files": 1 },
              { "name": "Razor", "lines": 1, "files": 1 } ] }
            """));
        Normalize(root.GetProperty("tests")).ShouldBe(Compact("""{ "unit": 9, "integration": 7, "acceptance": null }"""));

        // Lines: A1, A2 and B1 are hit in one run or the other, A3 and C10 in none: 3 of 5. Branches of A2: the better run
        // covered 2 of 2. The flattened file of the CRAP audit and the attachment copy change nothing.
        Normalize(root.GetProperty("coverage")).ShouldBe(Compact("""{ "linePercent": 60.0, "branchPercent": 100.0 }"""));
        Normalize(root.GetProperty("complexity")).ShouldBe(Compact("""{ "average": 2.7, "max": 5, "methods": 3 }"""));
        Normalize(root.GetProperty("crap")).ShouldBe(Compact("""{ "max": 5.5, "threshold": 6, "overThreshold": 0 }"""));
        root.GetProperty("analysis").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// Every gate ran in the release: each suite is counted once (from build_sql, not again from build_sqlite), the
    /// acceptance count is there, and the Qodana count is the scan's new and unchanged results, not the absent ones.
    /// </summary>
    [Test]
    [Capability("CAP-CF-015")]
    public void Should_RunBuildFacts_EveryGateRanHere_CountsEachSuiteOnceAndTheScansProblems()
    {
        using var sandbox = AppScriptSandbox.Create();
        var (repository, head) = Repository(sandbox);
        var artifacts = Path.Combine(sandbox.Root, "artifacts");
        Trx(sandbox, "artifacts/build_sql/test/UnitTests/unit.trx", total: 4, passed: 4, failed: 0);
        Trx(sandbox, "artifacts/build_sqlite/test/UnitTests/unit.trx", total: 4, passed: 4, failed: 0);
        Trx(sandbox, "artifacts/build_sqlite/test/IntegrationTests/it.trx", total: 6, passed: 5, failed: 0);
        Trx(sandbox, "artifacts/acceptance/test/AcceptanceTests/at.trx", total: 6, passed: 3, failed: 2);
        Trx(sandbox, "artifacts/other/test/UnitTests/unit.trx", total: 99, passed: 99, failed: 0);
        Cobertura(sandbox, "artifacts/build_sqlite/test/UnitTests/run/coverage.cobertura.xml", UnitCoverage);
        Crap(sandbox, "artifacts/build_sqlite/crap-metrics", violations: 2);
        sandbox.Write("artifacts/qodana/qodana.sarif.json", """
            { "runs": [ { "results": [
              { "ruleId": "A", "baselineState": "unchanged" },
              { "ruleId": "B", "baselineState": "new" },
              { "ruleId": "C", "baselineState": "absent" },
              { "ruleId": "D" } ] } ] }
            """);
        SetEnvironment(sandbox, head, artifacts);
        var output = Path.Combine(sandbox.Root, "out", "build-facts.json");

        var run = sandbox.RunIn(repository, Script, "-Out", output, "-ArtifactsDir", artifacts);

        run.ExitCode.ShouldBe(0, run.Transcript);
        using var document = Read(output);
        var root = document.RootElement;
        Normalize(root.GetProperty("tests")).ShouldBe(Compact("""{ "unit": 4, "integration": 5, "acceptance": 5 }"""));

        // build_sql holds no coverage and no CRAP report here, so the SQLite gate's are read: A1 of 4 lines, 1 of 2 branches.
        Normalize(root.GetProperty("coverage")).ShouldBe(Compact("""{ "linePercent": 25.0, "branchPercent": 50.0 }"""));
        Normalize(root.GetProperty("complexity")).ShouldBe(Compact("""{ "average": 3.5, "max": 5, "methods": 2 }"""));
        Normalize(root.GetProperty("crap")).ShouldBe(Compact("""{ "max": 5.5, "threshold": 6, "overThreshold": 2 }"""));
        Normalize(root.GetProperty("analysis")).ShouldBe(Compact("""{ "qodanaProblems": 3 }"""));
    }

    /// <summary>
    /// Outside a build (no artifact folder, no Codefresh variables but VERSION): the commit is HEAD of the checkout, the
    /// repository the app's own, an unresolved build URL is no URL, and every measured section is null.
    /// </summary>
    [Test]
    [Capability("CAP-CF-015")]
    public void Should_RunBuildFacts_NoArtifactsAndNoCodefreshVariables_WritesTheIdentityAndTheCodeOnly()
    {
        using var sandbox = AppScriptSandbox.Create();
        var (repository, head) = Repository(sandbox);
        sandbox.Environment["VERSION"] = "2.5.9";
        sandbox.Environment["CF_BUILD_URL"] = "${{CF_BUILD_URL}}";
        var output = Path.Combine(sandbox.Root, "out", "build-facts.json");

        var run = sandbox.RunIn(sandbox.Root, Script, "-Out", output, "-Repo", repository);

        run.ExitCode.ShouldBe(0, run.Transcript);
        using var document = Read(output);
        var root = document.RootElement;
        root.EnumerateObject().Select(property => property.Name).ShouldBe(Contract);
        root.GetProperty("commit").GetString().ShouldBe(head);
        root.GetProperty("commitUrl").GetString().ShouldBe($"https://github.com/clearmeasure-aisf-sample-apps/20260923-001/commit/{head}");
        root.GetProperty("buildUrl").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("code").GetProperty("linesOfCode").GetInt32().ShouldBe(7);
        foreach (var section in new[] { "tests", "coverage", "complexity", "crap", "analysis" })
        {
            root.GetProperty(section).ValueKind.ShouldBe(JsonValueKind.Null, section);
        }
    }

    /// <summary>
    /// Inputs that cannot be read cost their own section only: a broken Cobertura file nulls coverage and complexity, a
    /// broken CRAP report nulls crap, a broken SARIF nulls analysis, an unreadable TRX nulls its suite; the record is
    /// still written with the rest, and the step succeeds.
    /// </summary>
    [Test]
    [Capability("CAP-CF-015")]
    public void Should_RunBuildFacts_UnreadableInputs_NullsOnlyTheirSections()
    {
        using var sandbox = AppScriptSandbox.Create();
        var (repository, head) = Repository(sandbox);
        var artifacts = Path.Combine(sandbox.Root, "artifacts");
        Trx(sandbox, "artifacts/build_sql/test/UnitTests/unit.trx", total: 4, passed: 4, failed: 0);
        Trx(sandbox, "artifacts/build_sql/test/IntegrationTests/a.trx", total: 5, passed: 5, failed: 0);
        sandbox.Write("artifacts/build_sql/test/IntegrationTests/broken.trx", "<TestRun");
        sandbox.Write("artifacts/build_sql/test/UnitTests/run/coverage.cobertura.xml", "<coverage><packages>");
        sandbox.Write("artifacts/build_sql/crap-metrics/crap-by-file.json", "{ \"files\": [");
        sandbox.Write("artifacts/qodana/qodana.sarif.json", "not json");
        SetEnvironment(sandbox, head, artifacts);
        var output = Path.Combine(sandbox.Root, "out", "build-facts.json");

        var run = sandbox.RunIn(repository, Script, "-Out", output);

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.Error.ShouldContain("WARN coverage reports: could not be read");
        run.Error.ShouldContain("WARN crap: could not be read");
        run.Error.ShouldContain("WARN analysis: could not be read");
        run.Error.ShouldContain("WARN tests: a TRX file of the integration tests in build_sql is unreadable; integration is null");
        using var document = Read(output);
        var root = document.RootElement;
        root.GetProperty("version").GetString().ShouldBe("2.5.9");
        root.GetProperty("commit").GetString().ShouldBe(head);
        root.GetProperty("code").GetProperty("files").GetInt32().ShouldBe(4);
        Normalize(root.GetProperty("tests")).ShouldBe(Compact("""{ "unit": 4, "integration": null, "acceptance": null }"""));
        foreach (var section in new[] { "coverage", "complexity", "crap", "analysis" })
        {
            root.GetProperty(section).ValueKind.ShouldBe(JsonValueKind.Null, section);
        }
    }

    /// <summary>
    /// Without VERSION, or when the file cannot be written, the script fails and leaves no file (not even half of one):
    /// the image is then built without a record, and the app answers its own version only.
    /// </summary>
    [Test]
    [Capability("CAP-CF-015")]
    public void Should_RunBuildFacts_NoVersionOrUnwritableOutput_FailsAndLeavesNoRecord()
    {
        using var sandbox = AppScriptSandbox.Create();
        var (repository, head) = Repository(sandbox);
        SetEnvironment(sandbox, head, Path.Combine(sandbox.Root, "artifacts"));
        var output = Path.Combine(sandbox.Root, "out", "build-facts.json");
        var blocked = sandbox.Write("blocked", "a file where the folder should be");

        sandbox.Environment["VERSION"] = "${{VERSION}}";
        var noVersion = sandbox.RunIn(repository, Script, "-Out", output);
        sandbox.Environment["VERSION"] = "2.5.9";
        var unwritable = sandbox.RunIn(repository, Script, "-Out", Path.Combine(blocked, "built", "build-facts.json"));

        noVersion.ExitCode.ShouldBe(1, noVersion.Transcript);
        noVersion.Error.ShouldContain("VERSION is not set");
        File.Exists(output).ShouldBeFalse();
        unwritable.ExitCode.ShouldBe(1, unwritable.Transcript);
        unwritable.Error.ShouldContain("cannot write");
        Directory.EnumerateFiles(sandbox.Root, "build-facts.json*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    /// <summary>
    /// The pipeline: image_reuse, which runs after the gate passed and the contexts are staged and before the image
    /// builds, first writes the record into built/ of the UI context that ui_image builds; a failure of the script never
    /// fails the step, and stage-built.ps1 creates that built/ folder anew in every build (no record of an older build
    /// stays behind).
    /// </summary>
    [Test]
    [Capability("CAP-CF-015")]
    public void Should_ReadWorkordersRelease_ImageReuse_WritesTheRecordIntoTheUiContextBeforeTheImageBuild()
    {
        var steps = CodefreshRepository.Steps(CodefreshRepository.Load(Pipeline)).ToDictionary(step => step.Path, StringComparer.Ordinal);
        var imageReuse = steps["steps.image_reuse"];
        var commands = imageReuse.Commands.ToArray();
        var waitsFor = CodefreshRepository.Items(CodefreshRepository.Get(imageReuse.Body.GetValueOrDefault("when"), "steps"))
            .Select(entry => CodefreshRepository.Get(entry, "name") as string)
            .Order(StringComparer.Ordinal);
        var stage = CodefreshRepository.Read("codefresh/apps/workorders/scripts/stage-built.ps1");

        commands[0].ShouldStartWith($"pwsh -NoProfile -File \"${{{{CF_VOLUME_PATH}}}}/platform/basic-environment-octopus-codefresh/{Script}\" ");
        commands[0].ShouldContain("-Out \"${{CF_VOLUME_PATH}}/image-contexts/ui/built/build-facts.json\" || echo ");
        commands[1].ShouldContain("/scripts/supply-chain-step.ps1\" -Step image_reuse");
        waitsFor.ShouldBe(["gate", "stage_images"]);
        steps["steps.ui_image"].Body.GetValueOrDefault("working_directory").ShouldBe("${{CF_VOLUME_PATH}}/image-contexts/ui");
        steps["steps.stage_images"].Commands.Last().ShouldContain("-Out \"${CF_VOLUME_PATH}/image-contexts\"");
        stage.ShouldContain("$context = New-ContextFolder 'ui'");
        stage.ShouldContain("$built = Join-Path $context 'built'");
        File.Exists(Path.Combine(CodefreshRepository.Root, Script)).ShouldBeTrue();
    }

    private const string UnitCoverage = """
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE coverage SYSTEM "http://cobertura.example.test/coverage-04.dtd">
        <coverage line-rate="0.25" branch-rate="0.5" version="1.9">
          <packages>
            <package name="App">
              <classes>
                <class name="App.A" filename="Core\A.cs">
                  <methods>
                    <method name="Run" signature="()" complexity="2"><lines /></method>
                  </methods>
                  <lines>
                    <line number="1" hits="1" branch="False" />
                    <line number="2" hits="0" branch="True" condition-coverage="50% (1/2)" />
                    <line number="3" hits="0" branch="False" />
                  </lines>
                </class>
                <class name="App.B" filename="Core/B.cs">
                  <methods>
                    <method name="Run" signature="(System.Int32)" complexity="5"><lines /></method>
                  </methods>
                  <lines>
                    <line number="1" hits="0" branch="False" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    private const string IntegrationCoverage = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" branch-rate="1" version="1.9">
          <packages>
            <package name="App">
              <classes>
                <class name="App.A" filename="Core/A.cs">
                  <methods>
                    <method name="Run" signature="()" complexity="2"><lines /></method>
                  </methods>
                  <lines>
                    <line number="1" hits="0" branch="False" />
                    <line number="2" hits="3" branch="True" condition-coverage="100% (2/2)" />
                    <line number="3" hits="0" branch="False" />
                  </lines>
                </class>
                <class name="App.B" filename="Core/B.cs">
                  <methods>
                    <method name="Run" signature="(System.Int32)" complexity="5"><lines /></method>
                  </methods>
                  <lines>
                    <line number="1" hits="2" branch="False" />
                  </lines>
                </class>
                <class name="App.C" filename="Core/C.cs">
                  <methods>
                    <method name="Run" signature="()" complexity="1"><lines /></method>
                  </methods>
                  <lines>
                    <line number="10" hits="0" branch="False" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    /// <summary>
    /// An application checkout with code in three languages (blank lines between), and files that are not its code:
    /// generated, vendored, minified, Markdown, and one that is not tracked.
    /// </summary>
    private static (string Repository, string Head) Repository(AppScriptSandbox sandbox)
    {
        var repository = Path.Combine(sandbox.Root, "app");
        sandbox.Git(sandbox.Root, "init", "-q", "-b", "master", repository);
        sandbox.Write(Path.Combine(repository, "src", "Core", "A.cs"), "namespace App;\n\npublic class A;\n   \n");
        sandbox.Write(Path.Combine(repository, "src", "Core", "B.CS"), "namespace App;\r\n\r\npublic class B;\r\n");
        sandbox.Write(Path.Combine(repository, "src", "Database", "scripts", "001.sql"), "CREATE TABLE T (\n\tId int)\n");
        sandbox.Write(Path.Combine(repository, "src", "UI", "Page.razor"), "<h1>Page</h1>\n");
        sandbox.Write(Path.Combine(repository, "src", "UI", "Generated", "Client.cs"), "class Generated;\n");
        sandbox.Write(Path.Combine(repository, "src", "UI", "obj", "Page.g.cs"), "class Build;\n");
        sandbox.Write(Path.Combine(repository, "src", "UI", "wwwroot", "lib", "vendor.js"), "var vendored = 1;\n");
        sandbox.Write(Path.Combine(repository, "src", "UI", "wwwroot", "site.min.css"), "body{margin:0}\n");
        sandbox.Write(Path.Combine(repository, "README.md"), "# App\n\nWords, not code.\n");
        sandbox.Write(Path.Combine(repository, "qodana.sarif.json"), "{ \"runs\": [ { \"results\": [ { \"ruleId\": \"Baseline\" } ] } ] }\n");
        var head = sandbox.Commit(repository, "the release commit");
        sandbox.Write(Path.Combine(repository, "src", "Core", "Untracked.cs"), "class Untracked;\n");
        return (repository, head);
    }

    private static void SetEnvironment(AppScriptSandbox sandbox, string head, string artifacts)
    {
        sandbox.Environment["VERSION"] = "2.5.9";
        sandbox.Environment["CF_REVISION"] = head;
        sandbox.Environment["CF_BUILD_URL"] = BuildUrl;
        sandbox.Environment["CF_BUILD_ID"] = "rel-9";
        sandbox.Environment["CF_REPO_OWNER"] = "example-org";
        sandbox.Environment["CF_REPO_NAME"] = "example-app";
        sandbox.Environment["ARTIFACTS_DIR"] = artifacts;
    }

    /// <summary>A TRX file as the VSTest logger writes it: a skipped test is in total, not in executed, and notExecuted stays 0.</summary>
    private static void Trx(AppScriptSandbox sandbox, string path, int total, int passed, int failed) =>
        sandbox.Write(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="1" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results />
              <ResultSummary outcome="Completed">
                <Counters total="{total}" executed="{passed + failed}" passed="{passed}" failed="{failed}" error="0" timeout="0" aborted="0" notExecuted="0" />
              </ResultSummary>
            </TestRun>
            """);

    private static void Cobertura(AppScriptSandbox sandbox, string path, string content) => sandbox.Write(path, content);

    /// <summary>The two reports of the application's CRAP audit: scores per file, and the production methods over the gate.</summary>
    private static void Crap(AppScriptSandbox sandbox, string folder, int violations)
    {
        sandbox.Write($"{folder}/crap-by-file.json", """
            { "schemaVersion": "1.0", "threshold": 6, "fileCount": 2, "productionFileCount": 1, "files": [
              { "FilePath": "/app/src/UnitTests/Stub.cs", "MethodCount": 11, "CrappyMethodCount": 2, "MaxCrap": 462, "IsProduction": false },
              { "FilePath": "/app/src/Core/A.cs", "MethodCount": 3, "CrappyMethodCount": 0, "MaxCrap": 5.52, "IsProduction": true } ] }
            """);
        sandbox.Write($"{folder}/crap-production-violations.json", $$"""
            { "schemaVersion": "1.0", "threshold": 6, "violationCount": {{violations}}, "methods": [] }
            """);
    }

    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));

    /// <summary>The expected JSON of a section, in the form <see cref="Normalize"/> gives the section of the record.</summary>
    private static string Compact(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Normalize(document.RootElement);
    }

    /// <summary>JSON without insignificant white space, numbers as written (<c>60.0</c> stays <c>60.0</c>).</summary>
    private static string Normalize(JsonElement element) => JsonSerializer.Serialize(element);
}
