namespace Platform.Conformance.Harness.Catalogue;

/// <summary>The rule a <see cref="CatalogueViolation"/> breaks.</summary>
public enum ViolationKind
{
    /// <summary>A catalogue capability has no test carrying its ID.</summary>
    CapabilityWithoutTest,

    /// <summary>A test carries no <c>[Capability]</c>.</summary>
    TestWithoutCapability,

    /// <summary>A test carries an ID that is not in the catalogue.</summary>
    UnknownCapability,

    /// <summary>A catalogue <c>tests</c> entry names a test that does not exist or does not carry the capability.</summary>
    StaleTestEntry,

    /// <summary>A test carries a capability whose <c>tests</c> list does not name it.</summary>
    UnlistedTest,

    /// <summary>A capability with <c>live: false</c> has no <c>why_offline</c>.</summary>
    MissingWhyOffline,

    /// <summary>A destructive test is not categorised Destructive and NonProd, is categorised Prod, or its capability is not marked destructive.</summary>
    DestructiveCategory,

    /// <summary>A test is not categorised exactly one of Live and Offline, or its categories contradict the capability's <c>live</c> flag.</summary>
    LiveCategory,

    /// <summary>A live test lacks the category of its capability's tier (Build, NonProd or Prod).</summary>
    TierCategory,
}

/// <summary>One broken consistency rule.</summary>
/// <param name="Kind">The rule.</param>
/// <param name="Message">What is wrong and how to fix it.</param>
/// <param name="CapabilityId">The capability involved, if any.</param>
/// <param name="TestName">The test involved, if any.</param>
public sealed record CatalogueViolation(ViolationKind Kind, string Message, string? CapabilityId = null, string? TestName = null)
{
    /// <summary><c>[Kind] Message</c>.</summary>
    public override string ToString() => $"[{Kind}] {Message}";
}

/// <summary>
/// Checks the one-to-one mapping between the catalogue and the tests: every capability has a test, every test
/// proves a known capability, the catalogue's <c>tests</c> lists match the attributes exactly, and categories agree
/// with the <c>live</c>, <c>destructive</c> and <c>tier</c> flags.
/// </summary>
public static class CatalogueConsistency
{
    /// <summary>Returns every violation, grouped by capability then by test, in a stable order.</summary>
    /// <param name="catalogue">The merged catalogue.</param>
    /// <param name="tests">Tests discovered in the conformance assemblies.</param>
    public static IReadOnlyList<CatalogueViolation> Check(CapabilityCatalogue catalogue, IReadOnlyList<DiscoveredTest> tests)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(tests);
        var violations = new List<CatalogueViolation>();
        var testsByName = tests.GroupBy(test => test.FullName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var capability in catalogue.Capabilities.OrderBy(capability => capability.Id, StringComparer.Ordinal))
        {
            CheckCapability(capability, tests, testsByName, violations);
        }

        foreach (var test in tests.OrderBy(test => test.FullName, StringComparer.Ordinal))
        {
            CheckTest(test, catalogue, violations);
        }

        return violations;
    }

    private static void CheckCapability(Capability capability, IReadOnlyList<DiscoveredTest> tests, Dictionary<string, DiscoveredTest> testsByName, List<CatalogueViolation> violations)
    {
        var id = capability.Id;
        var carriers = tests.Where(test => test.CapabilityIds.Contains(id, StringComparer.Ordinal)).ToList();
        if (carriers.Count == 0)
        {
            violations.Add(new(ViolationKind.CapabilityWithoutTest, $"{id} ({capability.Location}) has no test carrying [Capability(\"{id}\")]; every capability needs at least one test.", id));
        }

        foreach (var listed in capability.Tests)
        {
            if (!testsByName.TryGetValue(listed, out var test))
            {
                violations.Add(new(ViolationKind.StaleTestEntry, $"{id} ({capability.Location}) lists {listed}, which is not a test in the conformance assemblies; remove or rename the entry.", id, listed));
            }
            else if (!test.CapabilityIds.Contains(id, StringComparer.Ordinal))
            {
                violations.Add(new(ViolationKind.StaleTestEntry, $"{id} ({capability.Location}) lists {listed}, which does not carry [Capability(\"{id}\")].", id, listed));
            }
        }

        foreach (var carrier in carriers.Where(test => !capability.Tests.Contains(test.FullName, StringComparer.Ordinal)))
        {
            violations.Add(new(ViolationKind.UnlistedTest, $"{carrier.FullName} carries [Capability(\"{id}\")] but {id} ({capability.Location}) does not list it under tests.", id, carrier.FullName));
        }

        if (!capability.Live && string.IsNullOrWhiteSpace(capability.WhyOffline))
        {
            violations.Add(new(ViolationKind.MissingWhyOffline, $"{id} ({capability.Location}) has live: false but no why_offline.", id));
        }

        if (capability.Live && carriers.Count > 0 && !carriers.Any(test => test.HasCategory(Categories.Live)))
        {
            violations.Add(new(ViolationKind.LiveCategory, $"{id} ({capability.Location}) has live: true but none of its tests is categorised {Categories.Live}.", id));
        }
    }

    private static void CheckTest(DiscoveredTest test, CapabilityCatalogue catalogue, List<CatalogueViolation> violations)
    {
        var name = test.FullName;
        if (test.CapabilityIds.Count == 0)
        {
            violations.Add(new(ViolationKind.TestWithoutCapability, $"{name} has no [Capability]; every test must prove a catalogue capability.", TestName: name));
        }

        var capabilities = new List<Capability>();
        foreach (var id in test.CapabilityIds)
        {
            if (catalogue.Find(id) is { } capability)
            {
                capabilities.Add(capability);
            }
            else
            {
                violations.Add(new(ViolationKind.UnknownCapability, $"{name} carries [Capability(\"{id}\")], which is not in the catalogue.", id, name));
            }
        }

        var live = test.HasCategory(Categories.Live);
        var offline = test.HasCategory(Categories.Offline);
        if (live && offline)
        {
            violations.Add(new(ViolationKind.LiveCategory, $"{name} is categorised both {Categories.Live} and {Categories.Offline}; use exactly one.", TestName: name));
        }
        else if (!live && !offline)
        {
            violations.Add(new(ViolationKind.LiveCategory, $"{name} is categorised neither {Categories.Live} nor {Categories.Offline}; add exactly one so category filters select it.", TestName: name));
        }

        foreach (var capability in capabilities.Where(candidate => !candidate.Live && live))
        {
            violations.Add(new(ViolationKind.LiveCategory, $"{name} is categorised {Categories.Live} but {capability.Id} ({capability.Location}) has live: false.", capability.Id, name));
        }

        CheckDestructive(test, capabilities, violations);
        if (live)
        {
            CheckTier(test, capabilities, violations);
        }
    }

    private static void CheckDestructive(DiscoveredTest test, List<Capability> capabilities, List<CatalogueViolation> violations)
    {
        var name = test.FullName;
        var destructiveCapabilities = capabilities.Where(capability => capability.Destructive).Select(capability => capability.Id).ToList();
        var categorised = test.HasCategory(Categories.Destructive);
        if (!categorised && destructiveCapabilities.Count == 0)
        {
            return;
        }

        if (!categorised)
        {
            violations.Add(new(ViolationKind.DestructiveCategory, $"{name} proves destructive capability {string.Join(", ", destructiveCapabilities)} but is not categorised {Categories.Destructive}.", destructiveCapabilities[0], name));
        }

        if (!test.HasCategory(Categories.NonProd))
        {
            violations.Add(new(ViolationKind.DestructiveCategory, $"{name} is destructive but not categorised {Categories.NonProd}; destructive tests run in nonprod only.", TestName: name));
        }

        if (test.HasCategory(Categories.Prod))
        {
            violations.Add(new(ViolationKind.DestructiveCategory, $"{name} is destructive and categorised {Categories.Prod}; destructive tests never run in prod.", TestName: name));
        }

        if (categorised && destructiveCapabilities.Count == 0 && capabilities.Count > 0)
        {
            violations.Add(new(ViolationKind.DestructiveCategory, $"{name} is categorised {Categories.Destructive} but none of its capabilities ({string.Join(", ", capabilities.Select(capability => capability.Id))}) has destructive: true.", TestName: name));
        }
    }

    private static void CheckTier(DiscoveredTest test, List<Capability> capabilities, List<CatalogueViolation> violations)
    {
        foreach (var capability in capabilities)
        {
            var required = capability.Tier switch
            {
                CapabilityTier.Build => Categories.Build,
                CapabilityTier.NonProd => Categories.NonProd,
                CapabilityTier.Prod => Categories.Prod,
                _ => null,
            };
            if (required is not null && !test.HasCategory(required))
            {
                violations.Add(new(ViolationKind.TierCategory, $"{test.FullName} is a live test of {capability.Id} (tier {capability.Tier.ToYaml()}) but is not categorised {required}.", capability.Id, test.FullName));
            }
        }
    }
}
