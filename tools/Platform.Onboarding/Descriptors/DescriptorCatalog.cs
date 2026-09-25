using System.Text.Json.Nodes;
using Platform.Onboarding.Naming;
using Platform.Onboarding.Output;
using Platform.Onboarding.Repository;

namespace Platform.Onboarding.Descriptors;

/// <summary>One descriptor file as loaded: its parse result, schema violations and, when valid, the typed descriptor.</summary>
/// <param name="FileApp">The file name without <c>.yaml</c>.</param>
/// <param name="RelativePath"><c>apps/&lt;x&gt;.yaml</c>.</param>
/// <param name="Document">The YAML as JSON, with lines.</param>
/// <param name="Violations">Schema violations.</param>
/// <param name="Descriptor">The typed descriptor when the file parses and passes the schema.</param>
internal sealed record LoadedDescriptor(string FileApp, string RelativePath, YamlJsonDocument Document, IReadOnlyList<SchemaViolation> Violations, AppDescriptor? Descriptor);

/// <summary>
/// Every descriptor of the repository, validated against <c>apps/schema.json</c> and against the rules a schema cannot
/// express: the file name, project prefixes, deployables naming declared projects, host names starting with their
/// namespace, uniqueness inside a descriptor and across descriptors, and the expiry warning.
/// </summary>
internal sealed class DescriptorCatalog
{
    private const int ExpiryWarningDays = 14;

    private DescriptorCatalog(IReadOnlyList<LoadedDescriptor> files, IReadOnlyList<Finding> findings)
    {
        Files = files;
        Findings = findings;
    }

    /// <summary>Every descriptor file, sorted by name.</summary>
    public IReadOnlyList<LoadedDescriptor> Files { get; }

    /// <summary>Every finding about the descriptors.</summary>
    public IReadOnlyList<Finding> Findings { get; }

    /// <summary>The valid descriptors.</summary>
    public IEnumerable<AppDescriptor> Valid => Files.Select(file => file.Descriptor).OfType<AppDescriptor>();

    /// <summary>App names known from file names, valid or not.</summary>
    public IEnumerable<string> AppNames => Files.Select(file => file.FileApp);

    /// <summary>The valid descriptor of <paramref name="app"/>, or <c>null</c>.</summary>
    /// <param name="app">App slug.</param>
    public AppDescriptor? Find(string app) => Valid.FirstOrDefault(descriptor => descriptor.Name == app);

    /// <summary>Loads and validates every <c>apps/*.yaml</c>.</summary>
    /// <param name="repository">The repository.</param>
    /// <param name="schema">The descriptor schema.</param>
    /// <param name="today">Today, for the expiry warning.</param>
    public static DescriptorCatalog Load(PlatformRepository repository, DescriptorSchema schema, DateOnly today)
    {
        var files = new List<LoadedDescriptor>();
        if (Directory.Exists(repository.AppsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(repository.AppsDirectory, "*.yaml").OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                files.Add(LoadFile(repository.Relative(path), File.ReadAllText(path), schema));
            }
        }

        return new DescriptorCatalog(files, Validate(files, today));
    }

    /// <summary>Loads one descriptor text (tests and fixtures).</summary>
    /// <param name="relativePath">Path used in messages, <c>apps/&lt;x&gt;.yaml</c>.</param>
    /// <param name="yaml">Descriptor text.</param>
    /// <param name="schema">The descriptor schema.</param>
    public static LoadedDescriptor LoadFile(string relativePath, string yaml, DescriptorSchema schema)
    {
        var fileApp = Path.GetFileNameWithoutExtension(relativePath);
        var document = YamlJson.Parse(yaml);
        if (document.Error is not null)
        {
            return new LoadedDescriptor(fileApp, relativePath, document, [], null);
        }

        var violations = schema.Evaluate(document.Root);
        var descriptor = violations.Count == 0 && document.Root is JsonObject root ? AppDescriptor.FromJson(root) : null;
        return new LoadedDescriptor(fileApp, relativePath, document, violations, descriptor);
    }

    /// <summary>Builds a catalogue from already-loaded files (tests).</summary>
    /// <param name="files">Loaded files.</param>
    /// <param name="today">Today, for the expiry warning.</param>
    public static DescriptorCatalog FromFiles(IReadOnlyList<LoadedDescriptor> files, DateOnly today) => new(files, Validate(files, today));

    private static List<Finding> Validate(IReadOnlyList<LoadedDescriptor> files, DateOnly today)
    {
        var findings = new List<Finding>();
        foreach (var file in files)
        {
            findings.AddRange(ValidateFile(file, today));
        }

        findings.AddRange(ValidateAcross(files.Select(file => file.Descriptor).OfType<AppDescriptor>().ToArray()));
        return findings;
    }

    private static IEnumerable<Finding> ValidateFile(LoadedDescriptor file, DateOnly today)
    {
        var app = file.FileApp;
        var path = file.RelativePath;
        if (file.Document.Error is not null)
        {
            yield return Finding.Error("yaml", app, file.Document.Error, path, file.Document.ErrorLine);
            yield break;
        }

        if (file.Document.Root is null)
        {
            yield return Finding.Error("yaml", app, "the descriptor is empty", path);
            yield break;
        }

        foreach (var violation in file.Violations)
        {
            yield return Finding.Error("schema", app, violation.Message, path, file.Document.LineOf(violation.Pointer));
        }

        var descriptor = file.Descriptor;
        if (descriptor is null)
        {
            yield break;
        }

        int? Line(string pointer) => file.Document.LineOf(pointer);

        if (descriptor.Name != file.FileApp)
        {
            yield return Finding.Error("name", app, $"name '{descriptor.Name}' must equal the file name '{file.FileApp}'", path, Line("/name"));
        }

        foreach (var (project, index) in descriptor.CodefreshProjects.Select((project, index) => (project, index)))
        {
            if (!HasAppPrefix(project, descriptor.Name))
            {
                yield return Finding.Error("project", app, $"Codefresh project '{project}' must be '{descriptor.Name}' or '{descriptor.Name}-<part>'", path, Line($"/codefresh/projects/{index}"));
            }
        }

        var octopusNames = descriptor.OctopusProjects.Select(project => project.Name).ToArray();
        foreach (var (project, index) in octopusNames.Select((project, index) => (project, index)))
        {
            if (!HasAppPrefix(project, descriptor.Name))
            {
                yield return Finding.Error("project", app, $"Octopus project '{project}' must be '{descriptor.Name}' or '{descriptor.Name}-<part>'", path, Line($"/octopus/projects/{index}/name"));
            }
        }

        foreach (var duplicate in Duplicates(octopusNames))
        {
            yield return Finding.Error("project", app, $"Octopus project '{duplicate}' is declared twice", path, Line("/octopus/projects"));
        }

        foreach (var duplicate in Duplicates(descriptor.Deployables.Select(deployable => deployable.Name)))
        {
            yield return Finding.Error("deployable", app, $"deployable '{duplicate}' is declared twice", path, Line("/deployables"));
        }

        foreach (var (deployable, index) in descriptor.Deployables.Select((deployable, index) => (deployable, index)))
        {
            if (!octopusNames.Contains(deployable.OctopusProject, StringComparer.Ordinal))
            {
                yield return Finding.Error("deployable", app, $"deployable '{deployable.Name}' names Octopus project '{deployable.OctopusProject}', which octopus.projects does not declare", path, Line($"/deployables/{index}/octopusProject"));
            }

            if (deployable.Helm is { } helm && helm.Chart.Split('/').Any(segment => segment is ".." or "."))
            {
                yield return Finding.Error("deployable", app, $"helm.chart '{helm.Chart}' must stay inside gitops/apps/{descriptor.Name}/", path, Line($"/deployables/{index}/helm/chart"));
            }
        }

        foreach (var duplicate in Duplicates(descriptor.Deployables.SelectMany(deployable => deployable.Images)))
        {
            yield return Finding.Error("image", app, $"image '{duplicate}' is pinned by more than one deployable; an image belongs to one deployable", path, Line("/deployables"));
        }

        foreach (var (environment, host) in descriptor.Hosts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var pointer = $"/hosts/{environment}";
            var namespaceName = PlatformNames.Namespace(descriptor.Name, environment);
            if (!descriptor.Environments.Contains(environment, StringComparer.Ordinal))
            {
                yield return Finding.Error("host", app, $"hosts.{environment}: environment '{environment}' is not in environments", path, Line(pointer));
            }

            if (!host.StartsWith(namespaceName + ".", StringComparison.Ordinal))
            {
                yield return Finding.Error("host", app, $"hosts.{environment} '{host}' must start with '{namespaceName}.': the first DNS label is the namespace (Kyverno platform-app-hostnames)", path, Line(pointer));
            }

            if (host.EndsWith(".sslip.io", StringComparison.Ordinal))
            {
                yield return Finding.Error("host", app, $"hosts.{environment} '{host}' is an sslip.io host, the default; declare only a host the tier provisioned (R35)", path, Line(pointer));
            }
        }

        foreach (var duplicate in Duplicates(descriptor.Secrets.Select(secret => secret.Name)))
        {
            yield return Finding.Error("secret", app, $"secret '{duplicate}' is declared twice", path, Line("/secrets"));
        }

        foreach (var duplicate in Duplicates(descriptor.Repositories.Select(repository => repository.Name)))
        {
            yield return Finding.Error("repository", app, $"repository '{duplicate}' is listed twice", path, Line("/repositories"));
        }

        if (descriptor.Expires is { } expires)
        {
            var days = expires.DayNumber - today.DayNumber;
            if (days < 0)
            {
                yield return Finding.Warning("expiry", app, $"expired on {expires:yyyy-MM-dd}: freeze or retire it (Platform.Onboarding retire {descriptor.Name} --freeze)", path, Line("/expires"));
            }
            else if (days <= ExpiryWarningDays)
            {
                yield return Finding.Warning("expiry", app, $"expires on {expires:yyyy-MM-dd}, in {days} day(s)", path, Line("/expires"));
            }
        }
    }

    private static IEnumerable<Finding> ValidateAcross(IReadOnlyList<AppDescriptor> descriptors)
    {
        foreach (var (kind, names) in new (string Kind, Func<AppDescriptor, IEnumerable<string>> Names)[]
                 {
                     ("Octopus project", descriptor => descriptor.OctopusProjects.Select(project => project.Name)),
                     ("Codefresh project", descriptor => descriptor.CodefreshProjects),
                 })
        {
            foreach (var clash in descriptors
                         .SelectMany(descriptor => names(descriptor).Select(name => (Name: name, App: descriptor.Name)))
                         .GroupBy(entry => entry.Name, StringComparer.Ordinal)
                         .Where(group => group.Select(entry => entry.App).Distinct(StringComparer.Ordinal).Count() > 1))
            {
                var apps = clash.Select(entry => entry.App).Distinct(StringComparer.Ordinal).ToArray();
                yield return Finding.Error("project", string.Join(",", apps), $"{kind} '{clash.Key}' is claimed by {string.Join(" and ", apps.Select(PlatformRepository.DescriptorPath))}");
            }
        }

        foreach (var shared in descriptors
                     .SelectMany(descriptor => descriptor.Repositories.Where(repository => !repository.IsPlaceholder).Select(repository => (repository.Name, App: descriptor.Name)))
                     .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(entry => entry.App).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            var apps = shared.Select(entry => entry.App).Distinct(StringComparer.Ordinal).ToArray();
            yield return Finding.Warning("repository", string.Join(",", apps), $"repository '{shared.Key}' is shared by {string.Join(" and ", apps)}; each app normally has its own");
        }
    }

    private static bool HasAppPrefix(string project, string app) =>
        project == app || (project.StartsWith(app + "-", StringComparison.Ordinal) && PlatformNames.IsPartName(project[(app.Length + 1)..]));

    private static IEnumerable<string> Duplicates(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key);
}
