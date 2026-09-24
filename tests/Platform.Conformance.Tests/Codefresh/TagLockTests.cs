using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-007: released tags are write-locked. Observed as <c>writeEnabled</c> and <c>deleteEnabled</c> of every tag
/// pinned under gitops/apps/*/envs/** (the bootstrap pin <c>0.0.0-bootstrap</c>, which precedes an app's first release,
/// is skipped). supply_chain locks each tag after signing; nothing unlocks a pinned tag.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class TagLockTests : CodefreshCapabilityTestBase
{
    /// <summary>Every pinned tag has writeEnabled false and deleteEnabled false.</summary>
    [Test]
    [Capability("CAP-CF-007")]
    [Category(Categories.Build)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_GetRegistryTagAsync_EveryPinnedTag_IsLocked()
    {
        var pins = CodefreshPlatform.ReadPins()
            .Where(pin => !pin.IsBootstrap)
            .DistinctBy(pin => pin.ToString())
            .ToArray();
        if (pins.Length == 0)
        {
            throw new PlatformPrerequisiteException("Prerequisites missing for the tag lock test: no app has a release pin yet (every pin is 0.0.0-bootstrap).");
        }

        var unlocked = new List<string>();
        foreach (var pin in pins)
        {
            var tag = await Azure.GetRegistryTagAsync(pin.Repository, pin.Tag, Token);
            if (tag.Attributes.WriteEnabled != false || tag.Attributes.DeleteEnabled != false)
            {
                unlocked.Add($"{pin} ({pin.File}): writeEnabled={tag.Attributes.WriteEnabled}, deleteEnabled={tag.Attributes.DeleteEnabled}");
            }
        }

        unlocked.ShouldBeEmpty($"pinned tags that are not locked: {string.Join("; ", unlocked)}");
    }
}
