using NUnit.Framework;

namespace Platform.Conformance.Harness;

/// <summary>
/// Declares which platform capability a test proves. The ID must exist in the capability catalogue
/// (<c>catalogue/capabilities.yaml</c> and <c>catalogue/capabilities.d/*.yaml</c>), and the catalogue entry
/// must list the test. A test may prove several capabilities; repeat the attribute for each.
/// </summary>
/// <remarks>
/// The attribute is an NUnit property, so the ID also appears as the <c>Capability</c> property of the test
/// in NUnit output. <c>CatalogueConsistencyTests</c> enforces the one-to-one mapping.
/// </remarks>
/// <example><code>[Test, Capability("CAP-SLEEP-001")]</code></example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class CapabilityAttribute : PropertyAttribute
{
    /// <summary>Name of the NUnit property that carries the capability ID.</summary>
    public const string PropertyName = "Capability";

    /// <summary>Marks the test as proof of the capability with the given ID.</summary>
    /// <param name="id">Catalogue ID such as <c>CAP-SLEEP-001</c>.</param>
    public CapabilityAttribute(string id)
        : base(PropertyName, id)
    {
        Id = id;
    }

    /// <summary>The capability ID, for example <c>CAP-SLEEP-001</c>.</summary>
    public string Id { get; }
}
