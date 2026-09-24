namespace Platform.Conformance.Harness.Support;

/// <summary>Thread-safe <see cref="ICleanupRegistry"/> that runs actions last-in, first-out.</summary>
public sealed class CleanupRegistry : ICleanupRegistry
{
    private readonly Stack<(string Description, Func<CancellationToken, Task> Cleanup)> actions = new();
    private readonly Lock gate = new();

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (gate)
            {
                return actions.Count;
            }
        }
    }

    /// <inheritdoc />
    public void Register(string description, Func<CancellationToken, Task> cleanup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(cleanup);
        lock (gate)
        {
            actions.Push((description, cleanup));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CleanupFailure>> RunAllAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<CleanupFailure>();
        while (TryPop(out var action))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await action.Cleanup(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(new CleanupFailure(action.Description, ex));
            }
        }

        return failures;
    }

    private bool TryPop(out (string Description, Func<CancellationToken, Task> Cleanup) action)
    {
        lock (gate)
        {
            return actions.TryPop(out action);
        }
    }
}
