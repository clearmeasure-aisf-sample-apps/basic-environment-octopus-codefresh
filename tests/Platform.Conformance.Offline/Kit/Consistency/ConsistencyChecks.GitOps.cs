using System.Text;
using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Consistency;

/// <summary>Checks of the Argo CD and GitOps files: C02 to C06 and C09 to C11.</summary>
internal sealed partial class ConsistencyChecks
{
    /// <summary>Kinds that AppProject <c>app-&lt;app&gt;</c> denies because they are cluster-scoped (C09).</summary>
    private static readonly HashSet<string> ClusterScoped = new(StringComparer.Ordinal)
    {
        "Namespace", "ClusterRole", "ClusterRoleBinding", "CustomResourceDefinition", "StorageClass", "PersistentVolume",
        "ValidatingWebhookConfiguration", "MutatingWebhookConfiguration", "ClusterSecretStore", "ClusterPolicy", "ClusterIssuer",
        "PriorityClass", "ClusterExternalSecret", "ImageValidatingPolicy", "ValidatingPolicy", "ValidatingAdmissionPolicy", "APIService",
    };

    /// <summary>Namespaced kinds that only the platform owns (C09).</summary>
    private static readonly HashSet<string> DeniedNamespaced = new(StringComparer.Ordinal)
    {
        "SecretStore", "ResourceQuota", "LimitRange", "NetworkPolicy", "Policy", "PolicyException",
    };

    /// <summary>C02: the tenant chart renders the Octopus annotations, and no platform file uses a forbidden one (ADR-C8).</summary>
    private void C02Annotations(Report report)
    {
        var chart = Py.AsStr(At(contracts, "argocd", "tenantChart"));
        if (!files.Exists(chart))
        {
            report.Absent(chart + "/Chart.yaml", "tenant chart");
        }
        else
        {
            var text = string.Join('\n', files.Walk(chart, null).Select(relative => files.CodeText(relative) ?? string.Empty));
            var missing = Py.Iterate(At(contracts, "argocd", "annotations", "keys")).Where(key => !Py.In(key, text)).ToList();
            report.Verdict(chart + "/", missing.Select(key => $"never renders {Py.Str(key)}").ToList(), "the tenant chart renders the Octopus annotations");
        }

        var hits = new List<string>();
        foreach (var root in PlatformConfigRoots)
        {
            foreach (var relative in files.Walk(root, null))
            {
                if (relative.EndsWith(".md", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = files.CodeText(relative) ?? string.Empty;
                foreach (var key in Py.Iterate(At(contracts, "argocd", "annotations", "forbidden")))
                {
                    if (Py.In(key, text))
                    {
                        hits.Add($"{relative}: {Py.Str(key)}");
                    }
                }
            }
        }

        report.Verdict("-", hits, "no argo.octopus.com/tenant annotation anywhere (ADR-C8)");
    }

    /// <summary>C03: ApplicationSet <c>apps</c> of each tier reads apps/*.yaml and renders tenant-&lt;app&gt; in platform-tenants.</summary>
    private void C03ApplicationSet(Report report)
    {
        var spec = At(contracts, "argocd", "applicationSet");
        foreach (var tier in Tiers())
        {
            var relative = X(Py.Item(spec, "file"), ("tier", tier));
            if (!files.Exists(relative))
            {
                report.Absent(relative, $"ApplicationSet {Py.Str(Py.Item(spec, "name"))} ({tier})");
                continue;
            }

            var errors = new List<string>();
            var appSets = (report.Docs(relative) ?? []).OfType<PyDict>().Where(document => Py.Eq(Py.Get(document, "kind"), "ApplicationSet")).ToList();
            var appSet = appSets.FirstOrDefault(document => Py.Eq(Py.G(document, null, "metadata", "name"), Py.Item(spec, "name")));
            if (appSet is null)
            {
                report.Out(FindingStatus.Fail, relative, $"no ApplicationSet named {Py.Str(Py.Item(spec, "name"))}");
                continue;
            }

            var generators = Py.AllDicts(Py.G(appSet, new List<object?>(), "spec", "generators")).Where(generator => generator.ContainsKey("git")).ToList();
            var paths = new List<object?>();
            foreach (var generator in generators)
            {
                foreach (var file in Py.Iterate(Py.Or(Py.G(generator, new List<object?>(), "git", "files"), new List<object?>())))
                {
                    if (file is PyDict entry)
                    {
                        paths.Add(Py.Get(entry, "path"));
                    }
                }
            }

            if (!Py.In("apps/*.yaml", paths))
            {
                errors.Add($"git files generator must read apps/*.yaml (found {Py.Repr(paths)})");
            }

            var values = generators.Select(generator => Py.Or(Py.G(generator, new PyDict(), "git", "values"), new PyDict())).ToList();
            if (!values.Any(value => Py.Str(Py.Get(value, "cluster")) == tier))
            {
                errors.Add($"generator values must set cluster: {tier}");
            }

            var sync = Py.Or(Py.G(appSet, new PyDict(), "spec", "syncPolicy"), new PyDict());
            if (!Py.Eq(Py.Get(sync, "applicationsSync"), Py.Item(spec, "applicationsSync")))
            {
                errors.Add($"syncPolicy.applicationsSync must be {Py.Str(Py.Item(spec, "applicationsSync"))}");
            }

            if (Py.Get(sync, "preserveResourcesOnDeletion") is not true)
            {
                errors.Add("syncPolicy.preserveResourcesOnDeletion must be true");
            }

            var template = ScalarText(Py.Or(Py.G(appSet, new PyDict(), "spec", "template"), new PyDict()))
                + Py.Str(Py.Or(Py.G(appSet, string.Empty, "spec", "templatePatch"), string.Empty));
            if (!template.Contains("platform-tenants", StringComparison.Ordinal))
            {
                errors.Add("the template's project must be platform-tenants");
            }

            if (!template.Contains("tenant-", StringComparison.Ordinal))
            {
                errors.Add("the template must name Applications tenant-<app>");
            }

            report.Verdict(relative, errors, $"ApplicationSet {Py.Str(Py.Item(spec, "name"))} renders one tenant per descriptor on {tier}");
        }
    }

    /// <summary>C04: each tier's projects.yaml holds exactly the platform AppProjects; <c>default</c> stays locked.</summary>
    private void C04AppProjects(Report report)
    {
        var allowed = new PySet(Py.Iterate(At(contracts, "argocd", "appProjects", "platform")));
        foreach (var tier in Tiers())
        {
            var relative = $"argocd/clusters/{tier}/projects.yaml";
            if (!files.Exists(relative))
            {
                report.Absent(relative, "AppProjects");
                continue;
            }

            var projects = (report.Docs(relative) ?? []).OfType<PyDict>().Where(document => Py.Eq(Py.Get(document, "kind"), "AppProject")).ToList();
            var names = new PySet(projects.Select(project => Py.G(project, null, "metadata", "name")));
            var errors = Py.Sorted(allowed.Except(names).Items).Select(name => $"missing AppProject {Py.Str(name)}").ToList();
            errors.AddRange(Py.Sorted(names.Except(allowed).Items).Select(name => $"AppProject {Py.Str(name)} is not a platform project (the tenant chart renders app-<app>)"));
            var locked = projects.FirstOrDefault(project => Py.Eq(Py.G(project, null, "metadata", "name"), "default"));
            if (locked is not null && (Py.Truthy(Py.G(locked, null, "spec", "sourceRepos")) || Py.Truthy(Py.G(locked, null, "spec", "destinations"))))
            {
                errors.Add("AppProject default must stay locked (no sourceRepos, no destinations)");
            }

            report.Verdict(relative, errors, $"platform AppProjects {Py.Repr(Py.Sorted(allowed.Items))} only");
        }
    }

    /// <summary>C05: each tier's namespaces.yaml holds the labelled platform namespaces and no app namespace.</summary>
    private void C05Namespaces(Report report)
    {
        var kubernetes = At(contracts, "kubernetes");
        var bootstrap = new PySet(Py.Iterate(Py.Get(kubernetes, "bootstrapNamespaces", new List<object?>())));
        var want = Py.Iterate(Py.Item(kubernetes, "platformNamespaces")).Where(name => !bootstrap.Contains(name)).ToList<object?>();
        var label = At(kubernetes, "namespaceLabels", "platform");
        var appNamespace = apps.Count > 0 ? new Regex("^(" + string.Join('|', apps.Keys.Select(app => Regex.Escape((string)app!))) + ")-") : null;
        foreach (var tier in Tiers())
        {
            var relative = $"argocd/clusters/{tier}/namespaces.yaml";
            if (!files.Exists(relative))
            {
                report.Absent(relative, "platform namespaces");
                continue;
            }

            var found = new PyDict();
            foreach (var document in (report.Docs(relative) ?? []).OfType<PyDict>().Where(document => Py.Eq(Py.Get(document, "kind"), "Namespace")))
            {
                found.Set(Py.G(document, null, "metadata", "name"), document);
            }

            var errors = want.Where(name => !found.ContainsKey(name)).Select(name => $"missing namespace {Py.Str(name)}").ToList();
            foreach (var (name, document) in found.Items)
            {
                var labels = Py.Or(Py.G(document, new PyDict(), "metadata", "labels"), new PyDict());
                if (Py.In(name, want) && Py.Items(label).Any(pair => Py.Str(Py.Get(labels, pair.Key)) != Py.Str(pair.Value)))
                {
                    errors.Add($"{Py.Str(name)} lacks label tier: platform");
                }

                if (appNamespace is not null && appNamespace.IsMatch(Py.Str(name)))
                {
                    errors.Add($"{Py.Str(name)} is an app namespace; the tenant chart creates app namespaces");
                }
            }

            report.Verdict(relative, errors, "platform namespaces present and labelled; no app namespace");
        }
    }

    /// <summary>C06: the root application points at the tier's folder; the Argo CD bootstrap values match §7.0.</summary>
    private void C06Bootstrap(Report report)
    {
        var root = At(contracts, "argocd", "rootApplication");
        foreach (var tier in Tiers())
        {
            var relative = X(Py.Item(root, "valuesFile"), ("tier", tier));
            var text = files.CodeText(relative);
            if (text is null)
            {
                report.Absent(relative, "root application values");
            }
            else
            {
                var errors = new[] { Py.Item(root, "name"), X(Py.Item(root, "path"), ("tier", tier)) }
                    .Where(needle => !Py.In(needle, text))
                    .Select(needle => $"missing {Py.Str(needle)}")
                    .ToList();
                report.Verdict(relative, errors, $"{Py.Str(Py.Item(root, "name"))} points at {X(Py.Item(root, "path"), ("tier", tier))}");
            }

            relative = X(At(contracts, "argocd", "bootstrapValues"), ("tier", tier));
            text = files.CodeText(relative);
            if (text is null)
            {
                report.Absent(relative, "Argo CD bootstrap values");
                continue;
            }

            var problems = new List<string>();
            if (!ReconciliationTimeout().IsMatch(text))
            {
                problems.Add("timeout.reconciliation must be 30s");
            }

            if (!AdminDisabled().IsMatch(text))
            {
                problems.Add("admin.enabled must be false");
            }

            if (!OctopusAccount().IsMatch(text))
            {
                problems.Add("accounts.octopus must be apiKey");
            }

            foreach (var policy in Py.Iterate(At(contracts, "argocd", "gatewayPolicies")))
            {
                if (!Py.In(policy, text))
                {
                    problems.Add($"RBAC lacks '{Py.Str(policy)}'");
                }
            }

            report.Verdict(relative, problems, "bootstrap values match §7.0 (reconciliation, admin off, octopus account and app-*/* policies)");
        }
    }

    /// <summary>
    /// C09: every Kustomize overlay of every app renders (kustomize build) into its own namespace, with no cluster-scoped
    /// or platform-owned kind, ExternalSecrets that read ClusterSecretStore &lt;app&gt;-&lt;env&gt;, no tag latest and no
    /// image of the registry outside apps/&lt;app&gt;/ or platform/.
    /// </summary>
    private void C09RenderedOverlays(Report report)
    {
        if (!render || kustomize is null)
        {
            report.Out(render ? FindingStatus.Warn : FindingStatus.Skip, "gitops/apps/", "kustomize not available; rendered overlays not checked");
            return;
        }

        var registry = Py.Str(At(contracts, "azure", "registry", "loginServer"));
        foreach (var (app, descriptor) in Apps())
        {
            var targets = new List<(string Path, string Environment, string Namespace)>();
            foreach (var environment in AppEnvironments(descriptor))
            {
                foreach (var deployable in Deployables(descriptor))
                {
                    if (Py.Eq(Py.Get(deployable, "packaging"), "kustomize"))
                    {
                        targets.Add(($"gitops/apps/{app}/envs/{environment}/{Py.Str(Py.Get(deployable, "name"))}", environment, Namespace(app, environment, Py.Get(deployable, "part"))));
                    }
                }

                if (Py.Truthy(Py.Get(descriptor, "database")))
                {
                    targets.Add(($"gitops/apps/{app}/envs/{environment}/db", environment, Namespace(app, environment)));
                }
            }

            foreach (var (relative, environment, ns) in targets)
            {
                if (!files.IsFile(relative + "/kustomization.yaml"))
                {
                    continue;
                }

                var (rendered, error) = Kustomize.Build(kustomize, files.PathOf(relative));
                if (rendered is null)
                {
                    report.Out(FindingStatus.Fail, relative, $"kustomize build failed: {error}");
                    continue;
                }

                var errors = new List<string>();
                foreach (var document in rendered)
                {
                    var kind = Py.Str(Py.Get(document, "kind"));
                    var name = Py.Str(Py.G(document, null, "metadata", "name"));
                    var api = Py.Str(Py.Get(document, "apiVersion", string.Empty));
                    var target = Py.G(document, null, "metadata", "namespace");
                    if (ClusterScoped.Contains(kind))
                    {
                        errors.Add($"{kind}/{name} is cluster-scoped (AppProject app-{app} denies it)");
                    }
                    else if (DeniedNamespaced.Contains(kind) || api.Contains("kyverno.io", StringComparison.Ordinal))
                    {
                        errors.Add($"{kind}/{name} is platform-owned (AppProject app-{app} denies it)");
                    }
                    else if (target is not null && !Py.Eq(target, ns))
                    {
                        errors.Add($"{kind}/{name} targets namespace {Py.Str(target)}, not {ns}");
                    }

                    if (kind == "ExternalSecret")
                    {
                        var reference = Py.Or(Py.G(document, new PyDict(), "spec", "secretStoreRef"), new PyDict());
                        if (!Py.Eq(Py.Get(reference, "kind"), "ClusterSecretStore") || !Py.Eq(Py.Get(reference, "name"), $"{app}-{environment}"))
                        {
                            errors.Add($"ExternalSecret/{name} must read ClusterSecretStore {app}-{environment} (found {Py.Str(Py.Get(reference, "kind"))} {Py.Str(Py.Get(reference, "name"))})");
                        }
                    }

                    foreach (var dict in Py.AllDicts(document))
                    {
                        if (Py.Get(dict, "image") is not string image)
                        {
                            continue;
                        }

                        if (image.EndsWith(":latest", StringComparison.Ordinal) || image.Contains(":latest@", StringComparison.Ordinal))
                        {
                            errors.Add($"{kind}/{name} uses the tag latest");
                        }

                        if (image.StartsWith(registry + "/", StringComparison.Ordinal) && !image.StartsWith($"{registry}/apps/{app}/", StringComparison.Ordinal)
                            && !image.StartsWith($"{registry}/platform/", StringComparison.Ordinal))
                        {
                            errors.Add($"{kind}/{name} uses {image}, outside apps/{app}/");
                        }
                    }
                }

                report.Verdict(relative, errors.Distinct(StringComparer.Ordinal).Order(Py.StringOrder).ToList(), $"renders into {ns} inside the app fence");
            }
        }
    }

    /// <summary>C10: connection strings of an app's desired state name hosts in the app's own namespaces (WARN for a host other than db).</summary>
    private void C10DatabaseEndpoints(Report report)
    {
        if (!files.IsDirectory("gitops/apps"))
        {
            report.Out(FindingStatus.Skip, "gitops/apps/", "no app desired state yet");
            return;
        }

        foreach (var (app, _) in Apps())
        {
            if (!files.IsDirectory($"gitops/apps/{app}"))
            {
                continue;
            }

            var errors = new List<string>();
            var warnings = new List<string>();
            foreach (var relative in files.Walk($"gitops/apps/{app}", RepositoryFiles.Yaml))
            {
                foreach (Match match in ServerPattern().Matches(files.CodeText(relative) ?? string.Empty))
                {
                    var host = match.Groups[1].Value.Split(',')[0];
                    var labels = host.Split('.');
                    if (host.StartsWith('<') || host.StartsWith('$') || host.StartsWith('{'))
                    {
                        continue;
                    }

                    if (labels.Length > 1 && !labels[1].StartsWith($"{app}-", StringComparison.Ordinal)
                        && !(labels[1].StartsWith('<') || labels[1].StartsWith('$') || labels[1].StartsWith('{')))
                    {
                        errors.Add($"{relative}: database host {host} is in another app's namespace");
                    }
                    else if (labels[0] != "db")
                    {
                        warnings.Add($"{relative}: database host {host} is not the platform database Service db");
                    }
                }
            }

            report.Verdict($"gitops/apps/{app}/", errors, "connection strings stay inside the app's own namespaces");
            if (warnings.Count > 0 && errors.Count == 0)
            {
                report.Out(FindingStatus.Warn, $"gitops/apps/{app}/", string.Join("; ", warnings));
            }
        }
    }

    /// <summary>
    /// C11: platform ExternalSecrets read only the tier's platform vault; an app's desired state defines no store and its
    /// ExternalSecrets read only stores &lt;app&gt;-&lt;env&gt;.
    /// </summary>
    private void C11StoresAndVaults(Report report)
    {
        foreach (var tier in Tiers())
        {
            var relative = $"argocd/clusters/{tier}/platform-secrets.yaml";
            var text = files.CodeText(relative);
            if (text is null)
            {
                report.Absent(relative, "platform secrets");
                continue;
            }

            var errors = new List<string>();
            var match = PlatformVaultName().Match(files.CodeText($"terraform/tier/{tier}.tfvars") ?? string.Empty);
            var vault = match.Success && !match.Groups[1].Value.StartsWith('<') ? match.Groups[1].Value : X("<kv-platform-{tier}>", ("tier", tier));
            if (!text.Contains(vault, StringComparison.Ordinal))
            {
                errors.Add($"platform secrets must come from {vault} (<kv-platform-{tier}>)");
            }

            if (AppVaultName().IsMatch(text))
            {
                errors.Add("names an app vault; app vaults are read only through ClusterSecretStores <app>-<env>");
            }

            report.Verdict(relative, errors, $"platform ExternalSecrets read <kv-platform-{tier}> only");
        }

        foreach (var (app, _) in Apps())
        {
            if (!files.IsDirectory($"gitops/apps/{app}"))
            {
                continue;
            }

            var errors = new List<string>();
            foreach (var relative in files.Walk($"gitops/apps/{app}", RepositoryFiles.Yaml))
            {
                foreach (var document in (report.Docs(relative) ?? []).OfType<PyDict>())
                {
                    var kind = Py.Get(document, "kind");
                    if (Py.Eq(kind, "SecretStore") || Py.Eq(kind, "ClusterSecretStore"))
                    {
                        errors.Add($"{relative}: defines a {Py.Str(kind)}; only the tenant chart does");
                    }

                    if (Py.Eq(kind, "ExternalSecret"))
                    {
                        var reference = Py.Or(Py.G(document, new PyDict(), "spec", "secretStoreRef"), new PyDict());
                        var store = Py.Str(Py.Get(reference, "name", string.Empty));
                        var token = StoreToken().IsMatch(store);
                        if (!store.StartsWith($"{app}-", StringComparison.Ordinal) && !token && !store.Contains('{', StringComparison.Ordinal)
                            && !store.Contains('<', StringComparison.Ordinal) && !store.Contains('$', StringComparison.Ordinal))
                        {
                            errors.Add($"{relative}: ExternalSecret reads store {store}, not {app}-<env>");
                        }
                    }
                }
            }

            report.Verdict($"gitops/apps/{app}/", errors, $"app secrets come only from stores {app}-<env>");
        }
    }

    /// <summary>The template of an ApplicationSet as text for C03's substring checks: every scalar key and value.</summary>
    /// <remarks>
    /// The script searches <c>yaml.safe_dump(template)</c>; the needles contain no space, colon or line break, so they
    /// occur in that dump exactly when they occur in one of its scalars.
    /// </remarks>
    private static string ScalarText(object? node)
    {
        var builder = new StringBuilder();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object?>([node]);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case PyDict dict when seen.Add(dict):
                    foreach (var pair in dict.Items)
                    {
                        pending.Push(pair.Value);
                        pending.Push(pair.Key);
                    }

                    break;
                case List<object?> list when seen.Add(list):
                    list.AsEnumerable().Reverse().ToList().ForEach(pending.Push);
                    break;
                case PyDict or List<object?>:
                    break;
                case var scalar:
                    builder.Append(Py.Str(scalar)).Append('\n');
                    break;
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"timeout\.reconciliation:\s*""?30s")]
    private static partial Regex ReconciliationTimeout();

    [GeneratedRegex(@"admin\.enabled:\s*""?false")]
    private static partial Regex AdminDisabled();

    [GeneratedRegex(@"accounts\.octopus:\s*""?apiKey")]
    private static partial Regex OctopusAccount();

    [GeneratedRegex(@"(?:Server|Data Source)\s*=\s*(?:tcp:)?([A-Za-z0-9.<>{}_-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServerPattern();

    [GeneratedRegex(@"platform_key_vault_name\s*=\s*""([^""]+)""")]
    private static partial Regex PlatformVaultName();

    [GeneratedRegex(@"\bkv-[a-z][a-z0-9]{2,11}-[tup]-")]
    private static partial Regex AppVaultName();

    [GeneratedRegex(@"\A[A-Z0-9_]+\z")]
    private static partial Regex StoreToken();
}
