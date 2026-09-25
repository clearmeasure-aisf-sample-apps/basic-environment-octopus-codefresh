using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Consistency;

/// <summary>Checks that span the platform packages: C18 to C21.</summary>
internal sealed partial class ConsistencyChecks
{
    /// <summary>Files C21 does not read: code and project files hold tags and generics, not placeholders.</summary>
    private static readonly string[] PlaceholderFreeExtensions = [".md", ".sh", ".cs", ".ps1", ".tpl", ".json", ".props", ".targets", ".csproj", ".xml", ".resx", ".config"];

    /// <summary>
    /// C18: the tenant chart renders the per-app signer policy (kind, issuer, subject pattern, Audit and Enforce); the
    /// generic Kyverno policies exist and carry no retired pipeline-ID placeholder.
    /// </summary>
    private void C18SignerAndKyverno(Report report)
    {
        var chart = Py.AsStr(At(contracts, "argocd", "tenantChart"));
        if (!files.Exists(chart))
        {
            report.Absent(chart + "/Chart.yaml", "tenant chart (per-app signer policy)");
        }
        else
        {
            var text = string.Join('\n', files.Walk(chart, null).Select(relative => files.Text(relative) ?? string.Empty));
            var signer = At(contracts, "registry", "signer");
            var errors = new[] { Py.Item(signer, "kind"), Py.Item(signer, "issuer"), "release(-[a-z0-9-]+)?", "[0-9a-f]{24}", "Audit" }
                .Where(needle => !Py.In(needle, text))
                .Select(needle => $"signer template lacks {Py.Str(needle)}")
                .ToList();
            if (!text.Contains("Deny", StringComparison.Ordinal) && !text.Contains("Enforce", StringComparison.Ordinal))
            {
                errors.Add("signer template never enforces (Deny or Enforce in prod)");
            }

            report.Verdict(chart + "/", errors, "per-app signer policy follows §7.0 (issuer, subject pattern, Enforce and Audit)");
        }

        var policies = files.Walk("policies/kyverno", RepositoryFiles.Yaml);
        if (policies.Count == 0)
        {
            report.Absent("policies/kyverno/base/kustomization.yaml", "Kyverno policies");
            return;
        }

        var policyText = string.Join('\n', policies.Select(relative => files.CodeText(relative) ?? string.Empty));
        var problems = new List<string>();
        if (!policyText.Contains("MSSQL_PID", StringComparison.Ordinal))
        {
            problems.Add("no SQL Server edition rule (require-mssql-express)");
        }

        if (!policyText.Contains("apps/", StringComparison.Ordinal))
        {
            problems.Add("no registry-path rule for apps/<app>/");
        }

        if (policyText.Contains("CF_RELEASE_PIPELINE_ID", StringComparison.Ordinal) || policyText.Contains("CF_PREVIEW_PIPELINE_ID", StringComparison.Ordinal))
        {
            problems.Add("retired pipeline-ID placeholders (the tenant chart renders signer subjects)");
        }

        report.Verdict("policies/kyverno/", problems, "generic policies present; per-app signers left to the tenant chart");
    }

    /// <summary>
    /// C19: every Terraform layer under terraform/ exists, declares no Azure SQL, and (terraform/apps/tier) computes vault
    /// names with sha1 and names the database disks; WARN when its state key appears in neither the layer nor the runbooks.
    /// </summary>
    private void C19TerraformLayers(Report report)
    {
        if (!files.IsDirectory("terraform"))
        {
            report.Out(FindingStatus.Skip, "terraform/", "directory absent");
            return;
        }

        var runbooks = string.Join('\n', files.Walk(".octopus/platform-infrastructure", null).Select(relative => files.CodeText(relative) ?? string.Empty));
        var appsTerraform = string.Join('\n', files.Walk("terraform/apps", [".tf", ".hcl"]).Select(relative => files.CodeText(relative) ?? string.Empty));
        foreach (var layer in Py.Iterate(At(contracts, "azure", "terraform", "layers")))
        {
            var path = Py.AsStr(Py.Item(layer, "path"));
            if (!path.StartsWith("terraform/", StringComparison.Ordinal))
            {
                continue;
            }

            var terraform = files.Walk(path, [".tf"]);
            if (terraform.Count == 0)
            {
                report.Out(FindingStatus.Fail, path + "/", "layer missing");
                continue;
            }

            var text = string.Join('\n', terraform.Select(relative => files.CodeText(relative) ?? string.Empty));
            var everything = string.Join('\n', files.Walk(path, null).Select(relative => files.Text(relative) ?? string.Empty)) + runbooks;
            var errors = new List<string>();
            if (text.Contains("azurerm_mssql_", StringComparison.Ordinal) || text.Contains("azurerm_sql_", StringComparison.Ordinal))
            {
                errors.Add("declares Azure SQL; databases run in pods (ADR-IR34)");
            }

            if (path == "terraform/apps/tier")
            {
                if (!appsTerraform.Contains("sha1(", StringComparison.Ordinal))
                {
                    errors.Add("vault name formula sha1(...) not found under terraform/apps (kv-<app>-<e>-<hash4>)");
                }

                if (!appsTerraform.Contains("disk-", StringComparison.Ordinal))
                {
                    errors.Add("database disks disk-<app>-<env>-db not found under terraform/apps");
                }
            }

            report.Verdict(path + "/", errors, "layer present");
            var state = Py.AsStr(Py.Item(layer, "state"));
            if (!everything.Contains(state.Split('{')[0], StringComparison.Ordinal))
            {
                report.Out(FindingStatus.Warn, path + "/", $"state key {state} not found in the layer or the runbooks (backend configuration)");
            }
        }
    }

    /// <summary>
    /// C20: the retired paths of §11.8 are gone, and no platform, kit or test file names a retired name outside a line
    /// marked <c>name-lint: allow</c> (FAIL; WARN under catalogue, tests and tools); findings are grouped by owning package.
    /// </summary>
    private void C20RetiredPathsAndNames(Report report)
    {
        foreach (var retired in Py.Iterate(At(contracts, "retiredPaths")))
        {
            var path = Py.AsStr(Py.Item(retired, "path"));
            if (files.Exists(path))
            {
                report.Out(FindingStatus.Fail, path, $"retired path still present; now {Py.Str(Py.Item(retired, "now"))} ({Py.Str(Py.Item(retired, "package"))})");
            }
            else
            {
                report.Out(FindingStatus.Pass, path, $"retired; now {Py.Str(Py.Item(retired, "now"))}");
            }
        }

        string[] roots = [.. PlatformConfigRoots, "scripts", "apps", "catalogue", "tests", "tools"];
        var marker = Py.AsStr(Py.Get(contracts, "nameLintAllowMarker", "name-lint: allow"));
        var patterns = new List<(string Name, Regex Pattern, bool Display)>();
        patterns.AddRange(Py.Iterate(At(contracts, "retiredNames")).Select(name => (Py.AsStr(name), RetiredName(Py.AsStr(name)), false)));
        patterns.AddRange(Py.Iterate(Py.Get(contracts, "retiredDisplayNames", new List<object?>())).Select(name => (Py.AsStr(name), RetiredName(Py.AsStr(name)), true)));
        foreach (var root in roots)
        {
            var hits = new Dictionary<string, List<(string Path, string Name)>>(StringComparer.Ordinal);
            foreach (var relative in files.Walk(root, null))
            {
                if (relative.EndsWith(".md", StringComparison.Ordinal) || relative == "scripts/checks/consistency.sh")
                {
                    continue;
                }

                var code = files.CodeText(relative);
                if (string.IsNullOrEmpty(code))
                {
                    continue;
                }

                var text = string.Join('\n', Py.SplitLines(code).Where(line => !line.Contains(marker, StringComparison.Ordinal)));
                foreach (var (name, pattern, display) in patterns)
                {
                    if (display && AppScoped().IsMatch(relative))
                    {
                        continue;
                    }

                    if (pattern.IsMatch(text))
                    {
                        var package = Owner(relative);
                        if (!hits.TryGetValue(package, out var list))
                        {
                            hits[package] = list = [];
                        }

                        list.Add((relative, name));
                    }
                }
            }

            foreach (var package in hits.Keys.Order(Py.StringOrder))
            {
                var found = hits[package].Distinct()
                    .OrderBy(hit => hit.Path, Py.StringOrder)
                    .ThenBy(hit => hit.Name, Py.StringOrder)
                    .ToList();
                var status = root is "catalogue" or "tests" or "tools" ? FindingStatus.Warn : FindingStatus.Fail;
                report.Out(status, found[0].Path, "retired names: " + string.Join("; ", found.Take(25).Select(hit => $"{hit.Path}: {hit.Name}")));
            }

            if (hits.Count == 0 && files.IsDirectory(root))
            {
                report.Out(FindingStatus.Pass, root + "/", "no retired name");
            }
        }
    }

    /// <summary>
    /// C21: placeholders <c>&lt;…&gt;</c> in platform files, descriptors and fixtures are those of §7.0 and the contracts
    /// (WARN for an unknown one), never a retired one (FAIL). Only findings are reported, no PASS line.
    /// </summary>
    private void C21Placeholders(Report report)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Py.Iterate(At(contracts, "placeholders")).Concat(Py.Iterate(Py.Get(contracts, "notationTokens", new List<object?>()))))
        {
            known.UnionWith(ExpandToken(token));
        }

        var retired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Py.Iterate(Py.Get(contracts, "retiredPlaceholders", new List<object?>())))
        {
            retired.UnionWith(ExpandToken(token));
        }

        var families = Py.Iterate(Py.Get(contracts, "placeholderPatterns", new List<object?>()))
            .Select(pattern => new Regex("^" + string.Join("[A-Za-z0-9._-]+", Py.AsStr(pattern).Split('*').Select(Regex.Escape)) + "$"))
            .ToList();
        string[] roots = [.. PlatformConfigRoots, "apps", "fixtures", ".gitleaks.toml"];
        foreach (var root in roots)
        {
            var paths = files.IsFile(root) ? [root] : files.Walk(root, null);
            foreach (var relative in paths)
            {
                if (PlaceholderFreeExtensions.Any(extension => relative.EndsWith(extension, StringComparison.Ordinal)) || relative.Contains("/templates/", StringComparison.Ordinal))
                {
                    continue;
                }

                var found = Placeholder().Matches(files.CodeText(relative) ?? string.Empty).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
                var bad = found.Where(retired.Contains).Order(Py.StringOrder).Select(token => (object?)token).ToList();
                if (bad.Count > 0)
                {
                    report.Out(FindingStatus.Fail, relative, $"retired placeholders (§7.0): {Py.Repr(bad)}");
                }

                var unknown = found.Where(token => !retired.Contains(token) && !known.Contains(token) && !families.Any(family => family.IsMatch(token)))
                    .Order(Py.StringOrder)
                    .Select(token => (object?)token)
                    .ToList();
                if (unknown.Count > 0)
                {
                    report.Out(FindingStatus.Warn, relative, $"placeholders not in §7.0 or the contracts: {Py.Repr(unknown)}");
                }
            }
        }
    }

    /// <summary>The script's <c>expand_token(tok)</c>: the token and its expansions for every environment and tier.</summary>
    private HashSet<string> ExpandToken(object? token)
    {
        var variants = new HashSet<string>(StringComparer.Ordinal) { Py.AsStr(token) };
        foreach (var environment in Environments())
        {
            variants.Add(X(token, ("env", environment), ("tier", Py.AsStr(Py.Item(tierOfEnvironment, environment)))));
        }

        foreach (var tier in Tiers())
        {
            variants.Add(X(token, ("tier", tier)));
        }

        return variants;
    }

    /// <summary>A retired name not preceded by a name character (<c>(?&lt;![A-Za-z0-9_-])</c>).</summary>
    private static Regex RetiredName(string name) => new("(?<![A-Za-z0-9_-])" + Regex.Escape(name));

    [GeneratedRegex(@"^(apps/|fixtures/|(codefresh|\.octopus|gitops|containers)/apps/)")]
    private static partial Regex AppScoped();

    [GeneratedRegex(@"<([A-Za-z0-9{][A-Za-z0-9_.{}*/ -]{0,60})>")]
    private static partial Regex Placeholder();
}
