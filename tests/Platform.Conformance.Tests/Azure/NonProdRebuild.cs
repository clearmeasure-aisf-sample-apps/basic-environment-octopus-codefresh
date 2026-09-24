using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// The one nonprod rebuild of a test run (env-destroy, env-apply, apps-apply for the sandbox), shared by
/// <see cref="RebuildDataSurvivalTests"/> (CAP-AZ-008), which writes its canary first, and
/// <see cref="TierRebuildTests"/> (CAP-AZ-007). A rebuild takes most of an hour, so the run pays for it once.
/// </summary>
public static class NonProdRebuild
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RebuildOutcome? outcome;
    private static Exception? failure;

    /// <summary><c>true</c> once a test of this process has started the rebuild.</summary>
    public static bool Started { get; private set; }

    /// <summary>Runs <paramref name="rebuild"/> the first time; later callers get its outcome or its failure.</summary>
    /// <param name="rebuild">The rebuild procedure (<see cref="AzureConformanceTest.RebuildNonProdAsync"/>).</param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the first run.</param>
    public static async Task<RebuildOutcome> RunOnceAsync(Func<CancellationToken, Task<RebuildOutcome>> rebuild, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rebuild);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (outcome is not null)
            {
                return outcome;
            }

            switch (failure)
            {
                case PlatformPrerequisiteException missing:
                    throw new PlatformPrerequisiteException($"The shared nonprod rebuild of this run could not start: {missing.Message}", missing);
                case not null:
                    Assert.Fail($"The shared nonprod rebuild of this run failed earlier: {failure.GetType().Name}: {failure.Message}");
                    break;
            }

            Started = true;
            try
            {
                outcome = await rebuild(cancellationToken);
                return outcome;
            }
            catch (Exception ex)
            {
                failure = ex;
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>What the nonprod rebuild did and saw.</summary>
/// <param name="Destroy">The env-destroy run.</param>
/// <param name="Apply">The env-apply run.</param>
/// <param name="AppsApply">The apps-apply run for the sandbox.</param>
/// <param name="IssuerBefore">OIDC issuer of the cluster before the destroy.</param>
/// <param name="IssuerBetween">OIDC issuer between destroy and apply; <c>null</c> when the cluster was gone.</param>
/// <param name="IssuerAfter">OIDC issuer of the rebuilt cluster.</param>
/// <param name="IngressBefore">Address of pip-platform-nonprod-ingress before the destroy.</param>
/// <param name="IngressAfter">Address of pip-platform-nonprod-ingress after the apply.</param>
public sealed record RebuildOutcome(
    RunbookOutcome Destroy,
    RunbookOutcome Apply,
    RunbookOutcome AppsApply,
    string? IssuerBefore,
    string? IssuerBetween,
    string? IssuerAfter,
    string? IngressBefore,
    string? IngressAfter);
