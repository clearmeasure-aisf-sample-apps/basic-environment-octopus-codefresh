namespace Platform.Conformance.Harness.Settings;

/// <summary>A platform tier with its own Azure resources.</summary>
public enum PlatformTier
{
    /// <summary>Codefresh runner cluster and the shared resource group (registry, state).</summary>
    Build,

    /// <summary>Cluster <c>aks-platform-nonprod</c>: environments tdd and uat.</summary>
    NonProd,

    /// <summary>Cluster <c>aks-platform-prod</c>: environment prod.</summary>
    Prod,
}

/// <summary>Conversions between <see cref="PlatformTier"/> and its lowercase settings-file key.</summary>
public static class PlatformTierNames
{
    /// <summary>The key used in the settings file and the catalogue: <c>build</c>, <c>nonprod</c> or <c>prod</c>.</summary>
    /// <param name="tier">The tier.</param>
    public static string ToKey(this PlatformTier tier) => tier switch
    {
        PlatformTier.Build => "build",
        PlatformTier.NonProd => "nonprod",
        PlatformTier.Prod => "prod",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown tier."),
    };

    /// <summary>Parses a settings-file key; returns <c>false</c> for anything else.</summary>
    /// <param name="key">The key, compared case-insensitively.</param>
    /// <param name="tier">The parsed tier.</param>
    public static bool TryParse(string? key, out PlatformTier tier)
    {
        foreach (var candidate in Enum.GetValues<PlatformTier>())
        {
            if (string.Equals(candidate.ToKey(), key, StringComparison.OrdinalIgnoreCase))
            {
                tier = candidate;
                return true;
            }
        }

        tier = default;
        return false;
    }
}
