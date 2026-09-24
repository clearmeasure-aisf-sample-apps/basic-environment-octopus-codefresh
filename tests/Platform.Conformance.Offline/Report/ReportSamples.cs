using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Catalogue;
using Platform.Conformance.Offline.Support;
using Platform.Conformance.Report;
using static Platform.Conformance.Offline.Support.Catalogues;

namespace Platform.Conformance.Offline.Report;

/// <summary>A catalogue, its tests and a TRX run with one capability in each status.</summary>
internal static class ReportSamples
{
    public static readonly DateTimeOffset Start = new(2026, 9, 24, 6, 0, 0, TimeSpan.Zero);

    public static CapabilityCatalogue Catalogue { get; } = Parse(
        Entry("CAP-A-001", "Ns.Suite.WhenA_Passes_One, Ns.Suite.WhenA_Passes_Two"),
        Entry("CAP-B-001", "Ns.Live.WhenB_Fails_Always", live: true, owner: "octopus"),
        Entry("CAP-C-001", "Ns.Live.WhenC_Lacks_Secret", live: true, owner: "codefresh", tier: "build"),
        Entry("CAP-D-001", "Ns.Live.WhenD_Filtered_Out", live: true, owner: "azure", tier: "nonprod"),
        Entry("CAP-E-001", "Ns.Suite.WhenE_Case_Varies, Ns.Suite.WhenE_Other_Missing"));

    public static IReadOnlyList<DiscoveredTest> Tests { get; } =
    [
        OfflineTestOf("Ns.Suite.WhenA_Passes_One", "CAP-A-001"),
        OfflineTestOf("Ns.Suite.WhenA_Passes_Two", "CAP-A-001"),
        LiveTestOf("Ns.Live.WhenB_Fails_Always", ["CAP-B-001"]),
        LiveTestOf("Ns.Live.WhenC_Lacks_Secret", ["CAP-C-001"], Categories.Build),
        LiveTestOf("Ns.Live.WhenD_Filtered_Out", ["CAP-D-001"], Categories.NonProd),
        OfflineTestOf("Ns.Suite.WhenE_Case_Varies", "CAP-E-001"),
        OfflineTestOf("Ns.Suite.WhenE_Other_Missing", "CAP-E-001"),
    ];

    public static string Trx(string failureMessage = "Shouldly.ShouldAssertException: state\n    should be\n\"Running\"\n    but was\n\"Stopped\"") => TrxSamples.Create(
        Start,
        TimeSpan.FromSeconds(65),
        new TrxSample("Ns.Suite", "WhenA_Passes_One", "Passed", 0.25),
        new TrxSample("Ns.Suite", "WhenA_Passes_Two", "Passed", 0.75),
        new TrxSample("Ns.Live", "WhenB_Fails_Always", "Failed", 12, failureMessage),
        new TrxSample("Ns.Live", "WhenC_Lacks_Secret", "NotExecuted", 0.01, "Prerequisites missing for the Codefresh API: environment variable CODEFRESH_API_KEY is not set"),
        new TrxSample("Ns.Suite", "WhenE_Case_Varies(1)", "Passed", 0.1),
        new TrxSample("Ns.Suite", "WhenE_Case_Varies(2)", "NotExecuted", 0.1, "Ignored case"),
        new TrxSample("Ns.Stray", "WhenStray_HasNo_Capability", "Passed", 0.2));

    public static TrxRun Run(string? failureMessage = null) =>
        TrxReader.Parse(TrxSamples.AsStream(failureMessage is null ? Trx() : Trx(failureMessage)), "offline.trx");

    public static ConformanceReport Report(string? failureMessage = null) =>
        ConformanceReportBuilder.Build("Platform conformance summary", [Run(failureMessage)], Catalogue, Tests, Start.AddMinutes(2));
}
