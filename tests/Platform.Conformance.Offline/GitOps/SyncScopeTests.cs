using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Tests.GitOps;
using YamlDotNet.RepresentationModel;

namespace Platform.Conformance.Offline.GitOps;

/// <summary>
/// CAP-GIT-014: a commit starts an automated sync only in the applications whose manifests it changes. Every workload
/// Application of the tenant chart carries <c>argocd.argoproj.io/manifest-generate-paths</c>, and every directory its
/// Kustomize build reads (the overlay, then its <c>resources</c>, <c>components</c> and <c>bases</c>, transitively) lies
/// under one of those paths, so no real change escapes the annotation. Without it every commit to main started a sync
/// of each environment still at its bootstrap pin (live, 2026-09-24).
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class SyncScopeTests
{
    private const string PathsAnnotation = "argocd.argoproj.io/manifest-generate-paths";
    private static readonly Dictionary<string, string> StandInDomain = new(StringComparer.Ordinal) { ["platform.appsDomain"] = TenantChart.StandInDomain };

    /// <summary>Every workload Application names its paths, and they cover every directory its build reads.</summary>
    [Test]
    [Capability("CAP-GIT-014")]
    public void Should_Render_WorkloadApplications_ScopeSyncsToTheDirectoriesTheirBuildReads()
    {
        var descriptors = TenantChart.Descriptors;
        TenantChart.EnsureHelmAvailable();
        var problems = new List<string>();
        var checkedApplications = 0;

        foreach (var (app, path) in descriptors)
        {
            foreach (var tier in TenantChart.Tiers)
            {
                var render = TenantChart.Render(path, tier, StandInDomain);
                render.ExitCode.ShouldBe(0, $"{app.Name} on {tier}: {render.Error}");
                foreach (var application in render.Objects.Where(IsWorkloadApplication))
                {
                    checkedApplications++;
                    problems.AddRange(Problems(application));
                }
            }
        }

        checkedApplications.ShouldBeGreaterThan(0, "the tenant chart rendered no workload Application");
        problems.ShouldBeEmpty("workload Applications whose sync scope misses a directory their build reads");
    }

    private static bool IsWorkloadApplication(JsonElement item) =>
        GitOpsCluster.Text(item, "kind") == "Application"
        && GitOpsCluster.Text(item, "metadata", "labels", "platform/role") == "workload";

    private static IEnumerable<string> Problems(JsonElement application)
    {
        var name = GitOpsCluster.Text(application, "metadata", "name") ?? "?";
        var annotation = GitOpsCluster.Text(application, "metadata", "annotations", PathsAnnotation);
        if (string.IsNullOrWhiteSpace(annotation))
        {
            yield return $"{name}: no {PathsAnnotation}";
            yield break;
        }

        var source = GitOpsCluster.Text(application, "spec", "source", "path") ?? string.Empty;
        var scopes = annotation.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(scope => scope.StartsWith('/') ? scope.TrimStart('/') : Normalize(Path.Combine(source, scope)))
            .ToArray();
        if (GitOpsCluster.Text(application, "spec", "source", "helm", "releaseName") is not null)
        {
            if (!Covered(source, scopes))
            {
                yield return $"{name}: chart {source} is outside {annotation}";
            }

            yield break;
        }

        foreach (var directory in BuildDirectories(source))
        {
            if (!Covered(directory, scopes))
            {
                yield return $"{name}: {directory} is read by the build but outside {annotation}";
            }
        }
    }

    /// <summary>The directories a Kustomize build of <paramref name="start"/> reads, relative to the repository root.</summary>
    private static IReadOnlyCollection<string> BuildDirectories(string start)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([Normalize(start)]);
        while (pending.TryPop(out var directory))
        {
            if (!seen.Add(directory))
            {
                continue;
            }

            var file = new[] { "kustomization.yaml", "kustomization.yml", "Kustomization" }
                .Select(candidate => Path.Combine(GitOpsRepository.Root, directory, candidate))
                .FirstOrDefault(File.Exists);
            if (file is null)
            {
                continue;
            }

            foreach (var reference in References(file))
            {
                var target = Normalize(Path.Combine(directory, reference));
                pending.Push(Directory.Exists(Path.Combine(GitOpsRepository.Root, target)) ? target : Normalize(Path.GetDirectoryName(target) ?? target));
            }
        }

        return seen;
    }

    /// <summary>Local entries of <c>resources</c>, <c>components</c> and <c>bases</c> (remote URLs are not the repository's).</summary>
    private static IEnumerable<string> References(string kustomizationFile)
    {
        var stream = new YamlStream();
        using (var reader = new StreamReader(kustomizationFile))
        {
            stream.Load(reader);
        }

        if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode root)
        {
            yield break;
        }

        foreach (var key in new[] { "resources", "components", "bases" })
        {
            if (!root.Children.TryGetValue(new YamlScalarNode(key), out var node) || node is not YamlSequenceNode list)
            {
                continue;
            }

            foreach (var entry in list.Children.OfType<YamlScalarNode>().Select(scalar => scalar.Value ?? string.Empty))
            {
                if (entry.Length > 0 && !entry.Contains("://", StringComparison.Ordinal) && !entry.StartsWith("github.com/", StringComparison.Ordinal))
                {
                    yield return entry;
                }
            }
        }
    }

    private static bool Covered(string directory, IEnumerable<string> scopes) =>
        scopes.Any(scope => directory == scope || directory.StartsWith(scope + "/", StringComparison.Ordinal));

    /// <summary>A repository-relative path with forward slashes and no <c>.</c> or <c>..</c> segments.</summary>
    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == ".." && parts.Count > 0)
            {
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }
}
