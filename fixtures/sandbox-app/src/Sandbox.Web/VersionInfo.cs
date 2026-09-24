using System.Reflection;

namespace Sandbox.Web;

/// <summary>What <c>/version</c> reports: the app, the release version and the source revision.</summary>
/// <param name="App">Always <c>sandbox</c>.</param>
/// <param name="Version">Release version (image build argument <c>VERSION</c>, as <c>APP_VERSION</c>).</param>
/// <param name="Revision">Source commit (image build argument <c>REVISION</c>, as <c>APP_REVISION</c>).</param>
public sealed record VersionInfo(string App, string Version, string Revision)
{
    /// <summary>Reads the version from configuration, falling back to the assembly's informational version.</summary>
    /// <param name="configuration">Application configuration.</param>
    public static VersionInfo From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var assemblyVersion = typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-local";
        var version = configuration["APP_VERSION"];
        var revision = configuration["APP_REVISION"];
        return new VersionInfo(
            "sandbox",
            string.IsNullOrWhiteSpace(version) ? assemblyVersion : version,
            string.IsNullOrWhiteSpace(revision) ? "unknown" : revision);
    }
}
