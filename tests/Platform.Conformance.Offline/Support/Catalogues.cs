using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Catalogue;

namespace Platform.Conformance.Offline.Support;

/// <summary>Small in-memory catalogues and discovered tests for unit tests.</summary>
internal static class Catalogues
{
    public const string LiveTest = "Sample.Live.ClusterTests.WhenReadingState_ClusterRuns_ReportsRunning";
    public const string OfflineTest = "Sample.Offline.ConfigTests.WhenParsing_ValidConfig_Succeeds";

    public static string Entry(
        string id,
        string tests,
        bool live = false,
        bool destructive = false,
        string tier = "all",
        string owner = "platform",
        string? whyOffline = "static by nature") =>
        $"""
          - id: {id}
            statement: Capability {id} holds
            owner: {owner}
            adr: design/platform-design.md
            observed_by: a unit test
            tests:
        {string.Join(Environment.NewLine, tests.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(test => $"      - {test}"))}
            live: {(live ? "true" : "false")}
            destructive: {(destructive ? "true" : "false")}
            tier: {tier}
        {(whyOffline is null || live ? "" : $"    why_offline: {whyOffline}")}
        """;

    public static CapabilityCatalogue Parse(params string[] entries) =>
        CapabilityCatalogue.Parse("capabilities:" + Environment.NewLine + string.Join(Environment.NewLine, entries), "test-catalogue.yaml");

    public static DiscoveredTest Test(string name, string[] capabilities, params string[] categories) =>
        new(name, "Sample", capabilities, categories);

    public static DiscoveredTest OfflineTestOf(string name, params string[] capabilities) => Test(name, capabilities, Categories.Offline);

    public static DiscoveredTest LiveTestOf(string name, string[] capabilities, params string[] extraCategories) =>
        Test(name, capabilities, [Categories.Live, .. extraCategories]);
}
