namespace Platform.Conformance.Harness;

/// <summary>
/// NUnit category names used by the conformance suite. Apply them with <c>[Category(Categories.Live)]</c>
/// so <c>dotnet test --filter "TestCategory=Offline"</c> selects tests.
/// </summary>
/// <remarks>
/// Every test carries exactly one of <see cref="Live"/> or <see cref="Offline"/>. Live tests of a capability
/// whose tier is build, nonprod or prod also carry <see cref="Build"/>, <see cref="NonProd"/> or <see cref="Prod"/>.
/// A destructive test carries <see cref="Destructive"/> and <see cref="NonProd"/> and never <see cref="Prod"/>.
/// </remarks>
public static class Categories
{
    /// <summary>Talks to a real platform system (Octopus, Codefresh, Azure, a cluster or GitHub).</summary>
    public const string Live = "Live";

    /// <summary>Static or self-contained: needs no network and no secret.</summary>
    public const string Offline = "Offline";

    /// <summary>Changes or disrupts platform state (stops a cluster, deletes a resource, runs a runbook that mutates).</summary>
    public const string Destructive = "Destructive";

    /// <summary>Takes minutes rather than seconds (waits for a runbook, a deployment or a cluster wake).</summary>
    public const string Slow = "Slow";

    /// <summary>Targets the nonprod tier (cluster <c>aks-platform-nonprod</c>, environments tdd and uat).</summary>
    public const string NonProd = "NonProd";

    /// <summary>Targets the prod tier (cluster <c>aks-platform-prod</c>, environment prod). Never destructive.</summary>
    public const string Prod = "Prod";

    /// <summary>Targets the build tier (Codefresh, its runner and the shared registry).</summary>
    public const string Build = "Build";

    /// <summary>All category names, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } = [Live, Offline, Destructive, Slow, NonProd, Prod, Build];
}
