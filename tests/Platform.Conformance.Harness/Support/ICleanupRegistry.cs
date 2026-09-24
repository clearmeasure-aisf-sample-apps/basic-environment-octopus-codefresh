namespace Platform.Conformance.Harness.Support;

/// <summary>
/// Collects cleanup actions for resources a test creates (pods, branches, releases). <see cref="PlatformTestBase"/>
/// runs them in reverse registration order in its <c>[OneTimeTearDown]</c>, even when a test failed.
/// </summary>
public interface ICleanupRegistry
{
    /// <summary>Number of cleanup actions still registered.</summary>
    int Count { get; }

    /// <summary>Registers an asynchronous cleanup action.</summary>
    /// <param name="description">What the action removes, shown if it fails (for example "delete pod conf-x in workorders-tdd").</param>
    /// <param name="cleanup">The action; it receives the teardown cancellation token.</param>
    void Register(string description, Func<CancellationToken, Task> cleanup);

    /// <summary>Runs every registered action in reverse registration order and empties the registry.</summary>
    /// <param name="cancellationToken">Cancels the remaining actions.</param>
    /// <returns>The actions that threw; every action is attempted even when an earlier one fails.</returns>
    Task<IReadOnlyList<CleanupFailure>> RunAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>A cleanup action that threw.</summary>
/// <param name="Description">The description given at registration.</param>
/// <param name="Exception">The exception it threw.</param>
public sealed record CleanupFailure(string Description, Exception Exception);

/// <summary>Raised after cleanup when one or more cleanup actions failed; lists each failure.</summary>
public sealed class CleanupFailedException : Exception
{
    /// <summary>Creates the exception from the failures reported by <see cref="ICleanupRegistry.RunAllAsync"/>.</summary>
    /// <param name="failures">The failed actions; must not be empty.</param>
    public CleanupFailedException(IReadOnlyList<CleanupFailure> failures)
        : base(BuildMessage(failures), failures.Count > 0 ? failures[0].Exception : null)
    {
        Failures = failures;
    }

    /// <summary>The failed actions.</summary>
    public IReadOnlyList<CleanupFailure> Failures { get; }

    private static string BuildMessage(IReadOnlyList<CleanupFailure> failures) =>
        $"{failures.Count} cleanup action{(failures.Count == 1 ? "" : "s")} failed; the resources may need manual removal: "
        + string.Join("; ", failures.Select(failure => $"{failure.Description} ({failure.Exception.GetType().Name}: {failure.Exception.Message})"));
}
