using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-KIT-003, repository half for the Codefresh Git integration: <c>github-aisf-sample-apps</c> is declared in
/// <c>codefresh/platform/integrations.yaml</c> as a GitHub App (<c>codefresh-github-app</c>, owner-only, verify-only), with
/// no value and no environment variable, and every <c>git:</c> clone step and <c>context:</c> trigger or specTemplate
/// reference to a <c>github-*</c> integration under <c>codefresh/</c> names a declared Git integration.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class GitIntegrationDeclarationTests
{
    private const string GitIntegration = "github-aisf-sample-apps";
    private const string PlatformIntegrations = "codefresh/platform/integrations.yaml";

    /// <summary>The Git integration is a declared, owner-only GitHub App that carries no value.</summary>
    [Test]
    [Capability("CAP-KIT-003")]
    public void Should_ReadIntegrations_GitIntegration_IsAnOwnerOnlyGitHubAppWithoutValue()
    {
        var entries = CodefreshRepository.Items(CodefreshRepository.Get(CodefreshRepository.Load(PlatformIntegrations), "gitIntegrations")).ToArray();

        var entry = entries.SingleOrDefault(candidate => CodefreshRepository.Get(candidate, "name") as string == GitIntegration);

        entry.ShouldNotBeNull($"{PlatformIntegrations} declares no gitIntegrations entry {GitIntegration}");
        CodefreshRepository.Get(entry, "kind").ShouldBe("codefresh-github-app");
        CodefreshRepository.Get(entry, "expectedType").ShouldBe("git.github-app");
        CodefreshRepository.Get(entry, "ownerOnly").ShouldBe("true", "YamlDotNet reads the flag as a string");
        var keys = entry is IDictionary<object, object?> map ? map.Keys.Select(key => key.ToString() ?? string.Empty).ToArray() : [];
        keys.ShouldNotContain("value", "a Git integration holds no value");
        keys.ShouldNotContain("fromEnv", "a Git integration holds no environment variable");
        keys.ShouldNotContain("data");
    }

    /// <summary>Every git: and context: reference to a github-* integration under codefresh/ names a declared Git integration.</summary>
    [Test]
    [Capability("CAP-KIT-003")]
    public void Should_ReadCodefreshYaml_EveryGithubIntegrationReference_NamesADeclaredGitIntegration()
    {
        var declared = CodefreshRepository.Integrations
            .SelectMany(file => CodefreshRepository.Items(CodefreshRepository.Get(CodefreshRepository.Load(file), "gitIntegrations")))
            .Select(entry => CodefreshRepository.Get(entry, "name") as string ?? string.Empty)
            .ToArray();
        var references = Directory.EnumerateFiles(Path.Join(CodefreshRepository.Root, "codefresh"), "*.y*ml", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(CodefreshRepository.Root, path).Replace('\\', '/'))
            .Where(relative => !relative.EndsWith("/integrations.yaml", StringComparison.Ordinal) && !relative.Contains("/tests/", StringComparison.Ordinal))
            .SelectMany(relative => CodefreshRepository.Read(relative).Split('\n')
                .Select(line => GithubReference().Match(line))
                .Where(match => match.Success)
                .Select(match => (File: relative, Name: match.Groups["name"].Value)))
            .ToArray();

        declared.ShouldContain(GitIntegration);
        references.ShouldNotBeEmpty("no git: or context: reference to a github-* integration under codefresh/");
        references.Where(reference => !declared.Contains(reference.Name))
            .Select(reference => $"{reference.File}: {reference.Name}")
            .Distinct()
            .ShouldBeEmpty($"references to a github-* integration that {PlatformIntegrations} does not declare under gitIntegrations");
    }

    [GeneratedRegex(@"^\s*(?:-\s+)?(?:git|context):\s*(?<name>github-[A-Za-z0-9-]+)\s*(?:#.*)?$")]
    private static partial Regex GithubReference();
}
