using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Consistency;

/// <summary>The Codefresh check: C25.</summary>
internal sealed partial class ConsistencyChecks
{
    /// <summary>
    /// C25: every spec keeps fork events off and attaches only the contexts of its lane; every app pipeline and starter
    /// meets handshake M1 (images under apps/&lt;app&gt;/, previews under apps-previews/&lt;app&gt;/) and M3 (explicit
    /// packages and no default package version, in the typed step and in the Octopus CLI command).
    /// </summary>
    private void C25CodefreshHandshake(Report report)
    {
        var codefresh = At(contracts, "codefresh");
        var platformContexts = new PySet(Py.Iterate(Py.Item(codefresh, "contexts")).Select(context => Py.Item(context, "name")));
        var specs = files.Walk("codefresh", RepositoryFiles.Yaml)
            .Where(relative => relative.Contains("/specs/", StringComparison.Ordinal) && !relative.Contains("/templates/", StringComparison.Ordinal))
            .ToList();
        if (specs.Count == 0)
        {
            report.Absent("codefresh/platform/specs/env-checks.yml", "Codefresh specs");
        }

        foreach (var relative in specs)
        {
            if ((report.Docs(relative) ?? []).FirstOrDefault() is not PyDict spec)
            {
                continue;
            }

            var errors = new List<string>();
            var app = relative.StartsWith("codefresh/apps/", StringComparison.Ordinal) ? relative.Split('/')[2] : null;
            foreach (var trigger in Py.Iterate(Py.Or(Py.G(spec, new List<object?>(), "spec", "triggers"), new List<object?>())))
            {
                if (trigger is PyDict entry && Py.Get(entry, "pullRequestAllowForkEvents") is true)
                {
                    errors.Add($"trigger {Py.Str(Py.Get(entry, "name"))} allows fork events");
                }
            }

            foreach (var item in Py.Iterate(Py.Or(Py.G(spec, new List<object?>(), "spec", "contexts"), new List<object?>())))
            {
                var context = Py.Str(item);
                if (!string.IsNullOrEmpty(app))
                {
                    if (context is not ("platform-octopus" or "platform-registry") && !context.StartsWith($"app-{app}-", StringComparison.Ordinal))
                    {
                        errors.Add($"context {context} is not for app pipelines (platform-octopus, platform-registry, app-{app}-*)");
                    }

                    if (context == "platform-octopus" && !ReleasePipeline().IsMatch(Py.Str(Py.G(spec, string.Empty, "metadata", "name"))))
                    {
                        errors.Add("platform-octopus is attached to release pipelines only");
                    }
                }
                else if (context.StartsWith("app-", StringComparison.Ordinal) || !platformContexts.Contains(context))
                {
                    errors.Add($"context {context} is not a platform context");
                }
            }

            report.Verdict(relative, errors, "fork events off; contexts in their lane");
        }

        var step = Py.Item(codefresh, "releaseStep");
        var cli = Py.Or(Py.Get(step, "cli"), new PyDict());
        var command = Py.AsStr(Py.Get(cli, "command", "octopus release create"));
        var commandPattern = new Regex(Regex.Escape(command) + @"\b");

        List<string> ReleaseErrors(PyDict pipeline)
        {
            var errors = new List<string>();
            foreach (var dict in Py.AllDicts(pipeline))
            {
                if (!Py.Str(Py.Get(dict, "type", string.Empty)).StartsWith(Py.AsStr(Py.Item(step, "type")), StringComparison.Ordinal))
                {
                    continue;
                }

                var arguments = Py.Or(Py.Get(dict, "arguments"), new PyDict());
                foreach (var need in Py.Iterate(Py.Item(step, "required")))
                {
                    if (!Py.In(need, arguments))
                    {
                        errors.Add($"octopus_release lacks explicit {Py.Str(need)} (M3)");
                    }
                }

                foreach (var bad in Py.Iterate(Py.Item(step, "forbidden")))
                {
                    if (Py.In(bad, arguments))
                    {
                        errors.Add($"octopus_release passes {Py.Str(bad)} (M3)");
                    }
                }
            }

            foreach (var text in Py.AllStrings(pipeline))
            {
                if (!commandPattern.IsMatch(text))
                {
                    continue;
                }

                // One command per match: join backslash continuations, then stop at the end of that command.
                var joined = LineContinuation().Replace(text, " ");
                foreach (var tail in commandPattern.Split(joined).Skip(1))
                {
                    var line = tail.Split('\n', 2)[0];
                    var flags = CommandFlag().Matches(line).Select(match => match.Groups[1].Value.Split('=', 2)[0].ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
                    foreach (var need in Py.Iterate(Py.Get(cli, "required", new List<object?>())))
                    {
                        if (!flags.Contains(Py.AsStr(need).ToLowerInvariant()))
                        {
                            errors.Add($"'{command}' lacks {Py.Str(need)}: packages are explicit (M3)");
                        }
                    }

                    foreach (var bad in Py.Iterate(Py.Get(cli, "forbidden", new List<object?>())))
                    {
                        if (flags.Contains(Py.AsStr(bad).ToLowerInvariant()))
                        {
                            errors.Add($"'{command}' passes {Py.Str(bad)}: no default package version (M3)");
                        }
                    }
                }
            }

            return errors;
        }

        foreach (var (app, _) in Apps())
        {
            foreach (var relative in files.Walk($"codefresh/apps/{app}/pipelines", RepositoryFiles.Yaml))
            {
                if ((report.Docs(relative) ?? []).FirstOrDefault() is not PyDict pipeline)
                {
                    continue;
                }

                var prefix = Path.GetFileName(relative).StartsWith("preview", StringComparison.Ordinal) ? $"apps-previews/{app}/" : $"apps/{app}/";
                var errors = new List<string>();
                foreach (var dict in Py.AllDicts(pipeline))
                {
                    if (Py.Get(dict, "image_name") is string name && !name.StartsWith(prefix, StringComparison.Ordinal) && !name.Contains("${{", StringComparison.Ordinal))
                    {
                        errors.Add($"image_name {name} is outside {prefix} (M1)");
                    }
                }

                errors.AddRange(ReleaseErrors(pipeline));
                report.Verdict(relative, errors, "handshake M1 and M3 hold");
            }
        }

        // Starters carry the same handshake, with tokens in place of the app (scaffolded once, then owned).
        string[] starterPrefixes = ["apps/<app>/", "apps/__APP__/", "apps-previews/<app>/", "apps-previews/__APP__/"];
        foreach (var relative in files.Walk("codefresh/templates", RepositoryFiles.Yaml))
        {
            if (!relative.Contains("/pipelines/", StringComparison.Ordinal) || (report.Docs(relative) ?? []).FirstOrDefault() is not PyDict pipeline)
            {
                continue;
            }

            var errors = new List<string>();
            foreach (var dict in Py.AllDicts(pipeline))
            {
                if (Py.Get(dict, "image_name") is string name && !starterPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) && !name.Contains("${{", StringComparison.Ordinal))
                {
                    errors.Add($"image_name {name} is outside apps/<app>/ (M1)");
                }
            }

            errors.AddRange(ReleaseErrors(pipeline));
            report.Verdict(relative, errors, "starter keeps handshake M1 and M3");
        }
    }

    [GeneratedRegex("/release(-[a-z0-9-]+)?$")]
    private static partial Regex ReleasePipeline();

    [GeneratedRegex(@"\\[ \t]*\n[ \t]*")]
    private static partial Regex LineContinuation();

    [GeneratedRegex(@"(?:^|\s)(--[A-Za-z][A-Za-z0-9=-]*)")]
    private static partial Regex CommandFlag();
}
