namespace Platform.Conformance.Harness.Catalogue;

/// <summary>The team or tool that owns a capability. The order is the order of the rendered catalogue.</summary>
public enum CapabilityOwner
{
    /// <summary><c>codefresh</c>: builds, images, packages and releases.</summary>
    Codefresh,

    /// <summary><c>octopus</c>: releases, promotion, approvals, migrations and runbooks.</summary>
    Octopus,

    /// <summary><c>argocd</c>: reconciliation of the GitOps overlays.</summary>
    ArgoCd,

    /// <summary><c>azure</c>: clusters, networks, identities, registry and monitoring.</summary>
    Azure,

    /// <summary><c>kyverno</c>: admission policies.</summary>
    Kyverno,

    /// <summary><c>onboarding</c>: adding applications and teams to the platform.</summary>
    Onboarding,

    /// <summary><c>platform</c>: cross-cutting behaviour, including this harness.</summary>
    Platform,
}

/// <summary>Where a capability applies.</summary>
public enum CapabilityTier
{
    /// <summary><c>build</c>: Codefresh, its runner and the shared registry.</summary>
    Build,

    /// <summary><c>nonprod</c>: the nonprod cluster and environments tdd and uat.</summary>
    NonProd,

    /// <summary><c>prod</c>: the prod cluster and environment prod.</summary>
    Prod,

    /// <summary><c>all</c>: every tier.</summary>
    All,
}

/// <summary>Conversions between catalogue enums and their YAML spellings.</summary>
public static class CatalogueNames
{
    /// <summary>YAML spelling of <paramref name="owner"/>, for example <c>argocd</c>.</summary>
    /// <param name="owner">The owner.</param>
    public static string ToYaml(this CapabilityOwner owner) => owner switch
    {
        CapabilityOwner.Codefresh => "codefresh",
        CapabilityOwner.Octopus => "octopus",
        CapabilityOwner.ArgoCd => "argocd",
        CapabilityOwner.Azure => "azure",
        CapabilityOwner.Kyverno => "kyverno",
        CapabilityOwner.Onboarding => "onboarding",
        CapabilityOwner.Platform => "platform",
        _ => throw new ArgumentOutOfRangeException(nameof(owner), owner, "Unknown owner."),
    };

    /// <summary>YAML spelling of <paramref name="tier"/>, for example <c>nonprod</c>.</summary>
    /// <param name="tier">The tier.</param>
    public static string ToYaml(this CapabilityTier tier) => tier switch
    {
        CapabilityTier.Build => "build",
        CapabilityTier.NonProd => "nonprod",
        CapabilityTier.Prod => "prod",
        CapabilityTier.All => "all",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown tier."),
    };

    /// <summary>Every owner spelling, in catalogue order.</summary>
    public static IReadOnlyList<string> Owners { get; } = Enum.GetValues<CapabilityOwner>().Select(owner => owner.ToYaml()).ToArray();

    /// <summary>Every tier spelling.</summary>
    public static IReadOnlyList<string> Tiers { get; } = Enum.GetValues<CapabilityTier>().Select(tier => tier.ToYaml()).ToArray();

    /// <summary>Parses an owner spelling (exact, lowercase).</summary>
    /// <param name="value">The YAML value.</param>
    /// <param name="owner">The parsed owner.</param>
    public static bool TryParseOwner(string? value, out CapabilityOwner owner) => TryParse(value, CapabilityOwnerValues, ToYaml, out owner);

    /// <summary>Parses a tier spelling (exact, lowercase).</summary>
    /// <param name="value">The YAML value.</param>
    /// <param name="tier">The parsed tier.</param>
    public static bool TryParseTier(string? value, out CapabilityTier tier) => TryParse(value, CapabilityTierValues, ToYaml, out tier);

    private static readonly CapabilityOwner[] CapabilityOwnerValues = Enum.GetValues<CapabilityOwner>();

    private static readonly CapabilityTier[] CapabilityTierValues = Enum.GetValues<CapabilityTier>();

    private static bool TryParse<T>(string? value, T[] candidates, Func<T, string> spell, out T result)
        where T : struct
    {
        foreach (var candidate in candidates)
        {
            if (string.Equals(spell(candidate), value, StringComparison.Ordinal))
            {
                result = candidate;
                return true;
            }
        }

        result = default;
        return false;
    }
}
