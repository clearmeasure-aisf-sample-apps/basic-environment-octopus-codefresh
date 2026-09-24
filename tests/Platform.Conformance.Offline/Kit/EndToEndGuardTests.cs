using Platform.Conformance.Harness;
using Platform.Conformance.Tests.Kit;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// CAP-KIT-009, offline side: the end-to-end pass refuses every repository outside clearmeasure-aisf-sample-apps
/// before it writes anything, and accepts app #1's copy from its descriptor.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class EndToEndGuardTests
{
    [TestCase("ClearMeasureLabs/bootcamp-palermo-workorders")]
    [TestCase("clearmeasurelabs/bootcamp-palermo-workorders")]
    [TestCase("<sandbox-app-repo>")]
    [TestCase("clearmeasure-aisf-sample-apps")]
    [TestCase("someone/20260923-001")]
    [Capability("CAP-KIT-009")]
    public void Should_AssertAllowedRepository_OutsideTheOrg_Throws(string repository)
    {
        var refusal = Should.Throw<InvalidOperationException>(() => EndToEndGuard.AssertAllowedRepository(repository));

        refusal.Message.ShouldContain("never the upstream ClearMeasureLabs repository");
    }

    [Test]
    [Capability("CAP-KIT-009")]
    public void Should_AssertAllowedRepository_AppOneRepository_Passes()
    {
        var workorders = KitDescriptors.Load(KitToolbox.RepositoryRoot).Single(descriptor => descriptor.Name == "workorders");

        Should.NotThrow(() => EndToEndGuard.AssertAllowedRepository(workorders.Repositories[0].Name));
        workorders.Repositories[0].DefaultBranch.ShouldBe("master");
    }
}
