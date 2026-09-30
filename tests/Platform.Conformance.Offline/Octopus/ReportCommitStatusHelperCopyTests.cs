using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-KIT-010, offline conformance half: the Octopus step Report platform/tdd status (<c>report-commit-status</c>) carries the
/// marked region of <c>scripts/github/GitHubAppAuth.ps1</c> verbatim, so the JWT and installation-token code that the unit and
/// integration tests prove is the code the step runs, and no second implementation (openssl, an inline JWT) is left in it.
/// Pure text checks: no pwsh needed.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ReportCommitStatusHelperCopyTests
{
    private const string Helper = "scripts/github/GitHubAppAuth.ps1";
    private const string Process = ".octopus/apps/workorders/workorders/deployment_process.ocl";
    private const string Slug = "report-commit-status";

    /// <summary>Every inline copy of the helper region in an app process or starter equals the region of the helper, and the workorders process has exactly one, inside the step.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_InlineGitHubAppAuth_EveryCopy_EqualsTheMarkedRegionOfTheHelper()
    {
        var canonical = OctopusRepository.MarkedRegion(Helper);
        var copies = OctopusRepository.AppProcesses()
            .SelectMany(file => OctopusRepository.InlineCopiesOf(OctopusRepository.Read(file), Helper).Select(copy => (file, copy)))
            .ToArray();

        canonical.ShouldNotBeEmpty();
        copies.Select(entry => entry.file).ShouldBe([Process], "only the workorders process inlines the helper, once");
        foreach (var (file, copy) in copies)
        {
            copy.ShouldBe(canonical, $"{file}: the inline copy of {Helper} differs from its marked region; copy the region again (indentation removed)");
        }

        var step = OctopusRepository.Steps(OctopusRepository.Read(Process)).Single(candidate => candidate.Slug == Slug);
        OctopusRepository.InlineCopiesOf(step.Text, Helper).ShouldHaveSingleItem($"the copy is inside step {Slug}");
    }

    /// <summary>The region holds the JWT and installation-token functions and nothing of the gh and board tooling (that stays outside).</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_MarkedRegion_HoldsTheJwtAndExchangeFunctionsOnly()
    {
        var region = string.Join('\n', OctopusRepository.MarkedRegion(Helper));

        foreach (var function in new[] { "function ConvertTo-Base64Url", "function New-GitHubAppJwt", "function Invoke-GitHubAppApi", "function Get-GitHubAppInstallationToken" })
        {
            region.ShouldContain(function);
        }

        region.ShouldNotContain("Get-GitHubCliToken");
        region.ShouldNotContain("Resolve-GitHubToken");
    }

    /// <summary>OCL heredocs forbid dollar-brace, percent-brace and hash-brace, in the helper region and therefore in the copy.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_MarkedRegionAndStep_HoldNoOclInterpolationSequence()
    {
        var region = string.Join('\n', OctopusRepository.MarkedRegion(Helper));
        var step = OctopusRepository.Steps(OctopusRepository.Read(Process)).Single(candidate => candidate.Slug == Slug).Text;

        foreach (var forbidden in new[] { "${", "%{", "#{" })
        {
            region.Contains(forbidden, StringComparison.Ordinal).ShouldBeFalse($"the helper region contains {forbidden}, which an OCL heredoc forbids; write $($Name) instead");
            step.Contains(forbidden, StringComparison.Ordinal).ShouldBeFalse($"step {Slug} contains {forbidden}, which an OCL heredoc forbids");
        }
    }

    /// <summary>The step has no second implementation: no openssl, no signature or temporary key file, and no inline JWT literals outside the helper copy; it calls the helper with the key in memory.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ReportCommitStatusStep_HasNoOpensslNoTempKeyFileAndNoInlineJwtOutsideTheHelperCopy()
    {
        var step = OctopusRepository.Steps(OctopusRepository.Read(Process)).Single(candidate => candidate.Slug == Slug).Text;
        var lines = step.Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim() == $"# >>> {Helper}");
        var end = Array.FindIndex(lines, line => line.Trim() == $"# <<< {Helper}");
        (start >= 0 && end > start).ShouldBeTrue("the step has no marked copy of the helper");
        var outside = string.Join('\n', lines.Take(start).Concat(lines.Skip(end + 1)));

        step.ShouldNotContain("openssl", Case.Insensitive);
        step.ShouldNotContain("dgst");
        step.ShouldNotContain("GetTempFileName");
        outside.ShouldNotContain("\"alg\"");
        outside.ShouldNotContain("'{\"iat\"");
        outside.ShouldNotContain("Base64");
        outside.ShouldNotContain("/access_tokens");
        outside.ShouldContain("Get-GitHubAppInstallationToken -AppId $appId -Repository @($repository) -Permission @{ statuses = 'write' } -InstallationId $installationId -PrivateKey $key");
        outside.ShouldContain("$ProgressPreference = 'SilentlyContinue'");
        outside.ShouldContain("Fail-Step \"No installation token for GitHub App $appId.\"");
        outside.ShouldContain("Fail-Step 'GitHub.StatusEnabled is True but the sensitive variable GitHub.StatusAppPrivateKey is empty.'");
    }
}
