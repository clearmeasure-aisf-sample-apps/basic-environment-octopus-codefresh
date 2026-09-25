using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>A rule of the tool-boundary lint.</summary>
/// <param name="Id">Rule ID, for example <c>TB01</c>.</param>
/// <param name="Description">The rule's one-line statement.</param>
/// <param name="Evaluate">Checks a tree.</param>
internal sealed record BoundaryRule(string Id, string Description, Func<BoundaryTree, BoundaryResult> Evaluate)
{
    /// <summary>Checks a tree against the rule.</summary>
    /// <param name="tree">The repository tree.</param>
    public BoundaryResult Check(BoundaryTree tree) => Evaluate(tree);
}

/// <summary>
/// C# port of the static lint of <c>scripts/checks/tool-boundaries.sh</c> (design ADR-D2, ADR-IR32 to ADR-IR34), rules
/// TB01 to TB22 plus TB23 (<see cref="ScriptLanguageRule"/>). One verb per tool: Codefresh builds; Octopus releases,
/// promotes, approves and runs runbooks; Argo CD applies; GitHub enforces merge rules. A rule fails when a platform file
/// lets a tool leave its lane, when a platform secret reaches an app project, or when a platform file names an app.
/// </summary>
/// <remarks>
/// Each rule keeps the script's patterns (POSIX extended regular expressions, translated by <see cref="PosixPatterns"/>),
/// its paths, its exemptions and its messages, so a finding reads as the script prints it. Paths absent from a partial
/// tree skip a rule, never fail it. Markdown files are never scanned: docs describe the forbidden features. Since the
/// scripts moved to PowerShell 7, TB14 and TB16 also match the PowerShell spellings of what they ban: a <c>-ApiKey</c>
/// parameter, and a push or commit through a Git wrapper function (<c>Invoke-SandboxGit push</c>) or with quoted or
/// array arguments (<c>git 'push'</c>, <c>git @('commit', ...)</c>).
/// Differences kept on purpose: findings are listed in full (TB22 of the script prints at most 60), duplicates are
/// dropped (TB20 searched octopus/templates twice), multi-line scans (TB13c, TB19) start afresh in every file, and a
/// single file is read like any other (grep leaves out the file name and the comment filter of a lone file operand).
/// </remarks>
internal static class ToolBoundaryRules
{
    /// <summary>A single or double quote (the script's <c>Q</c>).</summary>
    private const string Q = "[\"']";

    /// <summary>The Codefresh runtime every spec names (design §7.0, trust boundary TB2).</summary>
    private const string CfRuntime = "^(aks-platform-build/codefresh|<cf-runtime>)$";

    private static readonly string[] PowerAndRunbookPaths = [".octopus", "octopus", "terraform", "codefresh", "containers", "argocd", "gitops", "policies", "fixtures"];

    private static readonly string[] AppRoots = ["codefresh/apps/", ".octopus/apps/", "gitops/apps/", "containers/apps/"];

    /// <summary>Every rule, in the order the script runs them, then TB23.</summary>
    public static IReadOnlyList<BoundaryRule> All { get; } =
    [
        Grep("TB01", "Codefresh builds: no deploy, approval, helm or launch-composition steps",
            $$"""type:[[:space:]]*{{Q}}?(deploy|approval|helm|launch-composition){{Q}}?([[:space:]]|#|$)""",
            withComments: false, "codefresh"),
        Grep("TB02", "Codefresh never reaches app clusters: no argocd, kubectl, helm install/upgrade or az aks credentials",
            $$"""(^|[^[:alnum:]_./-])(argocd[[:space:]]+(app|appset|proj|cluster|repo|login|account|admin)[[:space:]]|kubectl[[:space:]]+(apply|create|replace|patch|set|delete|edit|scale|rollout|annotate|label|exec|cp|port-forward|get|logs|describe|config)([[:space:]]|$)|helm[[:space:]]+(install|upgrade|rollback|uninstall)([[:space:]]|$)|az[[:space:]]+aks[[:space:]]+(get-credentials|command))|type:[[:space:]]*{{Q}}?[[:alnum:]_/-]*argo-?cd[[:alnum:]_/-]*""",
            withComments: false, "codefresh"),
        Grep("TB03", "Codefresh GitOps Runtime and Promotions stay off",
            $$"""apiVersion:[[:space:]]*{{Q}}?codefresh\.io/|gitops-runtime|kind:[[:space:]]*{{Q}}?(PromotionFlow|PromotionPolicy|PromotionTemplate|Product){{Q}}?([[:space:]]|$)""",
            withComments: false, "argocd", "gitops", "policies", "terraform", "octopus", ".octopus", "codefresh"),
        Grep("TB04", "Octopus is the only image-tag writer: no Argo CD Image Updater",
            $$"""argocd-image-updater|image-updater\.argoproj\.io|kind:[[:space:]]*{{Q}}?ImageUpdater""",
            withComments: false, "argocd", "gitops", "policies", "terraform"),
        Grep("TB05", "Freezes live in Octopus: no Argo CD syncWindows",
            "syncWindows",
            withComments: false, "argocd", "gitops", "terraform"),
        Grep("TB06", "No floating 'latest' tag in desired state, deployment config or pipelines",
            $$"""(:latest([[:space:]"'@]|$)|(newTag|tag|imageTag):[[:space:]]*{{Q}}?latest{{Q}}?([[:space:]]|$))""",
            withComments: false, "gitops", "argocd", ".octopus", "octopus", "terraform", "codefresh", "containers"),
        Grep("TB07", "Octopus never applies Kubernetes state: no kubectl apply/set image/patch, Helm or Kubernetes deploy steps, no argocd app sync",
            """kubectl[[:space:]]+(apply|set[[:space:]]+image|patch|create|replace|edit|scale)([[:space:]]|$)|helm[[:space:]]+(install|upgrade)([[:space:]]|$)|Octopus\.(KubernetesDeploy[[:alnum:]]*|HelmChartUpgrade|Kustomize|KubernetesRunScript)|argocd[[:space:]]+app[[:space:]]+(sync|rollback|set|patch|delete)""",
            withComments: false, ".octopus", "octopus"),
        Grep("TB08", "Tier layers (terraform/tier, terraform/apps/tier) have no role assignments, role definitions, locks or policy assignments",
            "azurerm_role_assignment|azurerm_role_definition|azurerm_management_lock|azurerm_[a-z_]*policy_assignment|azuread_app_role_assignment|azuread_directory_role_assignment",
            withComments: true, "terraform/tier", "terraform/apps/tier"),
        new("TB09", "Octopus annotations only in the tenant chart gitops/platform/tenant; no tenant annotation", OctopusAnnotations),
        Grep("TB10", "Trigger sync stays off in the Argo CD step (ADR-D4) [VERIFY property name]",
            $$"""[Tt]rigger[._ -]?[Ss]ync[[:alnum:]._]*{{Q}}?[[:space:]]*[=:][[:space:]]*{{Q}}?[Tt]rue""",
            withComments: false, ".octopus", "octopus/templates"),
        Grep("TB11", "Codefresh creates releases: no feed or built-in release triggers (the keyless pattern is optional and not built)",
            "octopusdeploy_(external_feed_create_release_trigger|built_in_trigger)|auto_create_release[[:space:]]*=[[:space:]]*true",
            withComments: false, "octopus", ".octopus"),
        Grep("TB12", "No Octopus deployment targets on app clusters (Kubernetes workers only)",
            "octopusdeploy_kubernetes_(agent_deployment_target|cluster_deployment_target)",
            withComments: false, "octopus", "terraform"),
        Grep("TB13a", "No Octopus project uses the stored Azure Runtime Provisioner",
            "azure-runtime-provisioner|Azure Runtime Provisioner",
            withComments: false, ".octopus", "octopus/templates"),
        Grep("TB13b", "Stored Codefresh contexts github-aisf-sample-apps-token and azure-runtime-provisioner are attached to no pipeline",
            "azure-runtime-provisioner|github-aisf-sample-apps-token",
            withComments: false, "codefresh/apps", "codefresh/templates", "codefresh/platform/specs", "codefresh/platform/pipelines"),
        new("TB13c", "Stored variable sets 'Azure Runtime Provisioning' and 'GitHub AISF Sample Apps' are included in no project", LibrarySets),
        new("TB14", "Octopus API key only as OCTOPUS_API_KEY or X-Octopus-ApiKey, in release and conformance pipelines (ADR-IR32)", OctopusApiKey),
        new("TB15", "Kyverno image verification does not mutate image references (mutateDigest)", MutateDigest),
        new("TB16", "Codefresh never commits or pushes (conformance pipelines: <sandbox-app-repo> only)", NoGitWrites),
        new("TB17", "az aks start/stop and alert-processing-rule toggles only in env-wake.ocl and env-sleep.ocl (sleep/wake)", ClusterPower),
        new("TB18", "Runbook-run REST calls only in platform-infrastructure runbooks, platform-wake run-env-wake, release wake_nonprod and conformance pipelines", RunbookRuns),
        new("TB19", "No platform account or Azure start right in app projects, starters or app grants", NoStartRights),
        new("TB20", "Octopus key variables only in platform-infrastructure and platform-wake; PlatformWake.* never in apps; no literal API key", PlatformKey),
        new("TB21", "TB2: one runtime aks-platform-build/codefresh; no grant in terraform/build; no cloud identity for app pipelines or the runner", BuildCluster),
        new("TB22", "Platform files name no app outside the app-scoped paths (name lint)", NameLint),
        new(ScriptLanguageRule.Id, ScriptLanguageRule.Description, ScriptLanguageRule.Check),
    ];

    /// <summary>Checks every rule against the tree under <paramref name="root"/>.</summary>
    /// <param name="root">Repository root.</param>
    public static IReadOnlyList<BoundaryResult> CheckAll(string root)
    {
        var tree = new BoundaryTree(root);
        return All.Select(rule => rule.Check(tree)).ToArray();
    }

    /// <summary>Checks one rule.</summary>
    /// <param name="tree">The repository tree.</param>
    /// <param name="id">Rule ID, for example <c>TB14</c>.</param>
    public static BoundaryResult Check(BoundaryTree tree, string id) => All.Single(rule => rule.Id == id).Check(tree);

    /// <summary>An app or starter release pipeline (release.yml or release-&lt;x&gt;.yml).</summary>
    /// <param name="path">Repository-relative path.</param>
    public static bool IsReleasePipeline(string path) => CasePattern.Matches(
        path,
        "codefresh/apps/*/pipelines/release.yml",
        "codefresh/apps/*/pipelines/release-*.yml",
        "codefresh/templates/*/pipelines/release.yml",
        "codefresh/templates/*/pipelines/release-*.yml");

    /// <summary>
    /// The platform pipelines of the .NET harness and the PowerShell scripts only they run (single-operator exception,
    /// ADR-IR34 test harness): arming and publishing push to &lt;sandbox-app-repo&gt; through sandbox-git.ps1, and the
    /// runbook helper octopus-runbook.ps1 force-sleeps the app clusters through env-sleep.
    /// </summary>
    /// <param name="path">Repository-relative path.</param>
    public static bool IsConformancePipeline(string path) => CasePattern.Matches(
        path,
        "codefresh/platform/pipelines/conformance*.yml",
        "codefresh/platform/scripts/conformance-*.ps1",
        "codefresh/platform/scripts/sandbox-git.ps1",
        "codefresh/platform/scripts/octopus-runbook.ps1");

    /// <summary>Files that declare Codefresh contexts by variable name, never by value.</summary>
    /// <param name="path">Repository-relative path.</param>
    public static bool IsContextDefinition(string path) => CasePattern.Matches(
        path,
        "codefresh/register.sh",
        "codefresh/platform/integrations.yaml",
        "codefresh/apps/*/integrations.yaml");

    /// <summary>The app names of <c>apps/*.yaml</c> (their <c>name:</c> line), without the conformance fixture <c>sandbox</c>.</summary>
    /// <param name="tree">The repository tree.</param>
    public static IReadOnlyList<string> AppNames(BoundaryTree tree)
    {
        var folder = tree.FullPath("apps");
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var name = PosixPatterns.Ere("^name:[[:space:]]*([a-z][a-z0-9]*)[[:space:]]*(#.*)?$");
        return Directory.EnumerateFiles(folder, "*.yaml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(file => file.EndsWith(".yaml", StringComparison.Ordinal) && !file.StartsWith('.'))
            .Order(StringComparer.Ordinal)
            .Select(file => tree.Lines($"apps/{file}")?.Select(line => name.Match(line)).FirstOrDefault(match => match.Success)?.Groups[1].Value)
            .OfType<string>()
            .Where(app => app != "sandbox")
            .ToArray();
    }

    private static BoundaryRule Grep(string id, string description, string ere, bool withComments, params string[] specs) =>
        new(id, description, tree =>
        {
            var targets = tree.Targets(specs);
            if (targets.Count == 0)
            {
                return BoundaryResult.Skip(id, description, string.Join(' ', specs));
            }

            var hits = tree.Search(PosixPatterns.Ere(ere), targets);
            return BoundaryResult.Of(id, description, (withComments ? hits : BoundaryTree.WithoutComments(hits)).Select(hit => hit.Finding(id)));
        });

    /// <summary>
    /// TB09: Octopus scoping annotations are rendered only by the tenant chart (§7.0 "Wake and pins"); no tenant annotation
    /// anywhere (ADR-C8).
    /// </summary>
    private static BoundaryResult OctopusAnnotations(BoundaryTree tree)
    {
        const string Id = "TB09";
        var description = Describe(Id);
        string[] specs = ["argocd", "gitops", "policies", "terraform", "octopus", ".octopus", "codefresh"];
        var paths = tree.Targets(specs);
        if (paths.Count == 0)
        {
            return BoundaryResult.Skip(Id, description, string.Join(' ', specs));
        }

        var outside = BoundaryTree.WithoutComments(tree.Search(PosixPatterns.Ere(@"argo\.octopus\.com/"), paths))
            .Select(hit => hit.Path)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Where(path => !CasePattern.Matches(path, "gitops/platform/tenant/*"))
            .Select(path => new BoundaryFinding(Id, path, null, "carries argo.octopus.com/* outside the tenant chart"));
        var tenant = BoundaryTree.WithoutComments(tree.Search(PosixPatterns.Ere(@"argo\.octopus\.com/tenant"), paths)).Select(hit => hit.Finding(Id));
        return BoundaryResult.Of(Id, description, outside.Concat(tenant));
    }

    /// <summary>TB13c: the stored variable sets are included in no project (design §5.3, R5).</summary>
    private static BoundaryResult LibrarySets(BoundaryTree tree)
    {
        const string Id = "TB13c";
        var description = Describe(Id);
        var files = tree.FilesIn("octopus", ".octopus").Where(file => file.EndsWith(".tf", StringComparison.Ordinal) || file.EndsWith(".ocl", StringComparison.Ordinal)).ToArray();
        if (files.Length == 0)
        {
            return BoundaryResult.Skip(Id, description, "octopus .octopus");
        }

        var header = PosixPatterns.Ere("included_library_variable_sets|included_variable_sets|IncludedLibraryVariableSet");
        var stored = PosixPatterns.Ere("runtime[_ -]?provisioning|aisf");
        var findings = new List<BoundaryFinding>();
        foreach (var file in files)
        {
            var collecting = false;
            var buffer = string.Empty;
            var start = 0;
            var lines = tree.Lines(file) ?? [];
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                if (header.IsMatch(line))
                {
                    collecting = true;
                    buffer = string.Empty;
                    start = index + 1;
                }

                if (!collecting)
                {
                    continue;
                }

                buffer += " " + line;
                if (line.Contains(']', StringComparison.Ordinal) || line.Contains(')', StringComparison.Ordinal))
                {
                    if (stored.IsMatch(AsciiLower(buffer)))
                    {
                        findings.Add(new BoundaryFinding(Id, file, start, buffer));
                    }

                    collecting = false;
                }
            }
        }

        return BoundaryResult.Of(Id, description, findings);
    }

    /// <summary>
    /// TB14: the only Octopus API key in Codefresh is OCTOPUS_API_KEY from context platform-octopus (ADR-IR32, ADR-IR34
    /// decision 4), in app and starter release pipelines (handoff, wake_nonprod) and the conformance pipelines, where it
    /// may also go out as the X-Octopus-ApiKey header; the context definitions (integrations.yaml, register.sh) name the
    /// variable without a value. Everything else under codefresh/ and containers/ keeps the ban, and no file may use
    /// another key name, a command-line key option (<c>--api-key</c>, or the PowerShell parameter <c>-ApiKey</c>) or a
    /// literal key.
    /// </summary>
    private static BoundaryResult OctopusApiKey(BoundaryTree tree)
    {
        const string Id = "TB14";
        var description = Describe(Id);
        var paths = tree.Targets("codefresh", "containers");
        if (paths.Count == 0)
        {
            return BoundaryResult.Skip(Id, description, "codefresh containers");
        }

        var pattern = PosixPatterns.Ere("OCTOPUS_API_KEY|OCTO_API_KEY|X-Octopus-ApiKey|--api-?[Kk]ey([[:space:]=]|$)|(^|[^[:alnum:]_-])-[Aa]pi-?[Kk]ey([[:space:]=:]|$)|API-[A-Z0-9]{16,}");
        var findings = BoundaryTree.WithoutComments(tree.Search(pattern, paths))
            .Where(hit => !((IsReleasePipeline(hit.Path) || IsConformancePipeline(hit.Path) || IsContextDefinition(hit.Path))
                && !pattern.IsMatch(hit.Text.Replace("OCTOPUS_API_KEY", string.Empty, StringComparison.Ordinal).Replace("X-Octopus-ApiKey", string.Empty, StringComparison.Ordinal))))
            .Select(hit => hit.Finding(Id));
        return BoundaryResult.Of(Id, description, findings);
    }

    /// <summary>TB15: admission never rewrites desired state (ADR-D11: no mutateDigest).</summary>
    private static BoundaryResult MutateDigest(BoundaryTree tree)
    {
        const string Id = "TB15";
        var description = Describe(Id);
        var paths = tree.Targets("policies", "gitops/platform");
        if (paths.Count == 0)
        {
            return BoundaryResult.Skip(Id, description, "policies gitops/platform");
        }

        var findings = BoundaryTree.WithoutComments(tree.Search(PosixPatterns.Ere("mutateDigest:[[:space:]]*true"), paths)).Select(hit => hit.Finding(Id));
        var explicitFalse = PosixPatterns.Ere("mutateDigest:[[:space:]]*false");
        var warnings = tree.Search(PosixPatterns.Ere("kind:[[:space:]]*ImageValidatingPolicy|verifyImages:"), paths, pruneFolders: false)
            .Select(hit => hit.Path)
            .Distinct(StringComparer.Ordinal)
            .Where(path => !(tree.Lines(path) ?? []).Any(line => explicitFalse.IsMatch(line)))
            .Select(path => $"{path}: verifies images without an explicit 'mutateDigest: false'");
        return BoundaryResult.Of(Id, description, findings, warnings);
    }

    /// <summary>
    /// TB16: Codefresh reads repositories and posts statuses; it never commits or pushes. Exception: the conformance
    /// pipelines push the run's sandbox commits and results to &lt;sandbox-app-repo&gt; only (ADR-IR34 test harness).
    /// Besides <c>git push</c> and <c>git commit</c> the rule matches a Git wrapper (<c>sandbox_git push</c>,
    /// <c>Invoke-SandboxGit push</c>) and quoted or array arguments (<c>&amp; 'git' 'push'</c>, <c>git @('commit', ...)</c>).
    /// </summary>
    private static BoundaryResult NoGitWrites(BoundaryTree tree)
    {
        const string Id = "TB16";
        var description = Describe(Id);
        var paths = tree.Targets("codefresh");
        if (paths.Count == 0)
        {
            return BoundaryResult.Skip(Id, description, "codefresh");
        }

        var pattern = PosixPatterns.Ere($$"""[Gg]it(\.exe)?{{Q}}?[[:space:]]+(@?\([[:space:]]*)?{{Q}}?(push|commit){{Q}}?([^[:alnum:]_-]|$)|type:[[:space:]]*{{Q}}?git-commit""");
        return BoundaryResult.Of(Id, description, BoundaryTree.WithoutComments(tree.Search(pattern, paths))
            .Where(hit => !IsConformancePipeline(hit.Path))
            .Select(hit => hit.Finding(Id)));
    }

    /// <summary>TB17: only env-wake and env-sleep start or stop a cluster or toggle the alert suppression rule.</summary>
    private static BoundaryResult ClusterPower(BoundaryTree tree)
    {
        const string Id = "TB17";
        var description = Describe(Id);
        var files = tree.FilesIn(PowerAndRunbookPaths);
        if (files.Count == 0)
        {
            return BoundaryResult.Skip(Id, description, string.Join(' ', PowerAndRunbookPaths));
        }

        var pattern = PosixPatterns.Ere("""az[[:space:]]+aks[[:space:]]+(start|stop)([[:space:]]|$)|(Start|Stop)-AzAksCluster|managedClusters/[^[:space:]"'/]+/(start|stop)([^[:alnum:]]|$)|alert-processing-rule[[:space:]]+(update|create|delete)|(Set|Update|New|Remove)-AzAlertProcessingRule|AlertsManagement/actionRules""");
        string[] allowed = [".octopus/platform-infrastructure/runbooks/env-wake.ocl", ".octopus/platform-infrastructure/runbooks/env-sleep.ocl"];
        return BoundaryResult.Of(Id, description, tree.SearchInContext(pattern, files)
            .Where(hit => !allowed.Contains(hit.Path, StringComparer.Ordinal))
            .Select(hit => hit.Finding(Id)));
    }

    /// <summary>
    /// TB18: Octopus REST calls that run runbooks appear only in platform-owned places: the platform-infrastructure
    /// runbooks, step run-env-wake of platform-wake, step wake_nonprod of app and starter release pipelines, and the
    /// conformance pipelines. App projects wake without a key, through a Deploy a Release of platform-wake.
    /// </summary>
    private static BoundaryResult RunbookRuns(BoundaryTree tree)
    {
        const string Id = "TB18";
        var description = Describe(Id);
        var files = tree.FilesIn(PowerAndRunbookPaths);
        if (files.Count == 0)
        {
            return BoundaryResult.Skip(Id, description, string.Join(' ', PowerAndRunbookPaths));
        }

        var pattern = PosixPatterns.Ere("""runbookRuns|runbook-runs|/runbooks/[^[:space:]"']*/run([/?"'[:space:]]|$)|octopus[[:space:]]+runbook[[:space:]]+run|run-runbook""");
        return BoundaryResult.Of(Id, description, tree.SearchInContext(pattern, files)
            .Where(hit => !IsPlatformRunbookRun(hit))
            .Select(hit => hit.Finding(Id)));
    }

    private static bool IsPlatformRunbookRun(ContextHit hit) =>
        CasePattern.Matches(hit.Path, ".octopus/platform-infrastructure/runbooks/*.ocl")
        || (hit.Path == ".octopus/platform-wake/deployment_process.ocl" && hit.Context == "run-env-wake")
        || IsConformancePipeline(hit.Path)
        || (IsReleasePipeline(hit.Path) && CasePattern.Matches(hit.Context, "*/wake_nonprod", "*/wake_nonprod/*"));

    /// <summary>
    /// TB19: app projects and starters never hold Azure rights that can start a cluster (sleep/wake contract): no platform
    /// lifecycle account, no stored provisioner; app identities get no AKS-capable role and nothing on the cluster groups.
    /// </summary>
    private static BoundaryResult NoStartRights(BoundaryTree tree)
    {
        const string Id = "TB19";
        var description = Describe(Id);
        var octopus = tree.FilesIn(".octopus/apps", "octopus/templates");
        var grants = tree.FilesIn("terraform/apps").Where(file => file.EndsWith(".tf", StringComparison.Ordinal)).ToArray();
        if (octopus.Count == 0 && grants.Length == 0)
        {
            return BoundaryResult.Skip(Id, description, ".octopus/apps octopus/templates terraform/apps");
        }

        var accounts = BoundaryTree.WithoutComments(tree.Search(PosixPatterns.Ere(@"Azure\.LifecycleAccount|azure-platform-lifecycle-|azure-runtime-provisioner|Azure Runtime Provisioner"), octopus))
            .Select(hit => hit.Finding(Id));
        return BoundaryResult.Of(Id, description, accounts.Concat(grants.SelectMany(file => StartRoleAssignments(tree, file))));
    }

    private static IEnumerable<BoundaryFinding> StartRoleAssignments(BoundaryTree tree, string file)
    {
        var resource = PosixPatterns.Ere("^resource[ \t]+\"azurerm_role_assignment\"");
        var scopeLine = PosixPatterns.Ere("^[ \t]*scope[ \t]*=");
        var roleLine = PosixPatterns.Ere("^[ \t]*role_definition_name[ \t]*=");
        var startRole = PosixPatterns.Ere("\"(Owner|User Access Administrator|Role Based Access Control Administrator|Azure Kubernetes Service[^\"]*)\"");
        var clusterScope = PosixPatterns.Ere("(_aks|-aks|managedClusters|kubernetes_cluster)");
        var inBlock = false;
        var scope = string.Empty;
        var role = string.Empty;
        var start = 0;
        var lines = tree.Lines(file) ?? [];
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (resource.IsMatch(line))
            {
                inBlock = true;
                scope = string.Empty;
                role = string.Empty;
                start = index + 1;
                continue;
            }

            if (!inBlock)
            {
                continue;
            }

            if (scopeLine.IsMatch(line))
            {
                scope = line;
            }

            if (roleLine.IsMatch(line))
            {
                role = line;
            }

            if (line.StartsWith('}'))
            {
                if (startRole.IsMatch(role) || clusterScope.IsMatch(scope))
                {
                    yield return new BoundaryFinding("TB19", file, start, $" grants{role} at{scope}");
                }

                inBlock = false;
            }
        }
    }

    /// <summary>
    /// TB20: the Space Manager key stays in platform projects (ADR-IR32, ADR-IR34 decisions 17 and 24):
    /// Platform.OctopusApiKey only in platform-infrastructure, PlatformWake.* only in platform-wake, the X-Octopus-ApiKey
    /// header only in those two; never in an app project or a starter. No literal key anywhere.
    /// </summary>
    private static BoundaryResult PlatformKey(BoundaryTree tree)
    {
        const string Id = "TB20";
        var description = Describe(Id);
        var octopus = tree.FilesIn(".octopus", "octopus/templates");
        var rest = tree.FilesIn("octopus", "terraform", "argocd", "gitops", "policies", "apps", "fixtures");
        if (octopus.Count == 0 && rest.Count == 0)
        {
            return BoundaryResult.Skip(Id, description, ".octopus octopus terraform argocd gitops policies apps fixtures");
        }

        var wake = PosixPatterns.Ere(@"PlatformWake\.");
        var infrastructureKey = PosixPatterns.Ere(@"Platform\.OctopusApiKey");
        var keyVariables = tree.SearchInContext(PosixPatterns.Ere(@"Platform\.OctopusApiKey|PlatformWake\.|X-Octopus-ApiKey"), octopus)
            .Where(hit =>
            {
                var content = $"{hit.Context}:{hit.Text}";
                if (CasePattern.Matches(hit.Path, ".octopus/platform-infrastructure/*"))
                {
                    return wake.IsMatch(content);
                }

                return !CasePattern.Matches(hit.Path, ".octopus/platform-wake/*") || infrastructureKey.IsMatch(content);
            })
            .Select(hit => hit.Finding(Id));
        var literalKeys = tree.Search(PosixPatterns.Ere("API-[A-Z0-9]{16,}"), octopus.Concat(rest)).Select(hit => hit.Finding(Id));
        return BoundaryResult.Of(Id, description, keyVariables.Concat(literalKeys));
    }

    /// <summary>
    /// TB21: trust boundary TB2 (design §5.1, ADR-IR34 build runner): the runner runs in aks-platform-build with one runtime,
    /// the build cluster holds no role assignment, and pipelines get no cloud identity; registry pushes use the tokens of
    /// Codefresh registry integrations. The conformance pipelines (context platform-conformance) are the recorded
    /// single-operator exception.
    /// </summary>
    private static BoundaryResult BuildCluster(BoundaryTree tree)
    {
        const string Id = "TB21";
        var description = Describe(Id);
        var specFile = PosixPatterns.Ere(@"/specs/[^/]+\.ya?ml$");
        var specs = tree.FilesIn("codefresh").Where(file => specFile.IsMatch("/" + file)).ToArray();
        var build = tree.FilesIn("terraform/build").Where(file => file.EndsWith(".tf", StringComparison.Ordinal)).ToArray();
        var runner = tree.Targets("codefresh/runner");
        var appPipelines = tree.Targets("codefresh/apps", "codefresh/templates");
        if (specs.Length + build.Length + runner.Count + appPipelines.Count == 0)
        {
            return BoundaryResult.Skip(Id, description, "codefresh terraform/build");
        }

        var cfRuntime = PosixPatterns.Ere(CfRuntime);
        var runtimes = specs
            .Select(spec => (Spec: spec, Runtime: RuntimeName(tree.Lines(spec) ?? [])))
            .Where(entry => !cfRuntime.IsMatch(entry.Runtime))
            .Select(entry => new BoundaryFinding(Id, entry.Spec, null, $"runtimeEnvironment.name is '{(entry.Runtime.Length == 0 ? "unset" : entry.Runtime)}', not aks-platform-build/codefresh"));
        var grants = BoundaryTree.WithoutComments(tree.Search(PosixPatterns.Ere("azurerm_role_assignment|azuread_[a-z_]*role_assignment|azurerm_federated_identity_credential"), build));
        var runnerIdentities = BoundaryTree.WithoutComments(tree.Search(PosixPatterns.Ere(@"azure\.workload\.identity|AZURE_CLIENT_(ID|SECRET)|ARM_CLIENT_SECRET|eks\.amazonaws\.com/role-arn|iam\.gke\.io"), runner));
        var pipelineIdentities = BoundaryTree.WithoutComments(tree.Search(PosixPatterns.Ere("az[[:space:]]+login|az[[:space:]]+account[[:space:]]+get-access-token|AZURE_CLIENT_SECRET|ARM_CLIENT_SECRET|ARM_USE_OIDC|azure/login|platform-conformance"), appPipelines));
        return BoundaryResult.Of(Id, description, runtimes.Concat(grants.Concat(runnerIdentities).Concat(pipelineIdentities).Select(hit => hit.Finding(Id))));
    }

    /// <summary>
    /// The first <c>name:</c> under <c>runtimeEnvironment:</c> of a spec, unquoted and without a trailing comment, or an
    /// empty string (the script's awk: the block ends at the next line that starts without white space).
    /// </summary>
    private static string RuntimeName(IReadOnlyList<string> lines)
    {
        var key = PosixPatterns.Ere("^[[:space:]]*runtimeEnvironment:");
        var name = PosixPatterns.Ere("^[[:space:]]*name:");
        var namePrefix = PosixPatterns.Ere("^[[:space:]]*name:[[:space:]]*");
        var trailingComment = PosixPatterns.Ere("[[:space:]]+#.*$");
        var topLevel = PosixPatterns.Ere("^[^[:space:]]");
        var inside = false;
        foreach (var line in lines)
        {
            if (key.IsMatch(line))
            {
                inside = true;
                continue;
            }

            if (inside && name.IsMatch(line))
            {
                var value = namePrefix.Replace(line, string.Empty, 1).Replace("\"", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal);
                return trailingComment.Replace(value, string.Empty, 1);
            }

            if (inside && topLevel.IsMatch(line))
            {
                inside = false;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// TB22: name lint (ADR-IR34, app-neutral platform): a platform file outside the app-scoped paths never names an app.
    /// Apps come from apps/*.yaml; the conformance fixture (a platform component) is exempt. Markdown, design, docs,
    /// contracts, tests and the catalogue may use an app as the labelled example; so may test fixtures inside platform
    /// roots (a <c>tests/</c> folder, such as the Kyverno CLI and <c>terraform test</c> fixtures, which are never deployed)
    /// and any line that says "for example", "e.g." or "such as". Terraform <c>moved</c> blocks and lines marked
    /// <c>name-lint: allow</c> (migration records) may name old objects.
    /// </summary>
    private static BoundaryResult NameLint(BoundaryTree tree)
    {
        const string Id = "TB22";
        var description = Describe(Id);
        var apps = AppNames(tree);
        if (apps.Count == 0)
        {
            return BoundaryResult.Skip(Id, description, "apps/*.yaml");
        }

        var files = tree.FilesIn("argocd", "gitops", ".octopus", "octopus", "codefresh", "containers", "policies", "terraform", "scripts", "tools", "fixtures", "CODEOWNERS", ".gitleaks.toml", ".yamllint.yaml")
            .Where(file => !("/" + file).Contains("/tools/Platform.Onboarding.Tests/", StringComparison.Ordinal))
            .ToArray();
        var moved = PosixPatterns.Ere("^[[:space:]]*(from|to)[[:space:]]*=");
        var findings = new List<BoundaryFinding>();
        foreach (var app in apps)
        {
            var mention = PosixPatterns.Ere($"(^|[^A-Za-z0-9]){app}([^A-Za-z0-9]|$)", ignoreCase: true);
            var example = PosixPatterns.Ere($@"(for example|e\.g\.|such as)[^.;]*(^|[^A-Za-z0-9]){app}([^A-Za-z0-9]|$)", ignoreCase: true);
            findings.AddRange(BoundaryTree.WithoutComments(tree.Search(mention, files))
                .Where(hit => !IsAppScoped(hit.Path, app)
                    && !CasePattern.Matches(hit.Path, "*/tests/*")
                    && !(CasePattern.Matches(hit.Path, "*.tf") && moved.IsMatch(hit.Text))
                    && !hit.Text.Contains("name-lint: allow", StringComparison.Ordinal)
                    && !example.IsMatch(hit.Text))
                .Select(hit => hit.Finding(Id)));
        }

        return BoundaryResult.Of(Id, description, findings);
    }

    /// <summary>An app's own paths: its descriptor and its folders under the app roots of each tool.</summary>
    private static bool IsAppScoped(string path, string app) =>
        path == $"apps/{app}.yaml" || AppRoots.Any(root => path.StartsWith($"{root}{app}/", StringComparison.Ordinal));

    private static string Describe(string id) => All.Single(rule => rule.Id == id).Description;

    private static string AsciiLower(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var index = 0; index < source.Length; index++)
        {
            span[index] = source[index] is >= 'A' and <= 'Z' ? (char)(source[index] + 32) : source[index];
        }
    });
}
