using Platform.Conformance.Harness;
using Platform.Conformance.Tests.Kit;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// CAP-KIT-008 (offline half): the orphan report skips only the Codefresh account's own project <c>default</c>, and only
/// while it holds no pipeline; every other project without a descriptor stays an orphan.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class OrphanRuleTests
{
    /// <summary>The account default is exempt while empty; a pipeline in it, or any other name, is not.</summary>
    /// <param name="name">Project name.</param>
    /// <param name="pipelines">Its number of pipelines.</param>
    /// <param name="exempt">Whether the orphan report skips it.</param>
    [TestCase("default", 0, true)]
    [TestCase("default", 1, false)]
    [TestCase("Default", 0, false)]
    [TestCase("demo", 0, false)]
    [Capability("CAP-KIT-008")]
    public void Should_ReadCodefreshProject_AccountDefault_IsExemptOnlyWhileEmpty(string name, int pipelines, bool exempt)
    {
        KitNames.IsCodefreshAccountDefault(name, pipelines).ShouldBe(exempt);
    }
}
