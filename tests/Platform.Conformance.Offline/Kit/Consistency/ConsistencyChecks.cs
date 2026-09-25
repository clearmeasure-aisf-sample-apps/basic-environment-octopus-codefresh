using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Consistency;

/// <summary>
/// The rules of <c>scripts/checks/consistency.sh</c> in C#: cross-package consistency of the environment repository
/// (design §7.0, ADR-IR34), every platform file against <c>contracts/platform-contracts.yaml</c> and the descriptors
/// <c>apps/*.yaml</c>. <see cref="Run"/> runs one check over a repository root and returns its findings, each naming the
/// package that owns the file (design §11.10), in the script's order and wording.
/// </summary>
/// <remarks>
/// As in the script, C01 (contracts) and C24 (descriptors) load first: when the contracts are unusable every check
/// reports the C01 failures instead of running; a check that throws reports C99 (<c>&lt;function&gt; crashed: …</c>)
/// after the findings it made; a failure of the loading itself, where the script stops with a traceback, is C00.
/// </remarks>
internal sealed partial class ConsistencyChecks
{
    private static readonly string[] PlatformConfigRoots = ["argocd", "gitops", ".octopus", "octopus", "codefresh", "containers", "policies", "terraform"];

    private readonly RepositoryFiles files;
    private readonly string contractsFile;
    private readonly string? kustomize;
    private readonly bool render;
    private readonly List<object?> ownership = [];
    private readonly PyDict apps = new();
    private readonly Lazy<Prelude> prelude;
    private PyDict contracts = new();
    private object? environments;
    private object? tiers;
    private object? tierOfEnvironment;

    /// <summary>Creates the checks for a repository.</summary>
    /// <param name="root">Repository root (the script's <c>--root</c>).</param>
    /// <param name="contractsFile">Contracts file (the script's <c>--contracts</c>); default <c>&lt;root&gt;/contracts/platform-contracts.yaml</c>.</param>
    /// <param name="kustomize">kustomize executable for C09; <c>null</c> when it is not installed.</param>
    /// <param name="render"><c>false</c> is the script's <c>--no-render</c>: C09 is skipped.</param>
    public ConsistencyChecks(string root, string? contractsFile = null, string? kustomize = null, bool render = true)
    {
        files = new RepositoryFiles(root);
        this.contractsFile = contractsFile ?? Path.Combine(files.Root, "contracts", "platform-contracts.yaml");
        this.kustomize = kustomize;
        this.render = render;
        prelude = new Lazy<Prelude>(Load);
    }

    /// <summary>Every check, in the script's order: C01 and C24 load first, then the checks of its main loop.</summary>
    public static IReadOnlyList<ConsistencyCheck> Checks { get; } =
    [
        new("C01", "load_contracts", "contracts parse"),
        new("C24", "load_descriptors", "descriptors (basic)"),
        new("C02", "c02_annotations", "Octopus annotations"),
        new("C03", "c03_appset", "ApplicationSet apps"),
        new("C04", "c04_projects", "AppProjects"),
        new("C05", "c05_namespaces", "platform namespaces"),
        new("C06", "c06_bootstrap", "Argo CD bootstrap"),
        new("C09", "c09_rendered", "rendered app overlays"),
        new("C10", "c10_database_endpoints", "database endpoints"),
        new("C11", "c11_stores", "stores and vaults"),
        new("C12", "c12_runbooks", "platform runbooks"),
        new("C15", "c15_octopus_terraform", "Octopus Terraform"),
        new("C18", "c18_signer", "signer and Kyverno"),
        new("C19", "c19_terraform", "Terraform layers"),
        new("C20", "c20_retired", "retired paths, names"),
        new("C21", "c21_placeholders", "placeholders"),
        new("C22", "c22_runbook_inputs", "runbook inputs"),
        new("C23", "c23_sleep_wake", "sleep and wake"),
        new("C25", "c25_codefresh", "Codefresh handshake"),
    ];

    /// <summary>Runs one check and returns every finding it reports (PASS lines included), in the script's order.</summary>
    /// <param name="id">Check ID, for example <c>C09</c>.</param>
    /// <exception cref="ArgumentException">No check has this ID.</exception>
    public IReadOnlyList<ConsistencyFinding> Run(string id)
    {
        var check = Checks.FirstOrDefault(candidate => candidate.Id == id) ?? throw new ArgumentException($"no consistency check {id}", nameof(id));
        var loaded = prelude.Value;
        if (id == "C01")
        {
            return loaded.Contracts;
        }

        if (loaded.Blocked is { } blocked)
        {
            return blocked;
        }

        if (id == "C24")
        {
            return loaded.Descriptors;
        }

        Action<Report> rule = id switch
        {
            "C02" => C02Annotations,
            "C03" => C03ApplicationSet,
            "C04" => C04AppProjects,
            "C05" => C05Namespaces,
            "C06" => C06Bootstrap,
            "C09" => C09RenderedOverlays,
            "C10" => C10DatabaseEndpoints,
            "C11" => C11StoresAndVaults,
            "C12" => C12Runbooks,
            "C15" => C15OctopusTerraform,
            "C18" => C18SignerAndKyverno,
            "C19" => C19TerraformLayers,
            "C20" => C20RetiredPathsAndNames,
            "C21" => C21Placeholders,
            "C22" => C22RunbookInputs,
            "C23" => C23SleepAndWake,
            _ => C25CodefreshHandshake,
        };
        var report = new Report(this, id);
        try
        {
            rule(report);
        }
        catch (Exception exception)
        {
            report.Findings.Add(Crash("C99", check.Function, exception));
        }

        return report.Findings;
    }

    /// <summary>The script's <c>x(s, **kw)</c>: replaces <c>{name}</c> with each value.</summary>
    private static string X(object? template, params (string Name, string Value)[] values)
    {
        var text = template as string ?? throw PyException.AttributeError(template, "replace");
        foreach (var (name, value) in values)
        {
            text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>An item of the contracts, <c>C[key][key]…</c>.</summary>
    private static object? At(object? node, params string[] keys) => keys.Aggregate(node, Py.Item);

    /// <summary>Python's type name of an exception, for C99 and C00 lines.</summary>
    private static string TypeOf(Exception exception) => exception switch
    {
        PyException python => python.Type,
        YamlLoadException => "YAMLError",
        _ => exception.GetType().Name,
    };

    /// <summary>The script's <c>owner(rel)</c>: the package of the longest ownership root that covers the path.</summary>
    private string Owner(string relative)
    {
        if (relative.Length == 0)
        {
            return "-";
        }

        var bestRoot = string.Empty;
        var bestPackage = "-";
        foreach (var item in ownership)
        {
            var root = Py.Str(Py.Get(item, "root", string.Empty));
            if ((relative == root.TrimEnd('/') || relative.StartsWith(root, StringComparison.Ordinal)) && root.Length > bestRoot.Length)
            {
                bestRoot = root;
                bestPackage = Py.Str(Py.Get(item, "package", "-"));
            }
        }

        return bestPackage;
    }

    private ConsistencyFinding Crash(string id, string function, Exception exception)
    {
        string owner;
        try
        {
            owner = Owner("-");
        }
        catch (PyException)
        {
            owner = "-";
        }

        var message = id == "C00"
            ? $"{function} crashed: {TypeOf(exception)}: {exception.Message}; the script stops here, no check runs"
            : $"{function} crashed: {TypeOf(exception)}: {exception.Message}";
        return new ConsistencyFinding(FindingStatus.Fail, id, owner, "-", message);
    }

    /// <summary>C01, the module-level names and C24, which every check needs, as the script runs them before its checks.</summary>
    private Prelude Load()
    {
        var contractsReport = new Report(this, "C01");
        PyDict? loaded;
        try
        {
            loaded = LoadContracts(contractsReport);
        }
        catch (Exception exception)
        {
            contractsReport.Findings.Add(Crash("C00", "load_contracts", exception));
            return new Prelude(contractsReport.Findings, [], [contractsReport.Findings[^1]]);
        }

        if (loaded is null)
        {
            return new Prelude(contractsReport.Findings, [], contractsReport.Findings);
        }

        contracts = loaded;
        var descriptorsReport = new Report(this, "C24");
        try
        {
            environments = At(contracts, "expansions", "env");
            tiers = At(contracts, "expansions", "tier");
            tierOfEnvironment = At(contracts, "expansions", "tierOfEnv");
            LoadDescriptors(descriptorsReport);
        }
        catch (Exception exception)
        {
            descriptorsReport.Findings.Add(Crash("C00", "load_descriptors", exception));
            return new Prelude(contractsReport.Findings, descriptorsReport.Findings, [descriptorsReport.Findings[^1]]);
        }

        return new Prelude(contractsReport.Findings, descriptorsReport.Findings, null);
    }

    /// <summary>C01: the contracts file parses and holds every section; its <c>ownership</c> attributes every finding.</summary>
    private PyDict? LoadContracts(Report report)
    {
        var relative = contractsFile.StartsWith(files.Root, StringComparison.Ordinal) ? files.Relative(contractsFile) : contractsFile;
        object? loaded;
        try
        {
            loaded = PyYaml.Load(RepositoryFiles.ReadForYaml(contractsFile));
        }
        catch (PyException exception) when (exception.Type is "FileNotFoundError" or "OSError")
        {
            report.Out(FindingStatus.Fail, relative, "contracts file not found");
            return null;
        }
        catch (YamlLoadException exception)
        {
            report.Out(FindingStatus.Fail, relative, "contracts file does not parse: " + exception.Message);
            return null;
        }

        string[] need = ["expansions", "placeholders", "envRepo", "descriptors", "starters", "azure", "octopus", "codefresh",
            "registry", "argocd", "kubernetes", "sleepWake", "conformance", "retiredPaths", "retiredNames", "ownership"];
        var sections = Py.Or(loaded, new PyDict());
        var missing = need.Where(key => !Py.In(key, sections)).ToList();
        ownership.AddRange(Py.Iterate(Py.Get(sections, "ownership", new List<object?>())));
        report.Verdict(relative, missing.Select(key => $"missing section '{key}'").ToList(), "contracts parse; all sections present");
        return missing.Count == 0 ? loaded as PyDict : null;
    }

    /// <summary>C24: every <c>apps/*.yaml</c> parses, is a mapping, has the schema version and is named like its file.</summary>
    private void LoadDescriptors(Report report)
    {
        if (!files.IsDirectory("apps"))
        {
            report.Out(FindingStatus.Skip, "apps/", "no descriptors directory");
            return;
        }

        var descriptors = At(contracts, "descriptors");
        var schema = Py.AsStr(At(descriptors, "schema"));
        if (!files.Exists(schema))
        {
            report.Out(FindingStatus.Fail, schema, "the descriptor schema is missing");
        }

        foreach (var relative in files.Glob("apps", ".yaml"))
        {
            var stem = Path.GetFileName(relative)[..^5];
            var errors = new List<string>();
            object? loaded;
            try
            {
                loaded = PyYaml.Load(RepositoryFiles.ReadForYaml(files.PathOf(relative)));
            }
            catch (YamlLoadException exception)
            {
                report.Out(FindingStatus.Fail, relative, "YAML does not parse: " + exception.Message);
                continue;
            }

            if (loaded is not PyDict descriptor)
            {
                report.Out(FindingStatus.Fail, relative, "a descriptor is a mapping");
                continue;
            }

            if (!Py.Eq(Py.Get(descriptor, "schema"), At(descriptors, "schemaVersion")))
            {
                errors.Add($"schema must be {Py.Str(At(descriptors, "schemaVersion"))}");
            }

            if (!Py.Eq(Py.Get(descriptor, "name"), stem))
            {
                errors.Add($"name '{Py.Str(Py.Get(descriptor, "name"))}' must equal the file name '{stem}'");
            }

            if (!Regex.IsMatch(Py.Str(Py.Get(descriptor, "name", string.Empty)), @"\A(?:" + Py.AsStr(At(descriptors, "appPattern")) + ")"))
            {
                errors.Add("name does not match the app slug rule");
            }

            report.Verdict(relative, errors, "descriptor parses; name and schema version agree (full validation: validate-all.sh onboarding)");
            if (errors.Count == 0)
            {
                apps.Set(stem, descriptor);
            }
        }

        var fixture = At(descriptors, "fixtureApp");
        if (!apps.ContainsKey(fixture))
        {
            report.Out(FindingStatus.Fail, $"apps/{Py.Str(fixture)}.yaml", "the conformance fixture descriptor is missing");
        }
    }

    /// <summary>Environment names of the contracts (<c>expansions.env</c>).</summary>
    private List<string> Environments() => Py.Iterate(environments).Select(Py.Str).ToList();

    /// <summary>Tier names of the contracts (<c>expansions.tier</c>).</summary>
    private List<string> Tiers() => Py.Iterate(tiers).Select(Py.Str).ToList();

    /// <summary>Apps with a valid descriptor, sorted by name, as <c>sorted(APPS.items())</c>.</summary>
    private List<(string Name, PyDict Descriptor)> Apps() =>
        apps.Items.Select(pair => ((string)pair.Key!, (PyDict)pair.Value!)).OrderBy(pair => pair.Item1, Py.StringOrder).ToList();

    /// <summary>The script's <c>app_envs(d)</c>: the contract environments the descriptor lists (all when it lists none).</summary>
    private List<string> AppEnvironments(PyDict descriptor)
    {
        var listed = Py.Or(Py.Get(descriptor, "environments"), environments);
        return Py.Iterate(environments).Where(environment => Py.In(environment, listed)).Select(Py.Str).ToList();
    }

    /// <summary>The script's <c>deployables(d)</c>: the mappings under <c>deployables</c>.</summary>
    private static List<PyDict> Deployables(PyDict descriptor) =>
        Py.Iterate(Py.Or(Py.Get(descriptor, "deployables"), new List<object?>())).OfType<PyDict>().ToList();

    /// <summary>The script's <c>namespace(app, env, part)</c>.</summary>
    private static string Namespace(string app, string environment, object? part = null) =>
        Py.Truthy(part) ? $"{app}-{Py.Str(part)}-{environment}" : $"{app}-{environment}";

    /// <summary>What C01, C24 and the module-level names produced; <paramref name="Blocked"/> is what every other check reports when they failed.</summary>
    private sealed record Prelude(IReadOnlyList<ConsistencyFinding> Contracts, IReadOnlyList<ConsistencyFinding> Descriptors, IReadOnlyList<ConsistencyFinding>? Blocked);

    /// <summary>The findings of one check while it runs, with the script's <c>out</c>, <c>verdict</c>, <c>absent</c> and <c>docs</c>.</summary>
    private sealed class Report(ConsistencyChecks checks, string id)
    {
        private readonly Dictionary<string, IReadOnlyList<object?>?> documents = new(StringComparer.Ordinal);

        /// <summary>Findings so far.</summary>
        public List<ConsistencyFinding> Findings { get; } = [];

        /// <summary>The script's <c>out(status, cid, rel, msg)</c>.</summary>
        public void Out(FindingStatus status, string path, string message) =>
            Findings.Add(new ConsistencyFinding(status, id, checks.Owner(path), path, message));

        /// <summary>The script's <c>verdict</c>: FAIL (or WARN) with the errors joined by <c>; </c>, else PASS.</summary>
        public void Verdict(string path, IReadOnlyList<string> errors, string passMessage, bool warn = false)
        {
            if (errors.Count > 0)
            {
                Out(warn ? FindingStatus.Warn : FindingStatus.Fail, path, string.Join("; ", errors));
            }
            else
            {
                Out(FindingStatus.Pass, path, passMessage);
            }
        }

        /// <summary>The script's <c>absent</c>: SKIP when the path's top-level folder is absent, FAIL otherwise.</summary>
        public void Absent(string path, string what)
        {
            var top = path.Split('/')[0];
            if (checks.files.Exists(top))
            {
                Out(FindingStatus.Fail, path, $"{what}: file missing");
            }
            else
            {
                Out(FindingStatus.Skip, path, $"{what}: directory '{top}' absent");
            }
        }

        /// <summary>
        /// The script's <c>docs(rel, cid)</c>: the non-empty YAML documents of a file, loaded once; <c>null</c> when the file
        /// cannot be opened, or does not parse (reported once as FAIL).
        /// </summary>
        public IReadOnlyList<object?>? Docs(string path)
        {
            if (documents.TryGetValue(path, out var cached))
            {
                return cached;
            }

            IReadOnlyList<object?>? loaded;
            try
            {
                loaded = PyYaml.LoadAll(RepositoryFiles.ReadForYaml(checks.files.PathOf(path))).Where(document => document is not null).ToList();
            }
            catch (PyException exception) when (exception.Type is "FileNotFoundError" or "OSError")
            {
                loaded = null;
            }
            catch (YamlLoadException exception)
            {
                Out(FindingStatus.Fail, path, "YAML does not parse: " + exception.Message);
                loaded = null;
            }

            documents[path] = loaded;
            return loaded;
        }
    }
}
