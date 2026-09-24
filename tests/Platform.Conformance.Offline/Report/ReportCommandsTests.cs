using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Support;
using Platform.Conformance.Report;
using static Platform.Conformance.Offline.Support.Catalogues;

namespace Platform.Conformance.Offline.Report;

/// <summary>Proves the report tool end to end: files written, exit codes, map and render commands.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class ReportCommandsTests
{
    private string folder = null!;
    private StringWriter output = null!;
    private StringWriter error = null!;

    [SetUp]
    public void CreateWorkingFolder()
    {
        folder = Path.Combine(Path.GetTempPath(), "platform-conformance-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        output = new StringWriter();
        error = new StringWriter();
        File.WriteAllText(Path.Combine(folder, "catalogue.yaml"), "capabilities:" + Environment.NewLine + string.Join(Environment.NewLine,
            Entry("CAP-A-001", "Ns.Suite.WhenA_Passes_One, Ns.Suite.WhenA_Passes_Two"),
            Entry("CAP-B-001", "Ns.Live.WhenB_Fails_Always", live: true, owner: "octopus"),
            Entry("CAP-C-001", "Ns.Live.WhenC_Lacks_Secret", live: true, owner: "codefresh", tier: "build"),
            Entry("CAP-D-001", "Ns.Live.WhenD_Filtered_Out", live: true, owner: "azure", tier: "nonprod"),
            Entry("CAP-E-001", "Ns.Suite.WhenE_Case_Varies, Ns.Suite.WhenE_Other_Missing")));
        File.WriteAllText(Path.Combine(folder, "capability-map.json"), CapabilityMap.FromTests(ReportSamples.Tests).ToJson());
    }

    [TearDown]
    public void DeleteWorkingFolder()
    {
        output.Dispose();
        error.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenRun_ReportWithAFailedTest_WritesSummaryFilesAndExitsOne()
    {
        File.WriteAllText(Path.Combine(folder, "results", "live.trx").CreateParent(), ReportSamples.Trx());

        var exitCode = Run("report", "--trx", "results", "--map", "capability-map.json", "--catalogue", "catalogue.yaml", "--out", "report");

        exitCode.ShouldBe(ReportCommands.TestsFailed);
        File.ReadAllText(Path.Combine(folder, "report", "summary.md")).ShouldContain("**FAILED**");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "report", "summary.json")));
        json.RootElement.GetProperty("result").GetString().ShouldBe("failed");
        output.ToString().ShouldStartWith("FAILED: 7 results; 1 failed, 2 inconclusive;");
    }

    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenRun_ReportWithoutFailures_WritesNextToTheTrxAndExitsZero()
    {
        var trx = TrxSamples.Create(ReportSamples.Start, TimeSpan.FromSeconds(3), new TrxSample("Ns.Suite", "WhenA_Passes_One", "Passed"), new TrxSample("Ns.Suite", "WhenA_Passes_Two", "Passed"));
        File.WriteAllText(Path.Combine(folder, "offline.trx"), trx);

        var exitCode = Run("report", "--trx", "offline.trx", "--map", "capability-map.json", "--catalogue", "catalogue.yaml");

        exitCode.ShouldBe(ReportCommands.Success);
        File.ReadAllText(Path.Combine(folder, "summary.md")).ShouldContain("**PASSED**: no failed tests");
    }

    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenRun_ReportWithMissingTrx_ExitsTwoNamingTheFile()
    {
        var exitCode = Run("report", "--trx", "missing.trx", "--map", "capability-map.json", "--catalogue", "catalogue.yaml");

        exitCode.ShouldBe(ReportCommands.InvalidInput);
        error.ToString().ShouldContain("missing.trx does not exist");
    }

    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenRun_UnknownOption_PrintsUsageAndExitsTwo()
    {
        var exitCode = Run("report", "--junit", "results.xml");

        exitCode.ShouldBe(ReportCommands.InvalidInput);
        error.ToString().ShouldContain("'report' has no option --junit");
        error.ToString().ShouldContain("Usage: Platform.Conformance.Report");
    }

    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenRun_MapOnTheLiveTestAssembly_WritesItsTestsWithCapabilities()
    {
        var assembly = Path.Combine(TestContext.CurrentContext.TestDirectory, "Platform.Conformance.Tests.dll");

        var exitCode = Run("map", "--assembly", assembly, "--out", "map.json");

        exitCode.ShouldBe(ReportCommands.Success);
        var map = CapabilityMap.FromJson(File.ReadAllText(Path.Combine(folder, "map.json")), "map.json");
        map.Assemblies.ShouldBe(["Platform.Conformance.Tests"]);
        map.Tests.ShouldContain(test => test.Name == "Platform.Conformance.Tests.Smoke.PlatformReachabilityTests.WhenGetSpaceAsync_WithConfiguredSpace_ReturnsThatSpace" && test.Capabilities.SequenceEqual(new[] { "CAP-HARNESS-002" }));
    }

    [Test]
    [Capability("CAP-HARNESS-012")]
    public void WhenRun_RenderCatalogueWithOut_WritesTheMarkdownFile()
    {
        var exitCode = Run("render-catalogue", "--catalogue", "catalogue.yaml", "--out", "docs/capabilities.md");

        exitCode.ShouldBe(ReportCommands.Success);
        File.ReadAllText(Path.Combine(folder, "docs", "capabilities.md")).ShouldContain("## octopus");
    }

    [Test]
    [Capability("CAP-HARNESS-012")]
    public void WhenRun_RenderCatalogueFromRepositoryRoot_WritesDocsCapabilitiesUnderThatRoot()
    {
        var fragments = Path.Combine(folder, "repo", "catalogue", "capabilities.d");
        Directory.CreateDirectory(fragments);
        File.Copy(Path.Combine(folder, "catalogue.yaml"), Path.Combine(fragments, "sample.yaml"));

        var exitCode = Run("render-catalogue", "--repo-root", "repo");

        exitCode.ShouldBe(ReportCommands.Success);
        File.ReadAllText(Path.Combine(folder, "repo", "docs", "capabilities.md")).ShouldContain("| `catalogue/capabilities.d/sample.yaml` | 5 |");
    }

    private int Run(params string[] args) =>
        ReportCommands.Run(args, output, error, new StubEnvironmentVariables(), folder, new StubClock(ReportSamples.Start));
}

/// <summary>Path helpers for the command tests.</summary>
internal static class PathExtensions
{
    public static string CreateParent(this string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
