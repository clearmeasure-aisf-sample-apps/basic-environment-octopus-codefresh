using Sandbox.Web;

namespace Sandbox.Tests;

/// <summary>In-memory canary store for handler tests.</summary>
internal sealed class StubCanaryStore : ICanaryStore
{
    public Canary? Stored { get; private set; }

    public StubCanaryStore(Canary? initial = null)
    {
        Stored = initial;
    }

    public Task<Canary?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

    public Task<Canary> SetAsync(string value, CancellationToken cancellationToken)
    {
        Stored = new Canary(value, new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc));
        return Task.FromResult(Stored);
    }
}
