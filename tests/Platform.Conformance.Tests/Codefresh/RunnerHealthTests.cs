using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-002: the runner agent is healthy. Observed through <c>GET /agents</c>: the agent of the runtime reports
/// <c>healthy</c>, and its last report is under five minutes old.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class RunnerHealthTests : CodefreshCapabilityTestBase
{
    private static readonly TimeSpan ReportAge = TimeSpan.FromMinutes(5);

    /// <summary>aks-platform-build_codefresh serves the runtime, is healthy and reported recently.</summary>
    [Test]
    [Capability("CAP-CF-002")]
    [Category(Categories.Build)]
    [CancelAfter(5 * 60 * 1000)]
    public async Task Should_GetAgentsAsync_PlatformRunner_IsHealthy()
    {
        var agents = await Codefresh.GetAgentsAsync(Token);

        var agent = agents.SingleOrDefault(candidate => candidate.Name == CodefreshPlatform.Agent);

        agent.ShouldNotBeNull($"no agent {CodefreshPlatform.Agent}; agents: {string.Join(", ", agents.Select(candidate => candidate.Name))}");
        agent.Runtimes.ShouldContain(CodefreshPlatform.Runtime);
        (agent.HealthStatus ?? string.Empty).ToLowerInvariant().ShouldBe("healthy", $"agent {agent.Name} reports {agent.HealthStatus}");
        agent.ReportedAt.ShouldNotBeNull($"agent {agent.Name} has no health report");
        (DateTimeOffset.UtcNow - agent.ReportedAt.Value).ShouldBeLessThan(ReportAge, $"agent {agent.Name} last reported at {agent.ReportedAt:O}");
    }
}
