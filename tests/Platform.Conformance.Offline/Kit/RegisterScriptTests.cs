using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// CAP-KIT-003, offline half for Codefresh: <c>codefresh/register.ps1 --app &lt;app&gt;</c> makes an app's Codefresh
/// pipelines match its descriptor, against a stubbed Codefresh API. A frozen app (<c>status: frozen</c>, ADR-IR34
/// decision 28) gets every git and cron trigger off, no webhook check and no build; an active app gets the triggers of
/// its specs and the webhook check; a dry run calls no API and prints no value.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class RegisterScriptTests
{
    private const string App = "demoapp";
    private const string ApiKey = "<stub-codefresh-api-key>";
    private const string ContextVariable = "DEMOAPP_REGISTER_TEST_VALUE";
    private const string ContextValue = "value-that-must-stay-masked";
    private const string GitIntegration = "github-aisf-sample-apps";
    private const string GitIntegrationSecret = "git-integration-value-that-must-never-print";

    /// <summary>A frozen app's pipelines are created or replaced with every trigger off, and nothing starts a build.</summary>
    [Test]
    [Capability("CAP-KIT-003")]
    public void Should_RegisterApp_FrozenApp_RegistersEveryTriggerOffAndStartsNoBuild()
    {
        using var workspace = RegisterWorkspace.Create("frozen");
        using var api = new StubCodefreshApi("project:demoapp", "pipeline:demoapp/ci", "context:platform-octopus");

        var result = workspace.Register(api, "--app", App);

        result.ExitCode.ShouldBe(0, result.Transcript);
        var registrations = api.Requests.Where(IsPipelineWrite).ToArray();
        registrations.Select(request => $"{request.Method} {request.Path}")
            .ShouldBe(["PUT /api/pipelines/demoapp%2Fci?disableRevisionCheck=true", "POST /api/pipelines"], result.Transcript);
        registrations.SelectMany(request => Triggers(request.Body)).Select(Disabled).ShouldBe([true, true, true], "branch-push, nightly (cron), main-push");
        api.Requests.ShouldNotContain(request => request.Path.StartsWith("/api/repos/webhooks/", StringComparison.Ordinal), "the webhooks of a frozen app were checked");
        api.Requests.ShouldNotContain(request => request.Path.StartsWith("/api/pipelines/run/", StringComparison.Ordinal) || request.Path.StartsWith("/api/builds", StringComparison.Ordinal), "a build was started");
        result.Output.ShouldContain("every trigger off, the app is frozen (apps/demoapp.yaml)");
    }

    /// <summary>An active app's pipelines keep the trigger states of their specs, and their repository's webhook is checked.</summary>
    [Test]
    [Capability("CAP-KIT-003")]
    public void Should_RegisterApp_ActiveApp_RegistersTheTriggersOfTheSpecsAndChecksTheWebhook()
    {
        using var workspace = RegisterWorkspace.Create("active");
        using var api = new StubCodefreshApi("project:demoapp", "pipeline:demoapp/ci", "context:platform-octopus");

        var result = workspace.Register(api, "--app", App);

        result.ExitCode.ShouldBe(0, result.Transcript);
        var registrations = api.Requests.Where(IsPipelineWrite).ToArray();
        registrations.Select(request => $"{request.Method} {request.Path}")
            .ShouldBe(["PUT /api/pipelines/demoapp%2Fci?disableRevisionCheck=true", "POST /api/pipelines"], result.Transcript);
        registrations.SelectMany(request => Triggers(request.Body)).Select(Disabled).ShouldBe([false, true, false], "branch-push, nightly (cron), main-push as in the specs");
        api.Requests.Count(request => request.Method == "GET" && request.Path == "/api/repos/webhooks/clearmeasure-aisf-sample-apps/demoapp-repo/github/github-aisf-sample-apps")
            .ShouldBe(1, result.Transcript);
        result.Output.ShouldNotContain("frozen");
    }

    /// <summary>A dry run of a frozen app plans every trigger off, calls no API and prints no context value.</summary>
    [Test]
    [Capability("CAP-KIT-003")]
    public void Should_RegisterDryRun_FrozenApp_PlansEveryTriggerOffWithoutApiCallOrValue()
    {
        using var workspace = RegisterWorkspace.Create("frozen");
        using var api = new StubCodefreshApi();

        var result = workspace.Register(api, "--app", App, "--dry-run");

        result.ExitCode.ShouldBe(0, result.Transcript);
        api.Requests.ShouldBeEmpty();
        result.Output.ShouldContain("FROZEN (apps/demoapp.yaml); triggers: branch-push (git) off, nightly (cron) off");
        result.Output.ShouldContain("create or replace (optional): TOKEN <- " + ContextVariable);
        result.Output.ShouldContain("\"TOKEN\": \"***\"");
        result.Transcript.ShouldNotContain(ContextValue);
    }

    /// <summary>An absent Git integration is only reported as pending: the lookup is the one request that names it, nothing is written.</summary>
    [Test]
    [Capability("CAP-KIT-003")]
    public void Should_RegisterFull_GitIntegrationAbsent_ReportsPendingAndWritesNothingForIt()
    {
        using var workspace = RegisterWorkspace.Create("active", withGitIntegration: true);
        using var api = new StubCodefreshApi("project:demoapp", "pipeline:demoapp/ci", "context:platform-octopus");

        var result = workspace.Register(api, "--full");

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Transcript.ShouldContain($"PENDING git integration {GitIntegration}: owner-only, create it in Codefresh (docs/runbooks/credential-rotation.md)");
        api.Requests.Count(request => request.Method == "GET" && request.Path == $"/api/contexts/{GitIntegration}").ShouldBe(1, result.Transcript);
        GitIntegrationWrites(api).ShouldBeEmpty("a Git integration is owner-only and never written");
    }

    /// <summary>A Git integration of the expected App type is reported ok and left alone.</summary>
    [Test]
    [Capability("CAP-KIT-003")]
    public void Should_RegisterFull_GitIntegrationIsAGitHubApp_ReportsOkAndPrintsNoValue()
    {
        using var workspace = RegisterWorkspace.Create("active", withGitIntegration: true);
        using var api = new StubCodefreshApi("project:demoapp", "pipeline:demoapp/ci", "context:platform-octopus", $"context:{GitIntegration}")
        {
            ContextTypes = { [GitIntegration] = "git.github-app" },
        };

        var result = workspace.Register(api, "--full");

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain($"git integration {GitIntegration}: ok (GitHub App)");
        result.Transcript.ShouldNotContain("PENDING git integration");
        result.Transcript.ShouldNotContain("WARN git integration");
        GitIntegrationWrites(api).ShouldBeEmpty("a Git integration is owner-only and never written");
        result.Transcript.ShouldNotContain(GitIntegrationSecret);
        result.Transcript.ShouldNotContain(ApiKey);
    }

    /// <summary>A Git integration that is still a token integration is a WARN, not a failure, and is not replaced.</summary>
    [Test]
    [Capability("CAP-KIT-003")]
    public void Should_RegisterFull_GitIntegrationIsStillAToken_WarnsWithoutFailingOrWriting()
    {
        using var workspace = RegisterWorkspace.Create("active", withGitIntegration: true);
        using var api = new StubCodefreshApi("project:demoapp", "pipeline:demoapp/ci", "context:platform-octopus", $"context:{GitIntegration}")
        {
            ContextTypes = { [GitIntegration] = "git.github" },
        };

        var result = workspace.Register(api, "--full");

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Transcript.ShouldContain($"WARN git integration {GitIntegration}: type git.github, expected git.github-app; switch it (owner only)");
        result.Output.ShouldNotContain("(GitHub App)");
        GitIntegrationWrites(api).ShouldBeEmpty("a Git integration is owner-only and never written");
        result.Transcript.ShouldNotContain(GitIntegrationSecret);
        result.Transcript.ShouldNotContain(ApiKey);
    }

    /// <summary>A dry run plans the verification, calls no API and prints no value.</summary>
    [Test]
    [Capability("CAP-KIT-003")]
    public void Should_RegisterDryRun_GitIntegration_PlansVerifyOnlyWithoutApiCallOrValue()
    {
        using var workspace = RegisterWorkspace.Create("active", withGitIntegration: true);
        using var api = new StubCodefreshApi();

        var result = workspace.Register(api, "--full", "--dry-run");

        result.ExitCode.ShouldBe(0, result.Transcript);
        api.Requests.ShouldBeEmpty();
        result.Output.ShouldContain($"### git integration {GitIntegration}");
        result.Output.ShouldContain("verify only (owner-only): expects type git.github-app");
        result.Transcript.ShouldNotContain(ContextValue);
        result.Transcript.ShouldNotContain(ApiKey);
    }

    /// <summary>Every request that would change the Git integration: a non-GET naming it in the path, or a write to a context or registry whose body names it.</summary>
    private static StubRequest[] GitIntegrationWrites(StubCodefreshApi api) => api.Requests
        .Where(request => request.Method != "GET"
            && (request.Path.Contains(GitIntegration, StringComparison.Ordinal)
                || ((request.Path.StartsWith("/api/contexts", StringComparison.Ordinal) || request.Path.StartsWith("/api/registries", StringComparison.Ordinal))
                    && request.Body.Contains(GitIntegration, StringComparison.Ordinal))))
        .ToArray();

    private static bool IsPipelineWrite(StubRequest request) =>
        request.Method is "POST" or "PUT" && request.Path.StartsWith("/api/pipelines", StringComparison.Ordinal);

    private static IEnumerable<JsonNode> Triggers(string body)
    {
        var spec = JsonNode.Parse(body)?["spec"] ?? throw new InvalidOperationException($"no spec in {body}");
        return new[] { "triggers", "cronTriggers" }.SelectMany(key => spec[key]?.AsArray().OfType<JsonNode>() ?? []);
    }

    private static bool? Disabled(JsonNode trigger) => trigger["disabled"]?.GetValue<bool>();

    /// <summary>A recorded request: method, the raw path with its query, and the body.</summary>
    private sealed record StubRequest(string Method, string Path, string Body);

    /// <summary>
    /// A Codefresh API on 127.0.0.1 that records every request. Objects in <c>existing</c> (<c>project:&lt;name&gt;</c>,
    /// <c>context:&lt;name&gt;</c>, <c>pipeline:&lt;name&gt;</c>) answer lookups with 200, all others with 404; creates and
    /// replaces succeed, the registry list is empty and every webhook record exists.
    /// </summary>
    private sealed class StubCodefreshApi : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly HashSet<string> existing;
        private readonly List<StubRequest> requests = [];
        private readonly Task loop;

        public StubCodefreshApi(params string[] existing)
        {
            this.existing = new HashSet<string>(existing, StringComparer.Ordinal);
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Url + "/");
            listener.Start();
            loop = Task.Run(ServeAsync);
        }

        /// <summary>Base URL, for CF_URL.</summary>
        public string Url { get; }

        /// <summary>
        /// Context name to <c>spec.type</c>: a lookup of an existing context in this map answers with that type and a fake
        /// secret value, which the script must never print.
        /// </summary>
        public Dictionary<string, string> ContextTypes { get; } = new(StringComparer.Ordinal);

        /// <summary>Requests in arrival order.</summary>
        public IReadOnlyList<StubRequest> Requests
        {
            get
            {
                lock (requests)
                {
                    return requests.ToArray();
                }
            }
        }

        public void Dispose()
        {
            listener.Close();
            try
            {
                loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }

        private async Task ServeAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                var path = context.Request.RawUrl ?? string.Empty;
                lock (requests)
                {
                    requests.Add(new StubRequest(context.Request.HttpMethod, path, body));
                }

                var (status, answer) = Answer(context.Request.HttpMethod, path);
                var bytes = Encoding.UTF8.GetBytes(answer);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                context.Response.Close();
            }
        }

        private (int Status, string Body) Answer(string method, string path)
        {
            var segments = path.Split('?')[0].Split('/').Select(Uri.UnescapeDataString).ToArray();
            return (method, segments.Length > 2 ? segments[2] : string.Empty, segments.Length) switch
            {
                ("GET", "projects", 5) => Found("project", segments[4]),
                ("GET", "contexts", 4) when ContextTypes.TryGetValue(segments[3], out var type) && existing.Contains($"context:{segments[3]}") =>
                    (200, new JsonObject
                    {
                        ["metadata"] = new JsonObject { ["name"] = segments[3] },
                        ["spec"] = new JsonObject { ["type"] = type, ["data"] = new JsonObject { ["token"] = GitIntegrationSecret } },
                    }.ToJsonString()),
                ("GET", "contexts", 4) => Found("context", segments[3]),
                ("GET", "pipelines", 4) => Found("pipeline", segments[3]),
                ("GET", "registries", 3) => (200, "[]"),
                ("GET", "repos", _) => (200, """{"endpoint":"https://g.codefresh.example.test/hook"}"""),
                ("POST", _, 3) => (201, "{}"),
                ("PUT", _, 4) or ("PATCH", _, 4) or ("DELETE", _, 4) => (200, "{}"),
                _ => (400, $$"""{"message":"unexpected {{method}} {{path}}"}"""),
            };

            (int Status, string Body) Found(string kind, string name) =>
                existing.Contains($"{kind}:{name}") ? (200, $$"""{"name":"{{name}}"}""") : (404, """{"message":"does not exist"}""");
        }
    }

    /// <summary>
    /// A throw-away environment-repo root holding one app, <c>demoapp</c>: its descriptor with the given status, two
    /// specs (ci: a git and a disabled cron trigger; release: a git trigger and context platform-octopus), their pipeline
    /// YAML and an optional app context whose value comes from the environment.
    /// </summary>
    private sealed class RegisterWorkspace : IDisposable
    {
        private RegisterWorkspace(string root) => Root = root;

        public string Root { get; }

        public static RegisterWorkspace Create(string status, bool withGitIntegration = false)
        {
            var workspace = new RegisterWorkspace(Path.Combine(Path.GetTempPath(), "platform-kit-tests", $"register-{Guid.NewGuid():N}"));
            workspace.Write($"apps/{App}.yaml", $"""
                schema: 1
                name: {App}
                status: {status}   # the line that Platform.Onboarding retire --freeze writes
                repositories:
                  - name: clearmeasure-aisf-sample-apps/demoapp-repo
                    defaultBranch: main
                """);
            workspace.Write($"codefresh/apps/{App}/integrations.yaml", $$"""
                contexts:
                  - name: app-{{App}}-ci
                    optional: true
                    data:
                      TOKEN: {fromEnv: {{ContextVariable}}}
                """);
            workspace.Write($"codefresh/apps/{App}/specs/ci.yml", Spec("ci", """
                  triggers:
                    - type: git
                      name: branch-push
                      repo: clearmeasure-aisf-sample-apps/demoapp-repo
                      provider: github
                      context: github-aisf-sample-apps
                      events:
                        - push.heads
                      branchRegex: "/^(?!main$).+/"
                      pullRequestAllowForkEvents: false
                      disabled: false
                  cronTriggers:
                    - name: nightly
                      type: cron
                      expression: "0 3 * * *"
                      disabled: true
                  contexts: []
                """));
            workspace.Write($"codefresh/apps/{App}/specs/release.yml", Spec("release", """
                  triggers:
                    - type: git
                      name: main-push
                      repo: clearmeasure-aisf-sample-apps/demoapp-repo
                      provider: github
                      context: github-aisf-sample-apps
                      events: [push.heads]
                      branchRegex: /^main$/
                      pullRequestAllowForkEvents: false
                      disabled: false
                  contexts:
                    - platform-octopus
                  concurrency: 1
                """));
            foreach (var pipeline in new[] { "ci", "release" })
            {
                workspace.Write($"codefresh/apps/{App}/pipelines/{pipeline}.yml", "version: \"1.0\"\nsteps: {}\n");
            }

            if (withGitIntegration)
            {
                workspace.Write("codefresh/platform/integrations.yaml", $"""
                    gitIntegrations:
                      - name: {GitIntegration}
                        kind: codefresh-github-app
                        expectedType: git.github-app
                        ownerOnly: true
                    """);
            }

            return workspace;
        }

        /// <summary>Runs <c>pwsh codefresh/register.ps1 &lt;arguments&gt; --root &lt;workspace&gt;</c> against the stub.</summary>
        public ProcessResult Register(StubCodefreshApi api, params string[] arguments) => KitToolbox.Run(
            KitToolbox.Require("pwsh"),
            ["-NoProfile", "-File", Path.Combine(KitToolbox.RepositoryRoot, "codefresh", "register.ps1"), .. arguments, "--root", Root],
            Root,
            TimeSpan.FromMinutes(2),
            new Dictionary<string, string?>
            {
                ["CF_API_KEY"] = ApiKey,
                ["CF_URL"] = api.Url,
                ["CF_RUNTIME"] = null,
                ["DRY_RUN"] = null,
                [ContextVariable] = ContextValue,
                ["NO_PROXY"] = "127.0.0.1,localhost",
                ["HTTP_PROXY"] = null,
                ["http_proxy"] = null,
                ["ALL_PROXY"] = null,
            });

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static string Spec(string name, string spec) => $"""
            version: "1.0"
            kind: pipeline
            metadata:
              name: {App}/{name}
              project: {App}
            spec:
            {spec}
              specTemplate:
                location: git
                repo: clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh
                path: codefresh/apps/{App}/pipelines/{name}.yml
                revision: main
                context: github-aisf-sample-apps

            """;

        private void Write(string relative, string text)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.EndsWith('\n') ? text : text + "\n", new UTF8Encoding(false));
        }
    }
}
