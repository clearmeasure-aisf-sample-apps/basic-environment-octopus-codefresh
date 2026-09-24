using System.Text;
using System.Text.RegularExpressions;
using Platform.Onboarding.Checks;
using Platform.Onboarding.Descriptors;
using Platform.Onboarding.Live;
using Platform.Onboarding.Naming;
using Platform.Onboarding.Output;
using Platform.Onboarding.Rendering;
using Platform.Onboarding.Repository;
using Platform.Onboarding.Scaffolding;

namespace Platform.Onboarding.Cli;

/// <summary>
/// The onboarding kit's command line (ADR-IR34, §11.7.5): <c>new</c>, <c>scaffold</c>, <c>render</c>,
/// <c>check [--live]</c>, <c>list</c> and <c>retire</c>. Offline and credential-free except <c>check --live</c>.
/// Exit codes: 0 success, 1 findings or a failed action, 2 usage error.
/// </summary>
internal sealed partial class OnboardingCli
{
    /// <summary>Success.</summary>
    public const int Success = 0;

    /// <summary>Errors were found, or the action failed.</summary>
    public const int Failed = 1;

    /// <summary>The command line is wrong.</summary>
    public const int Usage = 2;

    private const string Help = """
        Platform.Onboarding: the onboarding kit of the multi-app platform (docs/onboarding.md).

        Usage: Platform.Onboarding <command> [options] [--root DIR]

          new <app> --repo clearmeasure-aisf-sample-apps/<name> [--branch main] [--description TEXT]
                    [--images web[,migrator]] [--packaging kustomize|helm|raw] [--database none|mssql-2022-express]
              Writes a minimal descriptor apps/<app>.yaml.
          scaffold <app> [--codefresh <starter>] [--octopus <starter>[,<starter>]] [--gitops <starter>] [--force] [--dry-run]
              Copies starters into codefresh/apps/<app>/, .octopus/apps/<app>/<project>/ and gitops/apps/<app>/ once.
          render [<app>...] [--format yaml|json] [--subscription-id ID]
              Prints the platform objects each descriptor yields (vault names need AZURE_SUBSCRIPTION_ID or --subscription-id).
          check [<app>...] [--scope all|descriptors] [--changes FILE | --base REF] [--live]
              Validates descriptors (apps/schema.json and cross-app rules), the apps' own files, the blast radius of a
              change, and with --live the Octopus and Codefresh objects (OCTOPUS_URL, OCTOPUS_SPACE_ID, OCTOPUS_API_KEY,
              optional CODEFRESH_API_KEY).
          list [--format table|yaml|json]
              Prints the inventory. It is never committed.
          retire <app> --freeze | --delete [--dry-run]
              --freeze sets status: frozen; --delete removes a frozen app's descriptor and app-scoped folders and prints
              the teardown order.

        Exit codes: 0 success, 1 findings or failure, 2 usage error.
        """;

    private static readonly string[] Packagings = ["kustomize", "helm", "raw"];
    private static readonly string[] Engines = ["mssql-2022-express"];

    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly IEnvironment environment;
    private readonly string currentDirectory;
    private readonly Func<HttpMessageHandler>? httpHandlerFactory;

    /// <summary>Creates the command line.</summary>
    /// <param name="output">Standard output.</param>
    /// <param name="error">Standard error.</param>
    /// <param name="environment">Environment variables and today's date.</param>
    /// <param name="currentDirectory">Where the repository search starts.</param>
    /// <param name="httpHandlerFactory">HTTP handler for <c>check --live</c>; tests pass a stub.</param>
    public OnboardingCli(TextWriter output, TextWriter error, IEnvironment environment, string currentDirectory, Func<HttpMessageHandler>? httpHandlerFactory = null)
    {
        this.output = output;
        this.error = error;
        this.environment = environment;
        this.currentDirectory = currentDirectory;
        this.httpHandlerFactory = httpHandlerFactory;
    }

    [GeneratedRegex(@"^clearmeasure-aisf-sample-apps/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,38}[a-z0-9]$")]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._/-]{1,100}$")]
    private static partial Regex BranchPattern();

    [GeneratedRegex(@"^status:.*$", RegexOptions.Multiline)]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"^name:.*$", RegexOptions.Multiline)]
    private static partial Regex NameLine();

    /// <summary>Runs one command.</summary>
    /// <param name="args">The arguments, command first.</param>
    /// <returns>The exit code.</returns>
    public int Run(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Help);
            return args.Count == 0 ? Usage : Success;
        }

        var rest = args.Skip(1).ToArray();
        try
        {
            return args[0] switch
            {
                "new" => New(rest),
                "scaffold" => Scaffold(rest),
                "render" => Render(rest),
                "check" => Check(rest),
                "list" => List(rest),
                "retire" => Retire(rest),
                _ => throw new UsageException($"unknown command '{args[0]}'"),
            };
        }
        catch (UsageException ex)
        {
            error.WriteLine($"Platform.Onboarding: {ex.Message}");
            error.WriteLine("Run 'Platform.Onboarding help' for the usage.");
            return Usage;
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine($"Platform.Onboarding: {ex.Message}");
            return Failed;
        }
    }

    private int New(string[] args)
    {
        var parsed = Arguments.Parse(args, ["root", "repo", "branch", "description", "images", "packaging", "database"], []);
        var app = parsed.Single("app name");
        var repository = LocateRepository(parsed);
        if (PlatformNames.SlugError(app, allowFixture: false) is { } slugError)
        {
            throw new UsageException(slugError);
        }

        var repo = parsed.Value("repo") ?? throw new UsageException("--repo is required: the app repository, clearmeasure-aisf-sample-apps/<name>");
        if (!RepositoryPattern().IsMatch(repo))
        {
            throw new UsageException($"--repo '{repo}' must be a repository of clearmeasure-aisf-sample-apps (owner/name)");
        }

        var branch = parsed.Value("branch") ?? "main";
        if (!BranchPattern().IsMatch(branch))
        {
            throw new UsageException($"--branch '{branch}' is not a branch name");
        }

        var images = (parsed.Value("images") ?? "web").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (images.Length == 0 || images.Any(image => !ImagePattern().IsMatch(image)) || images.Distinct(StringComparer.Ordinal).Count() != images.Length)
        {
            throw new UsageException("--images takes distinct lowercase image names such as web,migrator");
        }

        var packaging = parsed.Value("packaging") ?? "kustomize";
        if (!Packagings.Contains(packaging, StringComparer.Ordinal))
        {
            throw new UsageException($"--packaging must be one of {string.Join(", ", Packagings)}");
        }

        var database = parsed.Value("database") ?? "none";
        if (database != "none" && !Engines.Contains(database, StringComparer.Ordinal))
        {
            throw new UsageException($"--database must be none or one of {string.Join(", ", Engines)}");
        }

        var relative = PlatformRepository.DescriptorPath(app);
        var path = repository.Full(relative);
        if (File.Exists(path))
        {
            error.WriteLine($"Platform.Onboarding: {relative} already exists; edit it instead");
            return Failed;
        }

        var text = NewDescriptorText(app, repo, branch, parsed.Value("description"), images, packaging, database);
        var schema = DescriptorSchema.Load(repository.SchemaPath);
        var loaded = DescriptorCatalog.LoadFile(relative, text, schema);
        if (loaded.Descriptor is null)
        {
            error.WriteLine($"Platform.Onboarding: the generated descriptor does not pass apps/schema.json: {string.Join("; ", loaded.Violations.Select(violation => violation.Message))}");
            return Failed;
        }

        Directory.CreateDirectory(repository.AppsDirectory);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        output.WriteLine($"wrote {relative}");
        output.WriteLine($"next: Platform.Onboarding scaffold {app} --codefresh <starter> --octopus <starter> --gitops <starter>");
        return Success;
    }

    private int Scaffold(string[] args)
    {
        var parsed = Arguments.Parse(args, ["root", "codefresh", "octopus", "gitops"], ["force", "dry-run"]);
        var app = parsed.Single("app name");
        var repository = LocateRepository(parsed);
        var catalog = LoadCatalog(repository);
        var descriptor = catalog.Find(app);
        if (descriptor is null)
        {
            WriteFindings(catalog.Findings.Where(finding => finding.Subject == app).ToArray());
            error.WriteLine($"Platform.Onboarding: apps/{app}.yaml is missing or invalid; run 'new' or fix it, then 'check'");
            return Failed;
        }

        var octopusStarters = (parsed.Value("octopus") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scaffolder = new Scaffolder(repository);
        var plan = scaffolder.Plan(descriptor, new ScaffoldRequest(parsed.Value("codefresh"), octopusStarters, parsed.Value("gitops")));
        if (plan.Findings.Count > 0)
        {
            WriteFindings(plan.Findings);
            return Failed;
        }

        if (parsed.Flag("dry-run"))
        {
            foreach (var target in plan.Files.Select(file => file.Target).Distinct(StringComparer.Ordinal))
            {
                output.WriteLine($"would write {target}");
            }

            return Success;
        }

        var (written, conflicts) = scaffolder.Apply(plan, parsed.Flag("force"), app);
        if (conflicts.Count > 0)
        {
            WriteFindings(conflicts);
            return Failed;
        }

        foreach (var target in written)
        {
            output.WriteLine($"wrote {target}");
        }

        output.WriteLine($"scaffold: {written.Count} files; the copies now belong to {app}. Next: Platform.Onboarding check {app}");
        return Success;
    }

    private int Render(string[] args)
    {
        var parsed = Arguments.Parse(args, ["root", "format", "subscription-id"], []);
        var format = parsed.Value("format") ?? "yaml";
        if (format is not ("yaml" or "json"))
        {
            throw new UsageException("--format must be yaml or json");
        }

        var repository = LocateRepository(parsed);
        var catalog = LoadCatalog(repository);
        var selected = Select(catalog, parsed.Positionals);
        var invalid = selected.Where(app => catalog.Find(app) is null).ToArray();
        if (invalid.Length > 0)
        {
            WriteFindings(catalog.Findings.Where(finding => invalid.Contains(finding.Subject, StringComparer.Ordinal)).ToArray());
            error.WriteLine($"Platform.Onboarding: cannot render invalid descriptors: {string.Join(", ", invalid)}");
            return Failed;
        }

        var subscription = parsed.Value("subscription-id") ?? environment.Get("AZURE_SUBSCRIPTION_ID");
        if (subscription is not null && (subscription.Contains('<', StringComparison.Ordinal) || !Guid.TryParse(subscription, out _)))
        {
            subscription = null;
        }

        output.Write(Inventory.Render(selected.Select(app => catalog.Find(app)!), subscription, format));
        return Success;
    }

    private int Check(string[] args)
    {
        var parsed = Arguments.Parse(args, ["root", "scope", "changes", "base"], ["live"]);
        var scope = parsed.Value("scope") ?? "all";
        if (scope is not ("all" or "descriptors"))
        {
            throw new UsageException("--scope must be all or descriptors");
        }

        if (parsed.Value("changes") is not null && parsed.Value("base") is not null)
        {
            throw new UsageException("give --changes or --base, not both");
        }

        var repository = LocateRepository(parsed);
        var live = new LiveCheck(environment, httpHandlerFactory);
        if (parsed.Flag("live") && live.MissingOctopusVariables() is { Count: > 0 } missing)
        {
            throw new UsageException($"--live needs {string.Join(", ", missing)}");
        }

        var catalog = LoadCatalog(repository);
        var selected = Select(catalog, parsed.Positionals);
        var findings = new FindingList();
        findings.AddRange(catalog.Findings.Where(finding => parsed.Positionals.Count == 0 || finding.Subject.Split(',').Intersect(selected, StringComparer.Ordinal).Any()));
        var valid = selected.Select(catalog.Find).OfType<AppDescriptor>().ToArray();
        if (scope == "all")
        {
            var check = new AppFilesCheck(repository);
            var allApps = catalog.AppNames.ToArray();
            foreach (var descriptor in valid)
            {
                findings.AddRange(check.Check(descriptor, allApps));
            }
        }

        if (parsed.Value("changes") is { } changesFile)
        {
            if (!File.Exists(changesFile))
            {
                throw new UsageException($"--changes file {changesFile} does not exist");
            }

            findings.AddRange(BlastRadius.Evaluate(BlastRadius.Parse(File.ReadAllText(changesFile))));
        }
        else if (parsed.Value("base") is { } baseReference)
        {
            findings.AddRange(BlastRadius.Evaluate(BlastRadius.FromGit(repository.Root, baseReference)));
        }

        if (parsed.Flag("live"))
        {
            findings.AddRange(live.RunAsync(valid, CancellationToken.None).GetAwaiter().GetResult());
        }

        findings.WriteTo(output);
        output.WriteLine($"check: {selected.Count} app(s), {findings.ErrorCount} error(s), {findings.WarningCount} warning(s)");
        return findings.ErrorCount > 0 ? Failed : Success;
    }

    private int List(string[] args)
    {
        var parsed = Arguments.Parse(args, ["root", "format"], []);
        var format = parsed.Value("format") ?? "table";
        if (format is not ("table" or "yaml" or "json"))
        {
            throw new UsageException("--format must be table, yaml or json");
        }

        if (parsed.Positionals.Count > 0)
        {
            throw new UsageException("list takes no app names");
        }

        var repository = LocateRepository(parsed);
        var catalog = LoadCatalog(repository);
        var invalid = catalog.Files.Where(file => file.Descriptor is null).Select(file => file.FileApp).ToArray();
        output.Write(format == "table" ? Inventory.Table(catalog.Valid) : Inventory.Render(catalog.Valid, null, format));
        if (invalid.Length > 0)
        {
            error.WriteLine($"invalid descriptors not listed: {string.Join(", ", invalid)} (run check)");
        }

        return Success;
    }

    private int Retire(string[] args)
    {
        var parsed = Arguments.Parse(args, ["root"], ["freeze", "delete", "dry-run"]);
        var app = parsed.Single("app name");
        if (parsed.Flag("freeze") == parsed.Flag("delete"))
        {
            throw new UsageException("give exactly one of --freeze and --delete");
        }

        if (app == PlatformNames.FixtureApp)
        {
            throw new UsageException($"'{app}' is the conformance fixture; it is never retired");
        }

        var repository = LocateRepository(parsed);
        var catalog = LoadCatalog(repository);
        var descriptor = catalog.Find(app);
        if (descriptor is null)
        {
            error.WriteLine($"Platform.Onboarding: apps/{app}.yaml is missing or invalid");
            return Failed;
        }

        var relative = PlatformRepository.DescriptorPath(app);
        return parsed.Flag("freeze") ? Freeze(repository, descriptor, relative, parsed.Flag("dry-run")) : Delete(repository, descriptor, relative, parsed.Flag("dry-run"));
    }

    private int Freeze(PlatformRepository repository, AppDescriptor descriptor, string relative, bool dryRun)
    {
        if (descriptor.IsFrozen)
        {
            output.WriteLine($"{descriptor.Name} is already frozen");
            return Success;
        }

        var path = repository.Full(relative);
        var text = File.ReadAllText(path);
        var updated = StatusLine().IsMatch(text)
            ? StatusLine().Replace(text, "status: frozen", 1)
            : NameLine().Replace(text, match => match.Value + "\nstatus: frozen", 1);
        if (!dryRun)
        {
            File.WriteAllText(path, updated, new UTF8Encoding(false));
        }

        output.WriteLine($"{(dryRun ? "would set" : "set")} status: frozen in {relative}");
        output.WriteLine("After the merge (ADR-IR34 decision 28):");
        output.WriteLine($"  1. apply octopus/terraform: the projects of app-{descriptor.Name} become disabled;");
        output.WriteLine($"  2. run codefresh/register.sh --app {descriptor.Name}: the triggers turn off;");
        output.WriteLine("  3. Argo CD scales the workloads and the database to zero through the tenant chart; disks, vaults and backups stay.");
        return Success;
    }

    private int Delete(PlatformRepository repository, AppDescriptor descriptor, string relative, bool dryRun)
    {
        var app = descriptor.Name;
        if (!descriptor.IsFrozen)
        {
            error.WriteLine($"Platform.Onboarding: freeze {app} first (retire {app} --freeze), apply it, then delete");
            return Failed;
        }

        var targets = PlatformRepository.AppScopedRoots(app).Select(root => root.TrimEnd('/')).Where(root => Directory.Exists(repository.Full(root))).Prepend(relative).ToArray();
        foreach (var target in targets)
        {
            output.WriteLine($"{(dryRun ? "would delete" : "deleted")} {target}");
            if (dryRun)
            {
                continue;
            }

            var full = repository.Full(target);
            if (File.Exists(full))
            {
                File.Delete(full);
            }
            else
            {
                Directory.Delete(full, recursive: true);
            }
        }

        output.WriteLine($"Teardown of {app}, in order (docs/onboarding.md, section Retire):");
        output.WriteLine($"  1. before the merge: back up the uat and prod databases (step template platform-db-backup) and tag the app repository;");
        output.WriteLine($"  2. merge this pull request: the ApplicationSet drops tenant-{app} and keeps its resources (preserveResourcesOnDeletion);");
        output.WriteLine($"  3. delete the namespaces {app}-* on both clusters, then the retained PersistentVolumes;");
        output.WriteLine($"  4. run apps-apply with App.Name={app} in infra-nonprod and infra-prod: the vaults (soft delete), disks, backup containers and App Insights go (the prod data lock needs the Owner);");
        output.WriteLine("  5. apply octopus/terraform (project shells, group, accounts) and delete the Codefresh projects;");
        output.WriteLine($"  6. lift the tag locks and delete the registry repositories apps/{app}/* and apps-previews/{app}/*;");
        output.WriteLine($"  7. as the provisioner, destroy terraform/apps/grants for {app} when it had Azure access;");
        output.WriteLine("  8. run the orphan test (CAP-KIT-008): nothing of the app may remain.");
        return Success;
    }

    private static string NewDescriptorText(string app, string repo, string branch, string? description, IReadOnlyList<string> images, string packaging, string database)
    {
        var builder = new StringBuilder();
        builder.Append("# App descriptor: design/platform-design.md section 7.0; schema apps/schema.json.\n");
        builder.Append("# Written by Platform.Onboarding new. Next: scaffold, render and check (docs/onboarding.md).\n");
        builder.Append("schema: 1\n");
        builder.Append("name: ").Append(app).Append('\n');
        builder.Append("description: ").Append(Quote(description ?? "TODO: one line about the app")).Append('\n');
        builder.Append("status: active\n");
        builder.Append("repositories:\n");
        builder.Append("  - name: ").Append(repo).Append('\n');
        builder.Append("    defaultBranch: ").Append(Quote(branch)).Append('\n');
        builder.Append("environments: [tdd, uat, prod]\n");
        builder.Append("codefresh:\n");
        builder.Append("  projects: [").Append(app).Append("]\n");
        builder.Append("octopus:\n");
        builder.Append("  projects:\n");
        builder.Append("    - name: ").Append(app).Append('\n');
        builder.Append("deployables:\n");
        builder.Append("  - name: app\n");
        builder.Append("    octopusProject: ").Append(app).Append('\n');
        builder.Append("    packaging: ").Append(packaging).Append('\n');
        builder.Append("    images: [").Append(string.Join(", ", images)).Append("]\n");
        if (packaging == "helm")
        {
            builder.Append("    helm:\n");
            builder.Append("      chart: charts/app\n");
            builder.Append("      imageReplacePaths:\n");
            foreach (var image in images)
            {
                var key = images.Count == 1 ? "image" : image.Replace("-", string.Empty, StringComparison.Ordinal);
                builder.Append("        - \"{{ .Values.").Append(key).Append(".repository }}:{{ .Values.").Append(key).Append(".tag }}\"\n");
            }
        }

        if (database != "none")
        {
            builder.Append("database:\n");
            builder.Append("  engine: ").Append(database).Append('\n');
        }

        return builder.ToString();
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private PlatformRepository LocateRepository(Arguments parsed) =>
        PlatformRepository.Locate(parsed.Value("root"), currentDirectory, environment)
        ?? throw new UsageException("repository root not found: run inside the environment repository (it holds apps/schema.json), or pass --root");

    private DescriptorCatalog LoadCatalog(PlatformRepository repository) =>
        DescriptorCatalog.Load(repository, DescriptorSchema.Load(repository.SchemaPath), environment.Today);

    private static IReadOnlyList<string> Select(DescriptorCatalog catalog, IReadOnlyList<string> requested)
    {
        var known = catalog.AppNames.ToArray();
        if (requested.Count == 0)
        {
            return known;
        }

        var unknown = requested.Where(app => !known.Contains(app, StringComparer.Ordinal)).ToArray();
        return unknown.Length > 0
            ? throw new UsageException($"no descriptor for {string.Join(", ", unknown)} (apps/<app>.yaml)")
            : requested.Distinct(StringComparer.Ordinal).ToArray();
    }

    private void WriteFindings(IEnumerable<Finding> findings)
    {
        foreach (var finding in findings)
        {
            output.WriteLine(finding);
        }
    }
}
