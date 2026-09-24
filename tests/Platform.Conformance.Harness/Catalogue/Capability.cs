namespace Platform.Conformance.Harness.Catalogue;

/// <summary>One platform capability from the catalogue, with the tests that prove it.</summary>
public sealed record Capability
{
    /// <summary>Unique ID such as <c>CAP-SLEEP-001</c>.</summary>
    public required string Id { get; init; }

    /// <summary>What the platform guarantees, as one sentence.</summary>
    public required string Statement { get; init; }

    /// <summary>Owning team or tool.</summary>
    public required CapabilityOwner Owner { get; init; }

    /// <summary>Design reference: an ADR, a design section or a document path.</summary>
    public required string Adr { get; init; }

    /// <summary>How the capability is observed (the API, object or signal the tests read).</summary>
    public required string ObservedBy { get; init; }

    /// <summary>Fully qualified names (<c>Namespace.Class.Method</c>) of the tests that prove it.</summary>
    public required IReadOnlyList<string> Tests { get; init; }

    /// <summary><c>true</c> when the proof needs the live platform.</summary>
    public required bool Live { get; init; }

    /// <summary><c>true</c> when proving it changes or disrupts platform state.</summary>
    public required bool Destructive { get; init; }

    /// <summary>Where the capability applies.</summary>
    public required CapabilityTier Tier { get; init; }

    /// <summary>Why the capability is proven offline; required when <see cref="Live"/> is <c>false</c>.</summary>
    public string? WhyOffline { get; init; }

    /// <summary>Catalogue file that defines it, relative to the repository root when loaded from the folder.</summary>
    public required string Source { get; init; }

    /// <summary>Line of the entry in <see cref="Source"/> (1-based).</summary>
    public required int Line { get; init; }

    /// <summary><c>Source:Line</c>, for messages.</summary>
    public string Location => $"{Source}:{Line}";
}

/// <summary>Text of one catalogue file with the name used in messages.</summary>
/// <param name="Name">Display name, for example <c>catalogue/capabilities.d/harness.yaml</c>.</param>
/// <param name="Content">The YAML text.</param>
public sealed record CatalogueSource(string Name, string Content);

/// <summary>The catalogue is invalid; <see cref="Errors"/> lists every problem found, each with file and line.</summary>
public sealed class CatalogueValidationException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="errors">Problems found; each names its file and line.</param>
    public CatalogueValidationException(IReadOnlyList<string> errors)
        : base($"The capability catalogue is invalid ({errors.Count} problem{(errors.Count == 1 ? "" : "s")}):{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", errors)}")
    {
        Errors = errors;
    }

    /// <summary>Every problem found.</summary>
    public IReadOnlyList<string> Errors { get; }
}
