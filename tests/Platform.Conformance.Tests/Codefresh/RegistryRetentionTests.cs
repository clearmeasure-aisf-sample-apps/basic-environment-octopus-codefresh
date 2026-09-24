using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-010: retention never deletes a pinned version, its referrers or the last 10 releases. Observed on a dry-run
/// plan of codefresh/platform/scripts/registry-retention.ps1 (the script of platform-env/registry-retention) against the
/// live tag inventory of every app repository and the pins under gitops/apps/*/envs/** of this checkout. The test runs
/// the script itself with <c>-InventoryFile</c> (a dry run): with one build at a time, the pipeline cannot run while
/// this build runs.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public partial class RegistryRetentionTests : CodefreshCapabilityTestBase
{
    private const int KeepReleases = 10;

    /// <summary>The plan keeps every pinned tag, the referrers of kept digests, the newest ten releases and the fixture.</summary>
    [Test]
    [Capability("CAP-CF-010")]
    [Category(Categories.Build)]
    [CancelAfter(15 * 60 * 1000)]
    public async Task Should_RetentionPlan_LiveInventory_KeepsPinnedAndRecentVersions()
    {
        var registry = RequireRegistry("the retention test");
        var pwsh = FindPwsh();
        var root = CodefreshPlatform.RepositoryRoot;
        var pins = CodefreshPlatform.ReadPins().Where(pin => !pin.IsBootstrap).DistinctBy(pin => pin.ToString()).ToArray();
        var inventory = new Dictionary<string, JsonElement[]>(StringComparer.Ordinal);
        foreach (var repository in Repositories(root, pins))
        {
            var tags = await registry.ListTagsAsync(repository, Token);
            if (tags.Count > 0)
            {
                inventory[repository] = tags.Select(tag => tag.Record).ToArray();
            }
        }

        var folder = Directory.CreateTempSubdirectory("retention-").FullName;
        Cleanup.Register($"delete {folder}", _ =>
        {
            Directory.Delete(folder, recursive: true);
            return Task.CompletedTask;
        });
        var inventoryFile = Path.Combine(folder, "inventory.json");
        var planFile = Path.Combine(folder, "plan.json");
        await File.WriteAllTextAsync(inventoryFile, JsonSerializer.Serialize(new { repositories = inventory }), Token);

        var exit = await RunAsync(pwsh, root, registry.LoginServer, inventoryFile, planFile);

        exit.ExitCode.ShouldBe(0, exit.Transcript);
        AttachArtifact("retention-plan.json", await File.ReadAllTextAsync(planFile, Token));
        using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planFile, Token));
        var problems = new List<string>();
        foreach (var repository in plan.RootElement.GetProperty("repositories").EnumerateArray())
        {
            problems.AddRange(Check(repository, pins));
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    private static IEnumerable<string> Check(JsonElement repository, IReadOnlyList<PinnedImage> pins)
    {
        var name = repository.GetProperty("repository").GetString()!;
        var kept = repository.GetProperty("keep").EnumerateArray()
            .Select(item => (Tag: item.GetProperty("tag").GetString()!, Digest: item.GetProperty("digest").GetString()!))
            .ToArray();
        var deleted = repository.GetProperty("delete").EnumerateArray()
            .Select(item => (Digest: item.GetProperty("digest").GetString()!, Tags: item.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToArray()))
            .ToArray();
        var keptTags = kept.Select(item => item.Tag).ToHashSet(StringComparer.Ordinal);
        var keptDigests = kept.Where(item => !Referrer().IsMatch(item.Tag)).Select(item => item.Digest).ToHashSet(StringComparer.Ordinal);
        var allTags = kept.Select(item => item.Tag).Concat(deleted.SelectMany(item => item.Tags)).ToArray();

        foreach (var pin in pins.Where(pin => pin.Repository == name && allTags.Contains(pin.Tag)))
        {
            if (!keptTags.Contains(pin.Tag))
            {
                yield return $"{pin} is pinned in {pin.File} but planned for deletion";
            }
        }

        foreach (var (digest, tags) in deleted)
        {
            if (keptDigests.Contains(digest))
            {
                yield return $"{name}@{digest} is kept by one tag and deleted with {string.Join(", ", tags)}";
            }

            foreach (var tag in tags.Where(tag => Referrer().Match(tag) is { Success: true } match && keptDigests.Contains($"sha256:{match.Groups["subject"].Value}")))
            {
                yield return $"{name}:{tag} is a referrer of a kept digest but planned for deletion";
            }
        }

        var releases = allTags.Where(tag => Release().IsMatch(tag)).OrderByDescending(Version.Parse).Take(KeepReleases);
        foreach (var release in releases.Where(release => !keptTags.Contains(release)))
        {
            yield return $"{name}:{release} is one of the newest {KeepReleases} releases but planned for deletion";
        }

        if ($"{name}:0.0.0-fixture" == CodefreshPlatform.UnsignedFixture && allTags.Contains("0.0.0-fixture") && !keptTags.Contains("0.0.0-fixture"))
        {
            yield return $"{CodefreshPlatform.UnsignedFixture} is planned for deletion";
        }
    }

    private static IEnumerable<string> Repositories(string root, IEnumerable<PinnedImage> pins)
    {
        var repositories = new SortedSet<string>(StringComparer.Ordinal) { CodefreshPlatform.UnsignedFixture.Split(':')[0] };
        foreach (var pin in pins)
        {
            repositories.Add(pin.Repository);
        }

        foreach (var descriptor in Directory.EnumerateFiles(Path.Combine(root, "apps"), "*.yaml"))
        {
            var text = File.ReadAllText(descriptor);
            var app = DescriptorName().Match(text) is { Success: true } match ? match.Groups["name"].Value : null;
            if (app is null)
            {
                continue;
            }

            foreach (var image in DescriptorImages().Matches(text).SelectMany(list => list.Groups["list"].Value.Split(',')).Select(item => item.Trim().Trim('"', '\'')).Where(item => item.Length > 0))
            {
                repositories.Add($"apps/{app}/{image}");
                repositories.Add($"apps-previews/{app}/{image}");
            }
        }

        return repositories;
    }

    private static string FindPwsh()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (InsideCodefresh)
        {
            Assert.Fail("pwsh is missing from the step image; platform/ci-dotnet ships it");
        }

        throw new PlatformPrerequisiteException("Prerequisites missing for the retention test: pwsh (PowerShell 7) is not on PATH.");
    }

    private static async Task<(int ExitCode, string Transcript)> RunAsync(string pwsh, string root, string loginServer, string inventoryFile, string planFile)
    {
        var start = new ProcessStartInfo(pwsh)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-File", Path.Combine(root, "codefresh", "platform", "scripts", "registry-retention.ps1"),
            "-Registry", loginServer, "-PinsRoot", root, "-InventoryFile", inventoryFile, "-PlanFile", planFile,
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        return (process.ExitCode, $"exit {process.ExitCode}: {await output}{await error}".Trim());
    }

    [GeneratedRegex(@"^sha256-(?<subject>[0-9a-f]{64})\.(sig|att|sbom)$")]
    private static partial Regex Referrer();

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")]
    private static partial Regex Release();

    [GeneratedRegex(@"^name:\s*(?<name>[a-z][a-z0-9-]*)", RegexOptions.Multiline)]
    private static partial Regex DescriptorName();

    [GeneratedRegex(@"^\s*-?\s*images:\s*\[(?<list>[^\]]*)\]", RegexOptions.Multiline)]
    private static partial Regex DescriptorImages();
}
