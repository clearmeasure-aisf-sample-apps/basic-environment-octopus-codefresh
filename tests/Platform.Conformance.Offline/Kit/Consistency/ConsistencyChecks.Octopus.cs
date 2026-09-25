using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Consistency;

/// <summary>Checks of the Octopus files: C12, C15, C22 and C23.</summary>
internal sealed partial class ConsistencyChecks
{
    private const string RunbookFolder = ".octopus/platform-infrastructure/runbooks";

    /// <summary>C12: each platform runbook exists, prompts its variables, runs its Terraform layer and avoids forbidden scopes; retired runbooks are gone.</summary>
    private void C12Runbooks(Report report)
    {
        var runbooks = At(contracts, "octopus", "runbooks");
        foreach (var runbook in Py.Iterate(Py.Item(runbooks, "list")))
        {
            var name = Py.Str(Py.Item(runbook, "name"));
            var relative = $"{RunbookFolder}/{name}.ocl";
            var text = files.CodeText(relative);
            if (text is null)
            {
                report.Absent(relative, $"runbook {name}");
                continue;
            }

            var errors = new List<string>();
            foreach (var variable in Py.Iterate(Py.Get(runbook, "prompted", new List<object?>())))
            {
                if (!Py.In(variable, text))
                {
                    errors.Add($"does not prompt {Py.Str(variable)}");
                }
            }

            if (Py.Truthy(Py.Get(runbook, "terraformDirectory")) && !Py.In(Py.Item(runbook, "terraformDirectory"), text))
            {
                errors.Add($"does not run {Py.Str(Py.Item(runbook, "terraformDirectory"))}");
            }

            foreach (var forbidden in Py.Iterate(Py.Get(runbooks, "forbidden", new List<object?>())))
            {
                if (!Py.Eq(Py.Item(forbidden, "name"), Py.Item(runbook, "name")))
                {
                    continue;
                }

                foreach (var environment in Py.Iterate(Py.Item(forbidden, "environments")))
                {
                    if (Regex.IsMatch(text, @"environments\s*=\s*\[[^\]]*""" + Regex.Escape(Py.AsStr(environment)) + @""""))
                    {
                        errors.Add($"is scoped to {Py.Str(environment)} (forbidden)");
                    }
                }
            }

            report.Verdict(relative, errors, $"{name} matches §7.0");
        }

        if (files.IsDirectory(RunbookFolder))
        {
            foreach (var retired in Py.Iterate(Py.Get(runbooks, "retired", new List<object?>())))
            {
                var relative = $"{RunbookFolder}/{Py.Str(retired)}.ocl";
                if (files.Exists(relative))
                {
                    report.Out(FindingStatus.Fail, relative, $"retired runbook {Py.Str(retired)} still present");
                }
            }
        }
    }

    /// <summary>C15: octopus/terraform names every space object of §7.0 and builds a project group app-${…} per descriptor from apps/*.yaml.</summary>
    private void C15OctopusTerraform(Report report)
    {
        var terraform = files.Walk("octopus/terraform", [".tf"]);
        if (terraform.Count == 0)
        {
            report.Absent("octopus/terraform/main.tf", "Octopus Terraform");
            return;
        }

        var text = string.Join('\n', terraform.Select(relative => files.CodeText(relative) ?? string.Empty));
        var octopus = At(contracts, "octopus");
        var want = new List<object?>();
        want.AddRange(Py.Iterate(Py.Item(octopus, "lifecycles")).Select(lifecycle => Py.Item(lifecycle, "name")));
        want.AddRange(Py.Iterate(Py.Item(octopus, "libraryVariableSets")).Select(set => Py.Item(set, "name")));
        want.AddRange(Py.Iterate(Py.Item(octopus, "feeds")).Select(feed => Py.Item(feed, "name")).Where(name => !Py.Eq(name, "built-in")));
        want.AddRange(Py.Iterate(Py.Item(octopus, "stepTemplates")));
        want.Add(At(octopus, "freeze", "name"));
        want.Add(At(octopus, "projectGroups", "platform"));
        want.Add("Platform.InterventionTestMode");
        want.Add("Platform.SoDMode");
        want.AddRange(Py.Iterate(Py.Item(octopus, "teams")));
        want.AddRange(Py.Iterate(Py.Item(octopus, "triggers")).Select(trigger => Py.Item(trigger, "name")));
        var errors = want.Where(item => !Py.In(item, text)).Select(item => $"never names {Py.Str(item)}").ToList();
        if (!text.Contains("azure-platform-lifecycle-", StringComparison.Ordinal))
        {
            errors.Add("never names the accounts azure-platform-lifecycle-<tier>");
        }

        if (!text.Contains("apps/*.yaml", StringComparison.Ordinal))
        {
            errors.Add("no for_each over apps/*.yaml (project shells come from the descriptors)");
        }

        if (!text.Contains("app-${", StringComparison.Ordinal))
        {
            errors.Add("no project group app-${...} per descriptor");
        }

        report.Verdict("octopus/terraform/", errors, "space objects and per-app shells match §7.0");
    }

    /// <summary>C22: every TF_VAR_&lt;name&gt; a platform runbook sets is a variable of the Terraform layer it runs (§11.10 interface).</summary>
    private void C22RunbookInputs(Report report)
    {
        var runbooks = files.Walk(RunbookFolder, [".ocl"]);
        if (runbooks.Count == 0)
        {
            report.Absent($"{RunbookFolder}/env-apply.ocl", "platform runbooks");
            return;
        }

        (string Prefix, string Layer)[] layers = [("env-", "terraform/tier"), ("apps-", "terraform/apps/tier")];
        foreach (var relative in runbooks)
        {
            var name = Path.GetFileName(relative);
            var layer = layers.FirstOrDefault(candidate => name.StartsWith(candidate.Prefix, StringComparison.Ordinal)).Layer;
            if (layer is null)
            {
                continue;
            }

            var terraform = string.Join('\n', files.Walk(layer, [".tf"]).Select(file => files.CodeText(file) ?? string.Empty));
            if (terraform.Length == 0)
            {
                report.Out(FindingStatus.Skip, relative, $"{layer} absent");
                continue;
            }

            var declared = TerraformVariable().Matches(terraform).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var used = TerraformInput().Matches(files.CodeText(relative) ?? string.Empty).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var errors = used.Where(variable => !declared.Contains(variable)).Order(Py.StringOrder).Select(variable => $"TF_VAR_{variable} is not a variable of {layer}").ToList();
            report.Verdict(relative, errors, $"every TF_VAR_* is a variable of {layer}");
        }
    }

    /// <summary>
    /// C23: env-wake and env-sleep run on the dynamic pool; platform-wake has one step and reads only PlatformWake.* and
    /// system variables; an app process that touches a cluster wakes it first, and an in-cluster app runbook waits for it.
    /// </summary>
    private void C23SleepAndWake(Report report)
    {
        var sleepWake = At(contracts, "sleepWake");
        foreach (var file in Py.Iterate(Py.Item(sleepWake, "clusterPowerFiles")))
        {
            var relative = Py.AsStr(file);
            var text = files.CodeText(relative);
            if (text is null)
            {
                report.Absent(relative, "sleep/wake runbook");
                continue;
            }

            var errors = new List<string>();
            if (!Py.In(Py.Item(sleepWake, "runbookPool"), text) && !text.Contains("Hosted Ubuntu", StringComparison.Ordinal))
            {
                errors.Add($"must run on {Py.Str(Py.Item(sleepWake, "runbookPool"))} (never an in-cluster pool)");
            }

            if (relative.EndsWith("env-sleep.ocl", StringComparison.Ordinal))
            {
                foreach (var variable in new[] { "Sleep.Force", "Sleep.DryRun", "Sleep.NowOverride" })
                {
                    if (!text.Contains(variable, StringComparison.Ordinal))
                    {
                        errors.Add($"does not prompt {variable}");
                    }
                }
            }

            report.Verdict(relative, errors, "sleep/wake runbook on the dynamic pool");
        }

        var wake = Py.Item(sleepWake, "wakeProject");
        var process = Py.AsStr(Py.Item(wake, "process"));
        var processText = files.CodeText(process);
        if (processText is null)
        {
            report.Absent(process, "platform-wake process");
        }
        else
        {
            var steps = OclSteps(processText).Select(step => (object?)step.Name).ToList();
            var errors = new List<string>();
            if (!Py.Eq(steps, new List<object?> { Py.Item(wake, "step") }))
            {
                errors.Add($"must have exactly one step, {Py.Str(Py.Item(wake, "step"))} (found {Py.Repr(steps)})");
            }

            var reads = VariableRead().Matches(processText)
                .Select(match => match.Groups[1].Value)
                .Where(variable => !variable.StartsWith("PlatformWake.", StringComparison.Ordinal) && !variable.StartsWith("Octopus.", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(Py.StringOrder)
                .Select(variable => (object?)variable)
                .ToList();
            if (reads.Count > 0)
            {
                errors.Add($"reads {Py.Repr(reads)}; platform-wake reads only PlatformWake.* and system variables (decision 17)");
            }

            report.Verdict(process, errors, "platform-wake: one step, namespaced variables only");
        }

        const string appsRoot = ".octopus/apps";
        if (!files.IsDirectory(appsRoot))
        {
            report.Out(FindingStatus.Skip, appsRoot + "/", "no app projects yet");
            return;
        }

        foreach (var relative in files.Walk(appsRoot, [".ocl"]))
        {
            var text = files.CodeText(relative) ?? string.Empty;
            if (Path.GetFileName(relative) == "deployment_process.ocl")
            {
                var steps = OclSteps(text);
                if (!(InClusterPool().IsMatch(text) || text.Contains("ArgoCDUpdateImageTags", StringComparison.Ordinal) || text.Contains("platform-pin-writer", StringComparison.Ordinal)))
                {
                    continue;
                }

                var first = steps.Count > 0 ? steps[0].Body : string.Empty;
                var wakesFirst = first.Contains("Octopus.DeployRelease", StringComparison.Ordinal) && first.Contains("platform-wake", StringComparison.Ordinal) && first.Contains("Always", StringComparison.Ordinal);
                report.Verdict(relative, wakesFirst ? [] : ["the first step must be a Deploy a Release of platform-wake with condition Always"], "step 0 wakes the cluster through platform-wake");
            }
            else if (relative.Contains("/runbooks/", StringComparison.Ordinal) && InClusterPool().IsMatch(text))
            {
                report.Verdict(relative, text.Contains("Wake.WaitMinutes", StringComparison.Ordinal) ? [] : ["an in-cluster runbook needs the keyless wait guard (Wake.WaitMinutes)"], "waits for a sleeping cluster");
            }
        }
    }

    /// <summary>The script's <c>ocl_steps(t)</c>: the <c>step "…" { … }</c> blocks of an OCL text.</summary>
    private static List<(string Name, string Body)> OclSteps(string text) => RepositoryFiles.Blocks(text, StepHeader()).ToList();

    [GeneratedRegex(@"^\s*step\s+""([^""]+)""\s*\{")]
    private static partial Regex StepHeader();

    [GeneratedRegex(@"#\{([A-Za-z][A-Za-z0-9_.\[\]-]*)")]
    private static partial Regex VariableRead();

    [GeneratedRegex(@"k8s-(tdd|uat|prod)|Platform\.WorkerPool")]
    private static partial Regex InClusterPool();

    [GeneratedRegex(@"variable\s+""([A-Za-z0-9_]+)""")]
    private static partial Regex TerraformVariable();

    [GeneratedRegex("TF_VAR_([A-Za-z0-9_]+)")]
    private static partial Regex TerraformInput();
}
