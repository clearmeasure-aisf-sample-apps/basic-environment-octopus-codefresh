using System.Text;
using Platform.Onboarding.Descriptors;
using Platform.Onboarding.Naming;
using Platform.Onboarding.Output;
using Platform.Onboarding.Repository;

namespace Platform.Onboarding.Scaffolding;

/// <summary>Which starters to copy for an app.</summary>
/// <param name="CodefreshStarter">Starter under <c>codefresh/templates/</c>, or <c>null</c>.</param>
/// <param name="OctopusStarters">Starters under <c>octopus/templates/</c>, layered in order; a text file that two layers share is appended.</param>
/// <param name="GitopsStarter">Starter under <c>gitops/templates/</c>, or <c>null</c>.</param>
internal sealed record ScaffoldRequest(string? CodefreshStarter, IReadOnlyList<string> OctopusStarters, string? GitopsStarter);

/// <summary>One file the scaffold writes.</summary>
/// <param name="Source">Absolute path of the starter file, or <c>null</c> for a file the kit generates.</param>
/// <param name="Target">Repository-relative target path.</param>
/// <param name="Tokens">Token values for the file's content.</param>
/// <param name="Content">Content of a generated file.</param>
internal sealed record PlannedFile(string? Source, string Target, IReadOnlyDictionary<string, string> Tokens, string? Content = null);

/// <summary>The files a scaffold writes, and problems found while planning.</summary>
/// <param name="Files">Planned files, in order; text files with the same target are appended in order (layered starters).</param>
/// <param name="Findings">Problems, such as an unknown starter.</param>
internal sealed record ScaffoldPlan(IReadOnlyList<PlannedFile> Files, IReadOnlyList<Finding> Findings);

/// <summary>
/// Copies starters into an app's own folders once ("scaffold, then own"; directive §9): Codefresh starters to
/// <c>codefresh/apps/&lt;app&gt;/</c>, Octopus starters to <c>.octopus/apps/&lt;app&gt;/&lt;project&gt;/</c> for every project, and
/// GitOps starters to <c>gitops/apps/&lt;app&gt;/</c>. Tokens are replaced in paths and text contents
/// (contracts <c>starters.tokens</c>).
/// </summary>
internal sealed class Scaffolder
{
    /// <summary>Starter roles and their folders.</summary>
    public static IReadOnlyDictionary<string, string> StarterRoots { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["codefresh"] = "codefresh/templates",
        ["octopus"] = "octopus/templates",
        ["gitops"] = "gitops/templates",
    };

    private const string EnvSegment = "<env>";
    private const string EnvAlias = "__ENV__";
    private const string DeployableSegment = "<deployable>";
    private const string DeployableAlias = "__DEPLOYABLE__";
    private static readonly string[] SkippedStarterFiles = ["README.md"];

    private readonly PlatformRepository repository;

    /// <summary>Creates a scaffolder for a repository.</summary>
    /// <param name="repository">The repository whose starters are copied.</param>
    public Scaffolder(PlatformRepository repository)
    {
        this.repository = repository;
    }

    /// <summary>Starter names of a role, sorted; empty when the role's templates folder is absent.</summary>
    /// <param name="role">codefresh, octopus or gitops.</param>
    public IReadOnlyList<string> Starters(string role)
    {
        var root = repository.Full(StarterRoots[role]);
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    /// <summary>Plans the files of a scaffold without writing anything.</summary>
    /// <param name="descriptor">The app.</param>
    /// <param name="request">The starters.</param>
    public ScaffoldPlan Plan(AppDescriptor descriptor, ScaffoldRequest request)
    {
        var files = new List<PlannedFile>();
        var findings = new List<Finding>();
        var common = CommonTokens(descriptor);
        if (request.CodefreshStarter is { } codefresh && StarterDirectory("codefresh", codefresh, descriptor, findings) is { } codefreshRoot)
        {
            files.AddRange(PlanTree(codefreshRoot, $"codefresh/apps/{descriptor.Name}", common, descriptor, expand: false));
        }

        foreach (var starter in request.OctopusStarters)
        {
            if (StarterDirectory("octopus", starter, descriptor, findings) is not { } octopusRoot)
            {
                continue;
            }

            foreach (var project in descriptor.OctopusProjects)
            {
                var tokens = new Dictionary<string, string>(common, StringComparer.Ordinal)
                {
                    ["<project>"] = project.Name,
                    ["__PROJECT__"] = project.Name,
                };
                files.AddRange(PlanTree(octopusRoot, $".octopus/apps/{descriptor.Name}/{project.Name}", tokens, descriptor, expand: false));
            }
        }

        if (request.GitopsStarter is { } gitops && StarterDirectory("gitops", gitops, descriptor, findings) is { } gitopsRoot)
        {
            files.AddRange(PlanTree(gitopsRoot, $"gitops/apps/{descriptor.Name}", common, descriptor, expand: true));
            files.AddRange(DatabaseFolders(descriptor, files));
        }

        if (request.CodefreshStarter is null && request.OctopusStarters.Count == 0 && request.GitopsStarter is null)
        {
            findings.Add(Finding.Error("scaffold", descriptor.Name, "name at least one starter: --codefresh, --octopus or --gitops"));
        }

        return new ScaffoldPlan(files, findings);
    }

    /// <summary>
    /// Writes a plan. Without <paramref name="force"/> nothing is written when any target exists: a scaffold happens once,
    /// then the files belong to the app.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="force">Overwrite existing files.</param>
    /// <param name="subject">App name for findings.</param>
    /// <returns>Written targets and any conflicts.</returns>
    public (IReadOnlyList<string> Written, IReadOnlyList<Finding> Findings) Apply(ScaffoldPlan plan, bool force, string subject)
    {
        var targets = plan.Files.GroupBy(file => file.Target, StringComparer.Ordinal).ToArray();
        if (!force)
        {
            var conflicts = targets.Where(group => File.Exists(repository.Full(group.Key))).Select(group => group.Key).ToArray();
            if (conflicts.Length > 0)
            {
                return ([], conflicts
                    .Select(target => Finding.Error("scaffold", subject, "already exists; the scaffold runs once and the files belong to the app (use --force to overwrite)", target))
                    .ToArray());
            }
        }

        var written = new List<string>();
        foreach (var group in targets)
        {
            var target = repository.Full(group.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var parts = group.Select(file => (File: file, Bytes: file.Content is null ? File.ReadAllBytes(file.Source!) : Encoding.UTF8.GetBytes(file.Content))).ToArray();
            if (parts.All(part => IsText(part.Bytes)))
            {
                var texts = parts.Select(part => Substitute(Encoding.UTF8.GetString(part.Bytes), part.File.Tokens).TrimEnd('\n') + "\n");
                File.WriteAllText(target, string.Join("\n", texts), new UTF8Encoding(false));
            }
            else
            {
                File.WriteAllBytes(target, parts[^1].Bytes);
            }

            written.Add(group.Key);
        }

        return (written, []);
    }

    /// <summary>Replaces every token of <paramref name="tokens"/> in <paramref name="text"/>.</summary>
    /// <param name="text">Text.</param>
    /// <param name="tokens">Token to value.</param>
    public static string Substitute(string text, IReadOnlyDictionary<string, string> tokens)
    {
        var builder = new StringBuilder(text);
        foreach (var (token, value) in tokens.OrderByDescending(pair => pair.Key.Length))
        {
            builder.Replace(token, value);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The database folder of every environment that the GitOps starter did not provide: the platform component and
    /// the Key Vault credentials component, fed by ConfigMap <c>db-settings</c>
    /// (gitops/platform/components/db/mssql-2022-express/kustomization.yaml, "Input").
    /// </summary>
    private static IEnumerable<PlannedFile> DatabaseFolders(AppDescriptor descriptor, IReadOnlyList<PlannedFile> planned)
    {
        if (descriptor.Database is not { } database)
        {
            yield break;
        }

        var app = descriptor.Name;
        foreach (var environment in descriptor.Environments)
        {
            var target = $"gitops/apps/{app}/envs/{environment}/db/kustomization.yaml";
            if (planned.Any(file => file.Target == target))
            {
                continue;
            }

            var ns = PlatformNames.Namespace(app, environment);
            var content = $"""
                # Database of {app} in {environment} (ADR-IR34; Application {PlatformNames.Application(app, PlatformNames.DatabaseDeployable, environment)}, rendered by the tenant chart).
                # Written by Platform.Onboarding scaffold; the app owns it from now on.
                apiVersion: kustomize.config.k8s.io/v1beta1
                kind: Kustomization
                namespace: {ns}
                components:
                  - ../../../../../platform/components/db/{database.Engine}
                  - ../../../../../platform/components/db-credentials/keyvault
                configMapGenerator:
                  - name: db-settings
                    literals:
                      - DB_NAME={app}
                      - DB_MIGRATOR_USER={app}_migrator
                      - DB_APP_USER={app}_app
                      - DB_APP_ROLES=db_datareader db_datawriter
                      - DB_SECRET_STORE={ns}
                      - DB_VOLUME=disk-{app}-{environment}-db

                """;
            yield return new PlannedFile(null, target, new Dictionary<string, string>(StringComparer.Ordinal), content);
        }
    }

    private static Dictionary<string, string> CommonTokens(AppDescriptor descriptor) => new(StringComparer.Ordinal)
    {
        ["<app>"] = descriptor.Name,
        ["__APP__"] = descriptor.Name,
        ["<app-repo>"] = descriptor.PrimaryRepository.Name,
        ["__APP_REPO__"] = descriptor.PrimaryRepository.Name,
        ["<app-branch>"] = descriptor.PrimaryRepository.DefaultBranch,
        ["__APP_BRANCH__"] = descriptor.PrimaryRepository.DefaultBranch,
    };

    private string? StarterDirectory(string role, string starter, AppDescriptor descriptor, List<Finding> findings)
    {
        var directory = repository.Full($"{StarterRoots[role]}/{starter}");
        if (starter.Contains('/', StringComparison.Ordinal) || starter.Contains('\\', StringComparison.Ordinal) || starter.StartsWith('.') || !Directory.Exists(directory))
        {
            var available = Starters(role);
            findings.Add(Finding.Error("scaffold", descriptor.Name,
                $"{role} starter '{starter}' not found under {StarterRoots[role]}/ (available: {(available.Count == 0 ? "none" : string.Join(", ", available))})"));
            return null;
        }

        return directory;
    }

    private IEnumerable<PlannedFile> PlanTree(string starterRoot, string targetRoot, IReadOnlyDictionary<string, string> tokens, AppDescriptor descriptor, bool expand)
    {
        foreach (var source in Directory.EnumerateFiles(starterRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(starterRoot, source).Replace(Path.DirectorySeparatorChar, '/');
            if (SkippedStarterFiles.Contains(relative, StringComparer.Ordinal))
            {
                continue;
            }

            var segments = relative.Split('/');
            foreach (var (expandedSegments, fileTokens) in Expand(segments, 0, new Dictionary<string, string>(tokens, StringComparer.Ordinal), descriptor, expand, null, null))
            {
                yield return new PlannedFile(source, $"{targetRoot}/{string.Join('/', expandedSegments)}", fileTokens);
            }
        }
    }

    private static IEnumerable<(string[] Segments, Dictionary<string, string> Tokens)> Expand(
        string[] segments, int index, Dictionary<string, string> tokens, AppDescriptor descriptor, bool expand, string? environment, Deployable? deployable)
    {
        if (index == segments.Length)
        {
            var leafTokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal);
            if (environment is not null)
            {
                var ns = PlatformNames.Namespace(descriptor.Name, environment, deployable?.Part);
                leafTokens["<namespace>"] = ns;
                leafTokens["__NAMESPACE__"] = ns;
            }

            yield return (segments.Select(segment => Substitute(segment, leafTokens)).ToArray(), leafTokens);
            yield break;
        }

        var segment = segments[index];
        var isDirectory = index < segments.Length - 1;
        if (expand && isDirectory && segment is EnvSegment or EnvAlias)
        {
            foreach (var env in descriptor.Environments)
            {
                var next = new Dictionary<string, string>(tokens, StringComparer.Ordinal) { [EnvSegment] = env, [EnvAlias] = env };
                var copy = (string[])segments.Clone();
                copy[index] = env;
                foreach (var result in Expand(copy, index + 1, next, descriptor, expand, env, deployable))
                {
                    yield return result;
                }
            }

            yield break;
        }

        if (expand && isDirectory && segment is DeployableSegment or DeployableAlias)
        {
            foreach (var item in descriptor.Deployables)
            {
                var next = new Dictionary<string, string>(tokens, StringComparer.Ordinal) { [DeployableSegment] = item.Name, [DeployableAlias] = item.Name };
                var copy = (string[])segments.Clone();
                copy[index] = item.Name;
                foreach (var result in Expand(copy, index + 1, next, descriptor, expand, environment, item))
                {
                    yield return result;
                }
            }

            yield break;
        }

        if (expand && isDirectory && segment == PlatformNames.DatabaseDeployable && descriptor.Database is null)
        {
            yield break;
        }

        foreach (var result in Expand(segments, index + 1, tokens, descriptor, expand, environment, deployable))
        {
            yield return result;
        }
    }

    private static bool IsText(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, 8000);
        for (var i = 0; i < length; i++)
        {
            if (bytes[i] == 0)
            {
                return false;
            }
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
