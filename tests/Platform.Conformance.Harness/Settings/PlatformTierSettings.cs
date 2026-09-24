namespace Platform.Conformance.Harness.Settings;

/// <summary>Azure names of one tier. Values may be <c>null</c> or placeholders until provisioning fills them in.</summary>
public sealed record PlatformTierSettings
{
    /// <summary>An empty tier (nothing configured).</summary>
    public static PlatformTierSettings Empty { get; } = new();

    /// <summary>Subscription of the tier when it differs from the platform subscription (the Codefresh runner cluster lives in another one).</summary>
    public string? SubscriptionId { get; init; }

    /// <summary>Resource group that holds the tier's cluster (or, for build, the shared resources).</summary>
    public string? ResourceGroup { get; init; }

    /// <summary>AKS cluster name of the tier.</summary>
    public string? ClusterName { get; init; }

    /// <summary>Other resource groups of the tier: the application environments (nonprod, prod) or the shared registry and state (build).</summary>
    public IReadOnlyList<string> ResourceGroups { get; init; } = [];
}

/// <summary>Time limits for waits in live tests.</summary>
public sealed record PlatformTimeLimits
{
    /// <summary>Pause between polls of a task, build, cluster or Argo CD Application (default 10 s).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Timeout of one HTTP request (default 100 s).</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>Longest wait for an Octopus runbook run (default 30 min).</summary>
    public TimeSpan RunbookTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Longest wait for an Octopus deployment (default 45 min).</summary>
    public TimeSpan DeploymentTimeout { get; init; } = TimeSpan.FromMinutes(45);

    /// <summary>Longest wait for a Codefresh build (default 60 min).</summary>
    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>Longest wait for a cluster to wake (default 25 min; runbook <c>env-wake</c> itself allows 20).</summary>
    public TimeSpan WakeTimeout { get; init; } = TimeSpan.FromMinutes(25);

    /// <summary>Longest wait for an Argo CD Application to report Synced and Healthy (default 15 min).</summary>
    public TimeSpan ArgoSyncTimeout { get; init; } = TimeSpan.FromMinutes(15);
}
