using System.Net;
using Platform.Onboarding.Descriptors;
using Platform.Onboarding.Live;
using Platform.Onboarding.Output;
using Platform.Onboarding.Tests.Support;

namespace Platform.Onboarding.Tests;

[TestFixture]
[Category("Offline")]
public class LiveCheckTests
{
    private static readonly StubEnvironment Environment = new StubEnvironment()
        .With(LiveCheck.OctopusUrlVariable, "https://octopus.example")
        .With(LiveCheck.OctopusSpaceVariable, "Spaces-1")
        .With(LiveCheck.OctopusKeyVariable, "stub-key")
        .With(LiveCheck.CodefreshKeyVariable, "stub-cf-key");

    private static AppDescriptor Load(string yaml) =>
        DescriptorCatalog.LoadFile("apps/demoapp.yaml", yaml, DescriptorSchema.Load(TempRepository.CommittedSchemaPath)).Descriptor.ShouldNotBeNull();

    [Test]
    public async Task Should_RunAsync_EverythingPresent_ReportsNothing()
    {
        var handler = new StubHttpMessageHandler()
            .Respond("/api/Spaces-1/projectgroups/all", """[{"Id":"ProjectGroups-9","Name":"app-demoapp"}]""")
            .Respond("/api/Spaces-1/projects/all", """[{"Id":"Projects-1","Name":"demoapp","ProjectGroupId":"ProjectGroups-9","IsDisabled":false}]""")
            .Respond("/api/projects/name/demoapp", """{"id":"p1","projectName":"demoapp"}""");

        var findings = await new LiveCheck(Environment, () => handler).RunAsync([Load(SampleDescriptors.Minimal())], CancellationToken.None);

        findings.ShouldBeEmpty();
        handler.Requests.ShouldContain(request => request.Headers.Contains("X-Octopus-ApiKey"));
    }

    [Test]
    public async Task Should_RunAsync_MissingObjects_ReportsEachOne()
    {
        var handler = new StubHttpMessageHandler()
            .Respond("/api/Spaces-1/projectgroups/all", "[]")
            .Respond("/api/Spaces-1/projects/all", "[]")
            .Respond("/api/projects/name/demoapp", "{}", HttpStatusCode.NotFound);

        var findings = await new LiveCheck(Environment, () => handler).RunAsync([Load(SampleDescriptors.Minimal())], CancellationToken.None);

        findings.Count(finding => finding.Severity == Severity.Error).ShouldBe(3);
    }

    [Test]
    public async Task Should_RunAsync_FrozenAppWithEnabledProject_ReportsMismatch()
    {
        var handler = new StubHttpMessageHandler()
            .Respond("/api/Spaces-1/projectgroups/all", """[{"Id":"ProjectGroups-9","Name":"app-demoapp"}]""")
            .Respond("/api/Spaces-1/projects/all", """[{"Id":"Projects-1","Name":"demoapp","ProjectGroupId":"ProjectGroups-9","IsDisabled":false}]""")
            .Respond("/api/projects/name/demoapp", "{}");

        var findings = await new LiveCheck(Environment, () => handler).RunAsync([Load(SampleDescriptors.Minimal() + "\nstatus: frozen\n")], CancellationToken.None);

        findings.ShouldHaveSingleItem().Message.ShouldContain("is enabled but the app is frozen");
    }
}
