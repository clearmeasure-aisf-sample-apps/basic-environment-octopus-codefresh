using System.Text.Json.Nodes;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// The scripts that replaced the inline Bash of two platform steps: registry-retention.ps1's testability hook
/// RETENTION_DRY_RUN and its plan folders on the build volume (CAP-CF-010), and ci-dotnet-smoke.ps1, whose failure fails
/// the image build (CAP-CF-013).
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class PlatformStepScriptTests
{
    private static readonly string[] SmokeTools =
        ["dotnet", "docker", "cosign", "syft", "crane", "gitleaks", "helm", "kustomize", "kubeconform", "terraform", "yamllint", "octopus", "az", "sqlcmd", "jq", "git", "dotnet-crap"];

    /// <summary>RETENTION_DRY_RUN=true plans only; -KeepPlans keeps the newest plan folders and never a folder not named like a build.</summary>
    [Test]
    [Capability("CAP-CF-010")]
    public void WhenRetention_DryRunVariableAndKeepPlans_PlansOnlyAndKeepsTheNewestPlanFolders()
    {
        using var harness = PlatformScriptHarness.Create();
        var pins = Directory.CreateDirectory(Path.Combine(harness.Root, "pins", "apps")).Parent!.FullName;
        File.WriteAllText(Path.Combine(pins, "apps", "sandbox.yaml"), "name: sandbox\ndeployables:\n  - name: web\n    images: [web]\n");
        var overlay = Directory.CreateDirectory(Path.Combine(pins, "gitops", "apps", "sandbox", "envs", "tdd", "web")).FullName;
        File.WriteAllText(Path.Combine(overlay, "kustomization.yaml"), "images:\n  - name: acr.example.io/apps/sandbox/web\n    newTag: 1.0.1\n");
        var inventory = Path.Combine(harness.Root, "inventory.json");
        File.WriteAllText(inventory, """
            {"repositories": {"apps/sandbox/web": [
              {"name": "1.0.1", "digest": "sha256:aaaa", "createdTime": "2026-01-01T00:00:00Z"},
              {"name": "pr-5", "digest": "sha256:dddd", "createdTime": "2025-06-01T00:00:00Z"}]}}
            """);
        var parent = Path.Combine(harness.Volume, "retention");
        var now = DateTime.UtcNow;
        for (var index = 0; index < 4; index++)
        {
            Directory.SetLastWriteTimeUtc(Directory.CreateDirectory(Path.Combine(parent, $"5f00000000000000000000{index:D2}")).FullName, now.AddHours(index - 24));
        }

        Directory.SetLastWriteTimeUtc(Directory.CreateDirectory(Path.Combine(parent, "notes")).FullName, now.AddDays(-30));
        var plan = Path.Combine(parent, "6600000000000000000000aa", "plan.json");
        harness.With("RETENTION_DRY_RUN", "true");

        var result = harness.Run("registry-retention.ps1", "-Registry", "acr.example.io", "-PinsRoot", pins, "-PlanFile", plan, "-KeepPlans", "2",
            "-InventoryFile", inventory, "-Now", "2026-09-25T00:00:00Z");

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Error.ShouldContain("registry-retention: RETENTION_DRY_RUN=true: plan only, nothing is deleted");
        var document = JsonNode.Parse(File.ReadAllText(plan))!;
        document["dryRun"]!.GetValue<bool>().ShouldBeTrue();
        document["totals"]!.ToJsonString().ShouldBe("""{"keptTags":1,"deletedDigests":1}""");
        Directory.EnumerateDirectories(parent).Select(Path.GetFileName).ShouldBe(["6600000000000000000000aa", "5f0000000000000000000003", "notes"], ignoreOrder: true);
    }

    /// <summary>Every tool answers in the order of the old step, crap4dotnet with DOTNET_ROLL_FORWARD=LatestMajor, then the pin reminder.</summary>
    [Test]
    [Capability("CAP-CF-013")]
    public void WhenSmoke_EveryToolAnswers_ChecksThemInOrderAndPrintsThePinReminder()
    {
        using var harness = Smoke(out var rollForward);

        var result = harness.Run("ci-dotnet-smoke.ps1");

        result.ExitCode.ShouldBe(0, result.Transcript);
        harness.Calls().Select(call => $"{call.Tool} {string.Join(' ', call.Arguments)}").ShouldBe(
        [
            "dotnet --list-sdks", "docker --version", "cosign version", "syft version", "crane version", "gitleaks version", "helm version --short",
            "kustomize version", "kubeconform -v", "terraform version", "yamllint --version", "octopus version", "az version --output none",
            "jq --version", "git --version", "dotnet-crap --help",
        ]);
        File.ReadAllText(rollForward).ShouldBe("LatestMajor");
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.ShouldContain("9.9.9");
        lines.ShouldContain(Path.Combine(harness.Root, "bin", "sqlcmd"));
        lines.ShouldContain(Path.Combine(harness.Root, "ms-playwright", "chromium-1181"));
        lines.ShouldContain(Path.Combine(harness.Root, "ms-playwright", "chromium_headless_shell-1181"));
        lines[^1].ShouldBe("Smoke passed for platform/ci-dotnet 20260925.0100-abcdef0; pin its tag and digest by commit (see the header).");
    }

    /// <summary>A tool that fails stops the smoke test: exit 1 with the tool's name, and no later tool runs.</summary>
    [Test]
    [Capability("CAP-CF-013")]
    public void WhenSmoke_ToolFails_StopsWithTheToolsName()
    {
        using var harness = Smoke(out _, failing: "cosign");

        var result = harness.Run("ci-dotnet-smoke.ps1");

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Output.ShouldContain("ci-dotnet-smoke: FAIL cosign");
        harness.Calls().Select(call => call.Tool).ShouldBe(["dotnet", "docker", "cosign"]);
    }

    private static PlatformScriptHarness Smoke(out string rollForward, string? failing = null)
    {
        var harness = PlatformScriptHarness.Create(SmokeTools);
        rollForward = Path.Combine(harness.Root, "roll-forward");
        if (failing is not null)
        {
            harness.Route(failing, [], exitCode: 3, stderr: $"{failing}: broken\n");
        }

        foreach (var tool in SmokeTools.Where(tool => tool != "dotnet-crap"))
        {
            harness.Route(tool, [], $"{tool} 1.2.3\n");
        }

        harness.Route("dotnet-crap", ["--help"], "usage\n", run: $"printf '%s' \"$DOTNET_ROLL_FORWARD\" >'{rollForward}'");
        foreach (var browser in new[] { "chromium-1181", "chromium_headless_shell-1181" })
        {
            Directory.CreateDirectory(Path.Combine(harness.Root, "ms-playwright", browser));
        }

        // A stand-in PSScriptAnalyzer module, so the test needs no analyzer of its own.
        var module = Directory.CreateDirectory(Path.Combine(harness.Root, "modules", "PSScriptAnalyzer")).FullName;
        File.WriteAllText(Path.Combine(module, "PSScriptAnalyzer.psd1"), "@{ ModuleVersion = '9.9.9'; GUID = 'a2a4f0bb-0b0c-4b5e-9b6f-2c1a3d4e5f60' }\n");
        harness.With("PSModulePath", Path.Combine(harness.Root, "modules")).With("VERSION", "20260925.0100-abcdef0")
            .With("PLAYWRIGHT_BROWSERS_PATH", Path.Combine(harness.Root, "ms-playwright"));
        return harness;
    }
}
