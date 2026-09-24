using Microsoft.Extensions.Configuration;
using Sandbox.Web;

namespace Sandbox.Tests;

[TestFixture]
public sealed class VersionInfoTests
{
    [Test]
    public void Should_From_ImageVersionConfigured_ReportsIt()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APP_VERSION"] = "0.1.42",
            ["APP_REVISION"] = "0123456789abcdef0123456789abcdef01234567",
        }).Build();

        var version = VersionInfo.From(configuration);

        version.ShouldBe(new VersionInfo("sandbox", "0.1.42", "0123456789abcdef0123456789abcdef01234567"));
    }

    [Test]
    public void Should_From_NothingConfigured_FallsBackToAssemblyVersion()
    {
        var configuration = new ConfigurationBuilder().Build();

        var version = VersionInfo.From(configuration);

        version.App.ShouldBe("sandbox");
        version.Version.ShouldNotBeNullOrWhiteSpace();
        version.Revision.ShouldBe("unknown");
    }
}
