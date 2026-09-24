using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Platform.Onboarding.Descriptors;
using Platform.Onboarding.Naming;
using Platform.Onboarding.Output;
using Platform.Onboarding.Repository;

namespace Platform.Onboarding.Checks;

/// <summary>
/// Checks an app's own files against its descriptor: the scaffold is complete (pipelines and specs, one OCL folder per
/// Octopus project, one GitOps folder per deployable and environment, the database folder), the pins have the shape of
/// their packaging, and no app file names another app's registry path, namespaces, stores, vaults or resource groups.
/// </summary>
internal sealed partial class AppFilesCheck
{
    private readonly PlatformRepository repository;

    /// <summary>Creates the check.</summary>
    /// <param name="repository">The repository.</param>
    public AppFilesCheck(PlatformRepository repository)
    {
        this.repository = repository;
    }

    [GeneratedRegex(@"^release(-[a-z0-9-]+)?$")]
    private static partial Regex ReleasePipelineName();

    [GeneratedRegex(@"<acr-name>\.azurecr\.io/(?<path>[a-z0-9][a-z0-9._/-]*)")]
    private static partial Regex RegistryReference();

    /// <summary>Checks one app.</summary>
    /// <param name="descriptor">The app.</param>
    /// <param name="otherApps">Every other app name in the repository.</param>
    public IReadOnlyList<Finding> Check(AppDescriptor descriptor, IReadOnlyCollection<string> otherApps)
    {
        var findings = new List<Finding>();
        CheckCodefresh(descriptor, findings);
        CheckOctopus(descriptor, findings);
        CheckGitOps(descriptor, findings);
        CheckCrossApp(descriptor, otherApps, findings);
        return findings;
    }

    private void CheckCodefresh(AppDescriptor descriptor, List<Finding> findings)
    {
        var app = descriptor.Name;
        var pipelines = $"codefresh/apps/{app}/pipelines";
        if (YamlFiles(pipelines).Count == 0)
        {
            findings.Add(Finding.Error("files", app, "no pipeline YAML; scaffold a Codefresh starter (--codefresh)", pipelines + "/"));
        }

        var specsFolder = $"codefresh/apps/{app}/specs";
        var specs = YamlFiles(specsFolder);
        if (specs.Count == 0)
        {
            findings.Add(Finding.Error("files", app, "no pipeline spec; scaffold a Codefresh starter (--codefresh)", specsFolder + "/"));
            return;
        }

        var projectsWithSpecs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var document = YamlJson.Parse(File.ReadAllText(repository.Full(spec)));
            if (document.Error is not null)
            {
                findings.Add(Finding.Error("files", app, document.Error, spec, document.ErrorLine));
                continue;
            }

            var fullName = (document.Root?["metadata"]?["name"] as JsonValue)?.ToString();
            if (string.IsNullOrWhiteSpace(fullName) || !fullName.Contains('/', StringComparison.Ordinal))
            {
                findings.Add(Finding.Error("files", app, "metadata.name must be '<project>/<pipeline>'", spec, document.LineOf("/metadata")));
                continue;
            }

            var project = fullName[..fullName.IndexOf('/', StringComparison.Ordinal)];
            var pipeline = fullName[(fullName.IndexOf('/', StringComparison.Ordinal) + 1)..];
            projectsWithSpecs.Add(project);
            if (!descriptor.CodefreshProjects.Contains(project, StringComparer.Ordinal))
            {
                findings.Add(Finding.Error("files", app, $"project '{project}' is not in codefresh.projects of apps/{app}.yaml", spec, document.LineOf("/metadata/name")));
            }

            if (pipeline.StartsWith("release", StringComparison.Ordinal) && !ReleasePipelineName().IsMatch(pipeline))
            {
                findings.Add(Finding.Error("files", app, $"release pipelines are named 'release' or 'release-<x>' (the prod signer rule); '{pipeline}' is not", spec, document.LineOf("/metadata/name")));
            }
        }

        foreach (var project in descriptor.CodefreshProjects.Where(project => !projectsWithSpecs.Contains(project)))
        {
            findings.Add(Finding.Error("files", app, $"Codefresh project '{project}' has no spec", specsFolder + "/"));
        }
    }

    private void CheckOctopus(AppDescriptor descriptor, List<Finding> findings)
    {
        foreach (var project in descriptor.OctopusProjects)
        {
            var folder = $".octopus/apps/{descriptor.Name}/{project.Name}";
            var full = repository.Full(folder);
            if (!Directory.Exists(full) || !Directory.EnumerateFiles(full, "*.ocl", SearchOption.AllDirectories).Any())
            {
                findings.Add(Finding.Error("files", descriptor.Name, $"no OCL for Octopus project '{project.Name}'; scaffold an Octopus starter (--octopus)", folder + "/"));
            }
        }
    }

    private void CheckGitOps(AppDescriptor descriptor, List<Finding> findings)
    {
        var app = descriptor.Name;
        foreach (var deployable in descriptor.Deployables)
        {
            if (deployable.Helm is { } helm && !File.Exists(repository.Full($"gitops/apps/{app}/{helm.Chart}/Chart.yaml")))
            {
                findings.Add(Finding.Error("files", app, $"deployable '{deployable.Name}': Helm chart missing (vendor it in Git, ADR-IR34 decision 7)", $"gitops/apps/{app}/{helm.Chart}/Chart.yaml"));
            }

            foreach (var environment in descriptor.Environments)
            {
                var folder = $"gitops/apps/{app}/envs/{environment}/{deployable.Name}";
                if (!Directory.Exists(repository.Full(folder)))
                {
                    findings.Add(Finding.Error("files", app, $"deployable '{deployable.Name}' has no {environment} folder; scaffold a GitOps starter (--gitops)", folder + "/"));
                    continue;
                }

                switch (deployable.Packaging)
                {
                    case "kustomize":
                        CheckKustomizePins(descriptor, deployable, $"{folder}/kustomization.yaml", findings);
                        break;
                    case "helm":
                        if (!File.Exists(repository.Full($"{folder}/values.yaml")))
                        {
                            findings.Add(Finding.Error("pin", app, "Helm deployables pin in values.yaml (argo.octopus.com/image-replace-paths)", $"{folder}/values.yaml"));
                        }

                        break;
                    default:
                        CheckRawPins(descriptor, deployable, folder, findings);
                        break;
                }
            }
        }

        if (descriptor.Database is not null)
        {
            foreach (var environment in descriptor.Environments)
            {
                var kustomization = $"gitops/apps/{app}/envs/{environment}/db/kustomization.yaml";
                if (!File.Exists(repository.Full(kustomization)))
                {
                    findings.Add(Finding.Error("files", app, $"the database needs its {environment} folder (Application {PlatformNames.Application(app, PlatformNames.DatabaseDeployable, environment)})", kustomization));
                }
            }
        }
    }

    private void CheckKustomizePins(AppDescriptor descriptor, Deployable deployable, string path, List<Finding> findings)
    {
        var app = descriptor.Name;
        if (!File.Exists(repository.Full(path)))
        {
            findings.Add(Finding.Error("pin", app, "Kustomize deployables pin images[].newTag in kustomization.yaml", path));
            return;
        }

        var document = YamlJson.Parse(File.ReadAllText(repository.Full(path)));
        if (document.Error is not null)
        {
            findings.Add(Finding.Error("pin", app, document.Error, path, document.ErrorLine));
            return;
        }

        var declared = deployable.Images.ToDictionary(image => PlatformNames.ImageReference(app, image), image => image, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var images = document.Root?["images"] as JsonArray ?? [];
        for (var index = 0; index < images.Count; index++)
        {
            if (images[index] is not JsonObject entry || (entry["name"] as JsonValue)?.ToString() is not { } name)
            {
                findings.Add(Finding.Error("pin", app, "every images[] entry needs a name", path, document.LineOf($"/images/{index}")));
                continue;
            }

            var line = document.LineOf($"/images/{index}");
            if (entry.ContainsKey("digest"))
            {
                findings.Add(Finding.Error("pin", app, $"'{name}' pins a digest; pins are tags only (V3)", path, line));
            }

            if (entry.ContainsKey("newName"))
            {
                findings.Add(Finding.Error("pin", app, $"'{name}' sets newName; the image name is fixed by the descriptor", path, line));
            }

            if (!declared.ContainsKey(name))
            {
                findings.Add(Finding.Error("pin", app, $"'{name}' is not an image of deployable '{deployable.Name}' ({string.Join(", ", declared.Keys)})", path, line));
                continue;
            }

            seen.Add(name);
            if (entry["newTag"] is not JsonValue tag || string.IsNullOrWhiteSpace(tag.ToString()) || tag.ToString() == "latest")
            {
                findings.Add(Finding.Error("pin", app, $"'{name}' needs a newTag (a version or 0.0.0-bootstrap; never latest)", path, line));
            }
        }

        foreach (var missing in declared.Keys.Where(name => !seen.Contains(name)))
        {
            findings.Add(Finding.Error("pin", app, $"images[] lacks '{missing}' (declared in apps/{app}.yaml)", path));
        }
    }

    private void CheckRawPins(AppDescriptor descriptor, Deployable deployable, string folder, List<Finding> findings)
    {
        var app = descriptor.Name;
        var files = YamlFiles(folder);
        if (files.Count == 0)
        {
            findings.Add(Finding.Error("pin", app, "raw deployables need at least one manifest", folder + "/"));
            return;
        }

        var declared = deployable.Images.Select(image => PlatformNames.Repository(app, image)).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(repository.Full(file)))
            {
                lineNumber++;
                foreach (Match match in RegistryReference().Matches(line))
                {
                    var repositoryPath = match.Groups["path"].Value.Split(':', '@')[0];
                    if (!declared.Contains(repositoryPath))
                    {
                        findings.Add(Finding.Error("pin", app, $"'{repositoryPath}' is not an image of deployable '{deployable.Name}'", file, lineNumber));
                    }
                }
            }
        }
    }

    private void CheckCrossApp(AppDescriptor descriptor, IReadOnlyCollection<string> otherApps, List<Finding> findings)
    {
        var others = otherApps.Where(other => other != descriptor.Name).ToArray();
        if (others.Length == 0)
        {
            return;
        }

        var names = Alternation(others);
        var pattern = new Regex(
            @"(?<![A-Za-z0-9_-])(?:apps(?:-previews)?/(?<a>" + names + @")/|(?:kv|rg-app)-(?<b>" + names + @")-|(?:tenant|app)-(?<d>" + names + @")(?![A-Za-z0-9])|(?<c>" + names + @")-(?:tdd|uat|prod)(?![A-Za-z0-9-]))",
            RegexOptions.CultureInvariant);
        foreach (var root in PlatformRepository.AppScopedRoots(descriptor.Name).Append(PlatformRepository.DescriptorPath(descriptor.Name)))
        {
            foreach (var file in TextFiles(root))
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(repository.Full(file)))
                {
                    lineNumber++;
                    if (IsCommentOrAllowed(line))
                    {
                        continue;
                    }

                    var match = pattern.Match(line);
                    if (match.Success)
                    {
                        var other = new[] { "a", "b", "c", "d" }.Select(group => match.Groups[group].Value).First(value => value.Length > 0);
                        findings.Add(Finding.Error("cross-app", descriptor.Name, $"references app '{other}' ('{match.Value}'); an app file names only its own registry path, namespaces, stores, vaults and resource groups", file, lineNumber));
                    }
                }
            }
        }
    }

    private static string Alternation(IEnumerable<string> names) => string.Join('|', names.Select(Regex.Escape));

    /// <summary>
    /// A whole-line comment (shell, YAML, OCL, C#, SQL, XML) or a line with the name-lint marker. Prose may mention
    /// another app, as the platform name lint (TB22) allows; only references in code and manifests count.
    /// </summary>
    /// <param name="line">One line of an app file.</param>
    private static bool IsCommentOrAllowed(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith('#')
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("--", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith("* ", StringComparison.Ordinal)
            || trimmed.StartsWith("<!--", StringComparison.Ordinal)
            || line.Contains("name-lint: allow", StringComparison.Ordinal);
    }

    private List<string> YamlFiles(string folder)
    {
        var full = repository.Full(folder);
        return Directory.Exists(full)
            ? Directory.EnumerateFiles(full, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(repository.Relative)
                .ToList()
            : [];
    }

    private IEnumerable<string> TextFiles(string root)
    {
        var full = repository.Full(root);
        if (File.Exists(full))
        {
            yield return repository.Relative(full);
            yield break;
        }

        if (!Directory.Exists(full))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (IsProbablyText(path))
            {
                yield return repository.Relative(path);
            }
        }
    }

    private static bool IsProbablyText(string path)
    {
        var buffer = new byte[4096];
        using var stream = File.OpenRead(path);
        var read = stream.Read(buffer, 0, buffer.Length);
        if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(buffer, 0, read);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return read == buffer.Length;
        }
    }
}
