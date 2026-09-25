using System.Text.Json;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>Variables and stub replies of the Octopus REST API for the runbook script tests.</summary>
internal static class OctopusReplies
{
    /// <summary>The Octopus Cloud URL of the tests.</summary>
    public const string Url = "https://example.octopus.app";

    /// <summary>A stand-in key: it must reach curl only on standard input.</summary>
    public const string Key = "stand-in-key-for-tests";

    /// <summary>The curl configuration line that carries <see cref="Key"/>.</summary>
    public const string KeyHeader = "header = \"X-Octopus-ApiKey: stand-in-key-for-tests\"\n";

    /// <summary>The environment ids the replies use: tdd, uat and infra-nonprod, then prod and infra-prod.</summary>
    public static IReadOnlyDictionary<string, string> EnvironmentIds { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tdd"] = "Environments-1",
        ["uat"] = "Environments-2",
        ["infra-nonprod"] = "Environments-3",
        ["prod"] = "Environments-4",
        ["infra-prod"] = "Environments-5",
    };

    /// <summary>JSON text of a value.</summary>
    /// <param name="value">Anonymous object, array or dictionary.</param>
    public static string Json(object? value) => JsonSerializer.Serialize(value);

    /// <summary>The system variables of a platform-infrastructure step in one tier, with the step-scoped key.</summary>
    /// <param name="script">The script.</param>
    /// <param name="tier"><c>nonprod</c> or <c>prod</c>.</param>
    public static RunbookScript InTier(this RunbookScript script, string tier = "nonprod") => script
        .With("Octopus.Environment.Name", $"infra-{tier}")
        .With("Environment.Class", tier)
        .With("Octopus.Web.ServerUri", Url + "/")
        .With("Octopus.Space.Id", "Spaces-1")
        .With("Octopus.Task.Id", "ServerTasks-900")
        .With("Platform.OctopusApiKey", Key);

    /// <summary>A reply to one Octopus REST call.</summary>
    /// <param name="script">The script.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Path and query as a POSIX extended regular expression, for example <c>/api/Spaces-1/tasks/T1</c>.</param>
    /// <param name="body">Response body (serialised unless it is a string), or <c>null</c> for none.</param>
    /// <param name="exitCode">curl's exit code (22 for an HTTP error).</param>
    /// <param name="times">How many calls the reply answers.</param>
    public static RunbookScript Api(this RunbookScript script, string method, string path, object? body = null, int exitCode = 0, int? times = null) =>
        script.Reply(
            $"^curl .* --request {method} .*{Escape(Url)}{path}$",
            body switch { null => string.Empty, string text => text, _ => Json(body) },
            exitCode,
            exitCode == 0 ? null : $"curl: (22) The requested URL returned error: 500\n",
            times);

    /// <summary>The partialName listing of a collection: the named item and a longer name that only partially matches.</summary>
    /// <param name="script">The script.</param>
    /// <param name="collection">Collection, for example <c>environments</c>.</param>
    /// <param name="name">Item name.</param>
    /// <param name="id">Item id.</param>
    public static RunbookScript Listing(this RunbookScript script, string collection, string name, string id) =>
        script.Api("GET", $@"/api/Spaces-1/{collection}\?partialName={Escape(name)}&take=100",
            new { Items = new[] { new { Id = id + "9", Name = name + "-old" }, new { Id = id, Name = name } } });

    /// <summary>The listings of the environments of a tier: its app environments and its infrastructure environment.</summary>
    /// <param name="script">The script.</param>
    /// <param name="tier"><c>nonprod</c> or <c>prod</c>.</param>
    public static RunbookScript Environments(this RunbookScript script, string tier = "nonprod")
    {
        foreach (var environment in tier == "prod" ? new[] { "prod", "infra-prod" } : ["tdd", "uat", "infra-nonprod"])
        {
            script.Listing("environments", environment, EnvironmentIds[environment]);
        }

        return script;
    }

    /// <summary>The task list of one environment in some states.</summary>
    /// <param name="script">The script.</param>
    /// <param name="environment">Environment name, for example <c>tdd</c>.</param>
    /// <param name="states">States as the scripts ask for them.</param>
    /// <param name="take">Page size.</param>
    /// <param name="tasks">Tasks from <see cref="Task"/>.</param>
    public static RunbookScript TaskList(this RunbookScript script, string environment, string states, int take, params object[] tasks) =>
        script.Api("GET", $@"/api/Spaces-1/tasks\?environment={EnvironmentIds[environment]}&states={states}&take={take}", new { Items = tasks });

    /// <summary>A task of a task list.</summary>
    /// <param name="id">Task id.</param>
    /// <param name="state">State.</param>
    /// <param name="description">Description.</param>
    /// <param name="queued">Queue time, or <c>null</c>.</param>
    /// <param name="completed">Completed time, or <c>null</c>.</param>
    public static object Task(string id, string state, string description, string? queued = null, string? completed = null) =>
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["Id"] = id, ["State"] = state, ["Description"] = description, ["QueueTime"] = queued, ["CompletedTime"] = completed };

    /// <summary>An ISO 8601 time some minutes from now, as Octopus writes it.</summary>
    /// <param name="minutes">Minutes from now (negative for the past).</param>
    public static string MinutesFromNow(double minutes) =>
        DateTimeOffset.UtcNow.AddMinutes(minutes).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The text escaped for a POSIX extended regular expression.</summary>
    /// <param name="text">Literal text.</param>
    public static string Escape(string text) => string.Concat(text.Select(character => "\\^$.|?*+()[]{}".Contains(character, StringComparison.Ordinal) ? $"\\{character}" : character.ToString()));

    /// <summary>Asserts that the key of the step reached curl only on standard input, never through an argument.</summary>
    /// <param name="run">The run.</param>
    /// <param name="key">The key the step reads.</param>
    public static void ShouldKeepTheKeyOffCommandLines(this ScriptRun run, string key = Key)
    {
        run.Calls.Where(call => call.Arguments.Any(argument => argument.Contains(key, StringComparison.Ordinal)))
            .Select(call => call.Line).ShouldBeEmpty("the key reached a command line");
        foreach (var call in run.Calls.Where(call => call.Tool == "curl"))
        {
            call.Input.ShouldBe($"header = \"X-Octopus-ApiKey: {key}\"\n", $"curl reads the key from its configuration on standard input: {call.Line}");
            call.Option("--config").ShouldBe("-");
        }
    }
}
