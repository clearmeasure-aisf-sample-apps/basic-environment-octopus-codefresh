using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// CAP-KIT-001: the onboarding tool scaffolds a valid app from every starter. In a temporary copy, each starter of each
/// role is used at least once: <c>new</c>, <c>scaffold</c>, <c>render</c> and <c>check</c> must all succeed, and every
/// scaffolded Kustomize folder must build when kustomize is installed.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class OnboardingToolTests
{
    [GeneratedRegex(@"apps/(?:<app>|__APP__)/(?<image>[a-z][a-z0-9-]*[a-z0-9])")]
    private static partial Regex StarterImage();

    [GeneratedRegex(@"^(\s*chart:\s*).*$", RegexOptions.Multiline)]
    private static partial Regex ChartLine();

    [Test]
    [Capability("CAP-KIT-001")]
    public void Should_NewScaffoldRenderCheck_EveryStarter_ChecksClean()
    {
        using var workspace = KitWorkspace.Create(withStarters: true);
        var codefresh = workspace.Starters("codefresh/templates");
        var octopus = workspace.Starters("octopus/templates");
        var gitops = workspace.Starters("gitops/templates");
        var deployStarters = octopus.Where(starter => File.Exists(workspace.PathOf($"octopus/templates/{starter}/deployment_process.ocl"))).ToArray();
        var addOns = octopus.Except(deployStarters).ToArray();
        TestContext.Out.WriteLine($"starters: codefresh [{string.Join(", ", codefresh)}], octopus [{string.Join(", ", octopus)}], gitops [{string.Join(", ", gitops)}]; fixture starters for: [{string.Join(", ", workspace.FixtureRoles)}]");

        codefresh.ShouldNotBeEmpty();
        deployStarters.ShouldNotBeEmpty();
        gitops.ShouldNotBeEmpty();
        var count = new[] { codefresh.Count, deployStarters.Length, gitops.Count, addOns.Length > 0 ? 1 : 0 }.Max();
        Assert.Multiple(() =>
        {
            for (var index = 0; index < count; index++)
            {
                var deploy = deployStarters[index % deployStarters.Length];
                var database = deploy.Contains("db", StringComparison.Ordinal) || (addOns.Length > 0 && index == count - 1);
                RunCase(workspace, $"kitapp{index}", codefresh[index % codefresh.Count], database ? [deploy, .. addOns] : [deploy], gitops[index % gitops.Count], database);
            }
        });
    }

    private static void RunCase(KitWorkspace workspace, string app, string codefresh, string[] octopus, string gitops, bool database)
    {
        var label = $"{app} (codefresh {codefresh}, octopus {string.Join('+', octopus)}, gitops {gitops}, database {database})";
        var starterRoot = workspace.PathOf($"gitops/templates/{gitops}");
        var packaging = PackagingOf(gitops, starterRoot);
        var images = ImagesOf(starterRoot);
        string[] newArgs = ["new", app, "--repo", $"clearmeasure-aisf-sample-apps/kit-fixture-{app}", "--packaging", packaging, "--images", string.Join(',', images)];
        var created = workspace.Onboarding(database ? [.. newArgs, "--database", "mssql-2022-express"] : newArgs);
        created.ExitCode.ShouldBe(0, $"new {label}: {created.Transcript}");
        var scaffolded = workspace.Onboarding("scaffold", app, "--codefresh", codefresh, "--octopus", string.Join(',', octopus), "--gitops", gitops);
        scaffolded.ExitCode.ShouldBe(0, $"scaffold {label}: {scaffolded.Transcript}");
        if (packaging == "helm")
        {
            PointDescriptorAtScaffoldedChart(workspace, app);
        }

        var rendered = workspace.Onboarding("render", app);
        rendered.ExitCode.ShouldBe(0, $"render {label}: {rendered.Transcript}");
        rendered.Output.ShouldContain($"app: {app}");
        var check = workspace.Onboarding("check", app);
        check.ExitCode.ShouldBe(0, $"check {label}: {check.Transcript}");
        BuildKustomizeFolders(workspace, app, label);
    }

    private static string PackagingOf(string starter, string starterRoot) => starter switch
    {
        "kustomize" or "helm" or "raw" => starter,
        _ when Directory.EnumerateFiles(starterRoot, "Chart.yaml", SearchOption.AllDirectories).Any() => "helm",
        _ when Directory.EnumerateFiles(starterRoot, "kustomization.yaml", SearchOption.AllDirectories).Any() => "kustomize",
        _ => "raw",
    };

    private static string[] ImagesOf(string starterRoot)
    {
        var images = Directory.EnumerateFiles(starterRoot, "*", SearchOption.AllDirectories)
            .SelectMany(file => StarterImage().Matches(File.ReadAllText(file)).Select(match => match.Groups["image"].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return images.Length > 0 ? images : ["web"];
    }

    private static void PointDescriptorAtScaffoldedChart(KitWorkspace workspace, string app)
    {
        var appRoot = workspace.PathOf($"gitops/apps/{app}");
        var chart = Directory.EnumerateFiles(appRoot, "Chart.yaml", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .FirstOrDefault(directory => !Path.GetRelativePath(appRoot, directory).StartsWith("envs", StringComparison.Ordinal));
        chart.ShouldNotBeNull($"the Helm starter scaffolded no chart under gitops/apps/{app}/");
        var descriptor = workspace.PathOf($"apps/{app}.yaml");
        var relative = Path.GetRelativePath(appRoot, chart).Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(descriptor, ChartLine().Replace(File.ReadAllText(descriptor), match => match.Groups[1].Value + relative, 1));
    }

    private static void BuildKustomizeFolders(KitWorkspace workspace, string app, string label)
    {
        var kustomize = KitToolbox.Find("kustomize", "KUSTOMIZE");
        var envs = workspace.PathOf($"gitops/apps/{app}/envs");
        if (kustomize is null || !Directory.Exists(envs))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(envs).SelectMany(Directory.EnumerateDirectories).Where(folder => File.Exists(Path.Combine(folder, "kustomization.yaml"))))
        {
            var build = KitToolbox.Run(kustomize, ["build", folder], workspace.Root);
            build.ExitCode.ShouldBe(0, $"kustomize build {Path.GetRelativePath(workspace.Root, folder)} for {label}: {build.Error}");
        }
    }
}
