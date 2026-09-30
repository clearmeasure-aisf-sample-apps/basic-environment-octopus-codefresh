using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-018, offline: the four config-as-code projects (workorders and sandbox through <c>octopusdeploy_project.app</c>,
/// platform-infrastructure, platform-wake) reach the environment repository through the Octopus Deploy GitHub App
/// connection (R3, issue #42), which provider 1.20.0 supports with the block <c>git_github_app_persistence_settings</c>.
/// The stored Git credential stays for the pin commits of the Argo CD image-tag step (issue #58), and an empty connection
/// ID is the documented rollback. The tests read octopus/terraform, octopus/apply.ps1 and the owner runbook as text.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class GitPersistenceTests
{
    private static readonly string[] ProjectResources = ["app", "platform_infrastructure", "platform_wake"];

    /// <summary>
    /// Every project resource has the GitHub App block, gated on the connection ID being set, with the connection ID from
    /// the variable; the credential block exists only in the rollback branch of a dynamic block, and no project resource
    /// writes a static <c>git_library_persistence_settings</c> block or a PAT.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-018")]
    public void Should_Projects_EveryProjectResource_UsesTheGitHubAppConnectionWithARollbackBranch()
    {
        var projects = OctopusRepository.Read("octopus/terraform/projects.tf");

        foreach (var name in ProjectResources)
        {
            var block = ResourceBlock(projects, "octopusdeploy_project", name);

            var app = DynamicBlock(block, "git_github_app_persistence_settings");
            app.ShouldContain("for_each = local.git_use_github_app ? [1] : []", customMessage: name);
            app.ShouldContain("github_connection_id = var.octopus_github_app_connection_id", customMessage: name);
            app.ShouldContain("url                  = var.env_repo_url", customMessage: name);
            app.ShouldContain("default_branch       = \"main\"", customMessage: name);

            var credential = DynamicBlock(block, "git_library_persistence_settings");
            credential.ShouldContain("for_each = local.git_use_github_app ? [] : [1]", customMessage: name);
            credential.ShouldContain("git_credential_id = local.stored_git_credential_id", customMessage: name);

            StaticBlock().IsMatch(block).ShouldBeFalse($"{name} has a static git_library_persistence_settings block: the credential is only the rollback");
            PatAttribute().IsMatch(block).ShouldBeFalse($"{name} sets a username or password of its own");
        }

        projects.ShouldContain("git_use_github_app = var.octopus_github_app_connection_id != \"\"");
        AppBasePath().Matches(projects).Count.ShouldBe(6, "each of the three project resources keeps its base path in both branches");
        projects.ShouldContain("base_path            = \".octopus/apps/${each.value.app}/${each.key}\"");
        projects.ShouldContain("base_path            = \".octopus/platform-infrastructure\"");
        projects.ShouldContain("base_path            = \".octopus/platform-wake\"");
    }

    /// <summary>The variable defaults to the owner's connection, accepts only that ID shape or an empty string, and is in the example file.</summary>
    [Test]
    [Capability("CAP-OCT-018")]
    public void Should_Variable_ConnectionId_DefaultsToTheOwnersConnectionAndValidatesTheShape()
    {
        var variables = OctopusRepository.Read("octopus/terraform/variables.tf");
        var block = ResourceBlock(variables, "variable", "octopus_github_app_connection_id", isVariable: true);

        block.ShouldContain("default     = \"GitHubAppConnections-41\"");
        var pattern = ValidationRegex().Match(block);
        pattern.Success.ShouldBeTrue("the variable validates the connection ID with a regex");
        var shape = new Regex(pattern.Groups["pattern"].Value.Replace("\\\\", "\\", StringComparison.Ordinal), RegexOptions.None, TimeSpan.FromSeconds(2));
        shape.IsMatch("GitHubAppConnections-41").ShouldBeTrue();
        shape.IsMatch("GitHubAppConnections-7").ShouldBeTrue();
        shape.IsMatch("GitHubAppConnections-").ShouldBeFalse();
        shape.IsMatch("GitHubAppConnections-41; drop").ShouldBeFalse();
        shape.IsMatch("Git-credentials-1").ShouldBeFalse();
        block.ShouldContain("var.octopus_github_app_connection_id == \"\" ||", customMessage: "an empty string is the rollback");
        OctopusRepository.Read("octopus/terraform/terraform.tfvars.example").ShouldContain("octopus_github_app_connection_id");
        OctopusRepository.Read("octopus/terraform/outputs.tf").ShouldContain("output \"git_persistence\"");
    }

    /// <summary>The pinned provider release has the GitHub App persistence block (added in provider 1.10.0).</summary>
    [Test]
    [Capability("CAP-OCT-018")]
    public void Should_Provider_PinnedRelease_HasTheGitHubAppPersistenceBlock()
    {
        var versions = OctopusRepository.Read("octopus/terraform/versions.tf");
        var pin = PinnedVersion().Match(versions);

        pin.Success.ShouldBeTrue("octopusdeploy is pinned to an exact version");
        Version.Parse(pin.Groups["version"].Value).ShouldBeGreaterThanOrEqualTo(new Version(1, 10, 0), "git_github_app_persistence_settings needs provider 1.10.0 or later");
    }

    /// <summary>
    /// The drift check requires the connection on every project, the protected-branch check reads both block kinds, and the
    /// stored-credential lookup stays (the Argo CD image-tag step still picks a credential).
    /// </summary>
    [Test]
    [Capability("CAP-OCT-018")]
    public void Should_Checks_DriftAndProtectedBranches_CoverTheGitHubAppBlock()
    {
        var projects = OctopusRepository.Read("octopus/terraform/projects.tf");

        var drift = ResourceBlock(projects, "check", "projects_git_via_github_app", isCheck: true);
        drift.ShouldContain("!local.git_use_github_app ||");
        drift.ShouldContain("values(octopusdeploy_project.app)");
        drift.ShouldContain("octopusdeploy_project.platform_infrastructure");
        drift.ShouldContain("octopusdeploy_project.platform_wake");
        drift.ShouldContain("github_connection_id == var.octopus_github_app_connection_id");

        var protectedBranches = ResourceBlock(projects, "check", "no_octopus_protected_branches", isCheck: true);
        protectedBranches.ShouldContain("p.git_github_app_persistence_settings");
        protectedBranches.ShouldContain("p.git_library_persistence_settings");
    }

    /// <summary>The stored credential is looked up and its R3 restriction check stays: it still writes the pin commits.</summary>
    [Test]
    [Capability("CAP-OCT-018")]
    public void Should_StoredCredential_PinCommits_KeepsItsRestrictionCheck()
    {
        var projects = OctopusRepository.Read("octopus/terraform/projects.tf");

        projects.ShouldContain("data \"octopusdeploy_git_credentials\" \"stored\"");
        ResourceBlock(projects, "check", "stored_git_credential_restricted", isCheck: true).ShouldContain("repository_restrictions");
        OctopusRepository.Read("octopus/terraform/variables.tf").ShouldContain("pin commits");
    }

    /// <summary>
    /// octopus/apply.ps1 reports, per project, the Git persistence switch of the plan, and a delete or replace of a
    /// project (which the switch must never be) still stops the run.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-018")]
    public void Should_ApplyScript_PlanReport_NamesTheGitPersistenceSwitchAndStillRefusesProjectDeletes()
    {
        var apply = OctopusRepository.Read("octopus/apply.ps1");

        apply.ShouldContain("git_github_app_persistence_settings = 'GitHub App connection'");
        apply.ShouldContain("git_library_persistence_settings = 'Git credential'");
        apply.ShouldContain("$change['type'] -cne 'octopusdeploy_project'");
        apply.ShouldContain("Git persistence of $($change['address'])");
        apply.ShouldContain("$replaceable = @('octopusdeploy_scoped_user_role', 'octopusdeploy_variable')");
        apply.ShouldContain("if ($actions -ccontains 'delete' -and $change['type'] -cnotin $replaceable)");
    }

    /// <summary>
    /// The owner runbook states the provider finding, the OWNER-ONLY steps, the main-protection bypass actor with its
    /// verification, and the rollback; the other docs link to it, and a follow-up issue holds the pin-commit gap.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-018")]
    public void Should_Runbook_MainProtectionBypass_IsOwnerOnlyWithAVerification()
    {
        var runbook = OctopusRepository.Read("docs/runbooks/octopus-github-app-git.md");

        runbook.ShouldContain("git_github_app_persistence_settings");
        runbook.ShouldContain("1.20.0");
        runbook.ShouldContain("(OWNER-ONLY; never edited by an agent)");
        runbook.ShouldContain("`main-protection`");
        runbook.ShouldContain("Octopus Deploy GitHub App");
        runbook.ShouldContain("the pin commit lands");
        runbook.ShouldContain("no `protected branch`, `rule violations` or `bypass` error");
        runbook.ShouldContain("octopus_github_app_connection_id = \"\"");
        runbook.ShouldContain("#58");
        OctopusRepository.Read("docs/runbooks/credential-rotation.md").ShouldContain("octopus-github-app-git.md");
        OctopusRepository.Read("docs/owner/public-repo-checklist.md").ShouldContain("octopus-github-app-git.md");
    }

    /// <summary>The text of the resource, variable or check block with the given labels, comments removed.</summary>
    private static string ResourceBlock(string terraform, string type, string name, bool isVariable = false, bool isCheck = false)
    {
        var header = isVariable || isCheck ? $"{type} \"{name}\" {{" : $"resource \"{type}\" \"{name}\" {{";
        var start = terraform.IndexOf(header, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"{header} not found");
        return WithoutComments(BalancedFrom(terraform, terraform.IndexOf('{', start)));
    }

    /// <summary>The <c>dynamic "name" { ... }</c> block inside a resource block.</summary>
    private static string DynamicBlock(string resource, string name)
    {
        var header = $"dynamic \"{name}\" {{";
        var start = resource.IndexOf(header, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"{header} not found");
        return BalancedFrom(resource, resource.IndexOf('{', start));
    }

    private static string BalancedFrom(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            depth += text[index] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return text[open..(index + 1)];
            }
        }

        throw new InvalidOperationException("unbalanced braces");
    }

    private static string WithoutComments(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    [GeneratedRegex(@"^\s*git_library_persistence_settings\s*\{", RegexOptions.Multiline)]
    private static partial Regex StaticBlock();

    [GeneratedRegex(@"^\s*(password|username)\s*=", RegexOptions.Multiline)]
    private static partial Regex PatAttribute();

    [GeneratedRegex(@"base_path\s+= ""\.octopus/")]
    private static partial Regex AppBasePath();

    [GeneratedRegex(@"regex\(""(?<pattern>[^""]+)"", var\.octopus_github_app_connection_id\)")]
    private static partial Regex ValidationRegex();

    [GeneratedRegex(@"source\s+= ""OctopusDeploy/octopusdeploy""\s+version\s+= ""(?<version>\d+\.\d+\.\d+)""")]
    private static partial Regex PinnedVersion();
}
