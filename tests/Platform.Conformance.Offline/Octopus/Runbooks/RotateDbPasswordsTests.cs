using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>
/// CAP-AZ-011 (offline half): database passwords rotate without breaking the app. Step rotate of rotate-db-passwords,
/// under the stub Octopus runtime and stub az and kubectl, per app-environment of the tier: &lt;app&gt;_migrator and
/// &lt;app&gt;_app, then a forced ESO refresh and a restart of the app's own Deployments, named by the workload Applications
/// of the tenant chart; then sa, last, because every change runs as sa, and a forced refresh of every ExternalSecret that
/// reads db-sa-password (db-sa of the database StatefulSet, db-sa-&lt;app&gt;-&lt;env&gt; of the backups), until db-0 logs
/// in with the mounted password as its probes do. Each password goes into the vault before ALTER LOGIN, and back out when
/// ALTER LOGIN fails, so the vault always holds the password that works; passwords travel only in files and on standard
/// input (docs/runbooks/credential-rotation.md section 4).
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class RotateDbPasswordsTests
{
    private const string Runbook = ".octopus/platform-infrastructure/runbooks/rotate-db-passwords.ocl";
    private const string Subscription = "00000000-0000-0000-0000-000000000001";
    private const string Alter = "exec -i db-0 -- /bin/bash -c SQLCMDPASSWORD=";

    /// <summary>The app vaults of sandbox for <see cref="Subscription"/>: kv-&lt;app&gt;-&lt;env initial&gt;-&lt;4 hex of SHA-1&gt;.</summary>
    private static readonly Dictionary<string, string> Vaults = new(StringComparer.Ordinal)
    {
        ["tdd"] = "kv-sandbox-t-0b24",
        ["uat"] = "kv-sandbox-u-8150",
    };

    /// <summary>Each login's password goes into the vault, then ALTER LOGIN runs as sa: the app's logins first, sa last.</summary>
    [Test]
    [Capability("CAP-AZ-011")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    public void Should_Rotate_EveryLogin_VaultFirstAndSaLast()
    {
        var run = Rotation(Database("tdd"), Database("uat", backup: true)).Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.Highlights.ShouldBe(["Database passwords of sandbox rotated in: tdd uat."], run.Transcript);
        foreach (var (environment, vault) in Vaults)
        {
            var ns = $"sandbox-{environment}";
            var writes = run.CallsMatching($"^az keyvault secret set --vault-name {vault} ").ToArray();
            writes.Select(call => call.Option("--name")).ShouldBe(["db-migrator-password", "db-app-password", "db-sa-password"], run.Transcript);
            var statements = Statements(run, ns);
            statements.Select(index => run.Calls[index].Input).ShouldBe(
            [
                $"ALTER LOGIN [sandbox_migrator] WITH PASSWORD = N'{writes[0].File}';\n",
                $"ALTER LOGIN [sandbox_app] WITH PASSWORD = N'{writes[1].File}';\n",
                $"ALTER LOGIN [sa] WITH PASSWORD = N'{writes[2].File}';\n",
            ], run.Transcript);
            for (var login = 0; login < 3; login++)
            {
                run.Calls.ToList().IndexOf(writes[login]).ShouldBeLessThan(statements[login], "the vault gets the password before ALTER LOGIN");
            }

            statements[2].ShouldBeGreaterThan(run.IndexOf($"^kubectl --namespace {ns} rollout status "), "sa rotates after the app's Deployments rolled out");
            run.CallsMatching($"^kubectl --namespace {ns} exec -i db-0 -- /bin/bash -c read -r password; ").Select(call => call.Input)
                .ShouldBe([writes[1].File + "\n", writes[2].File + "\n"], "the login checks of sandbox_app and sa read the new password on standard input");
        }

        run.CallsMatching(Alter).ShouldAllBe(call => call.Line.Contains("SQLCMDPASSWORD=\"$(cat /etc/platform/db-sa/password)\"", StringComparison.Ordinal));
        var passwords = run.Calls.Where(call => call.File is not null).Select(call => call.File!).ToArray();
        passwords.Length.ShouldBe(6, run.Transcript);
        passwords.ShouldAllBe(password => Regex.IsMatch(password, "^[A-Za-z0-9]{32}Aa9-$"));
        passwords.Distinct().Count().ShouldBe(6);
        run.Calls.Where(call => call.Arguments.Any(argument => passwords.Any(password => argument.Contains(password, StringComparison.Ordinal))))
            .Select(call => call.Line).ShouldBeEmpty("no password on a command line");
    }

    /// <summary>
    /// After sa, every ExternalSecret that reads db-sa-password is force-synced, db-0 remounts its Secret, and sa logs in
    /// with the mounted password before the environment counts as rotated.
    /// </summary>
    [Test]
    [Capability("CAP-AZ-011")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    public void Should_Rotate_SaPassword_ForceSyncsEveryExternalSecretThatReadsIt()
    {
        var run = Rotation(Database("tdd"), Database("uat", backup: true)).Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.CallsMatching("annotate externalsecret").Select(call => $"{call.Option("--namespace")}/{call.Arguments[4]}").ShouldBe(
        [
            "sandbox-tdd/db-migrator", "sandbox-tdd/db-app", "sandbox-tdd/db-sa",
            "sandbox-uat/db-migrator", "sandbox-uat/db-app", "sandbox-uat/db-sa", "platform-backup/db-sa-sandbox-uat",
        ], run.Transcript);
        foreach (var ns in new[] { "sandbox-tdd", "sandbox-uat" })
        {
            var remount = run.IndexOf($"^kubectl --namespace {ns} annotate pod db-0 platform/db-sa-synced-at=[0-9]+ --overwrite$");
            remount.ShouldBeGreaterThan(run.IndexOf($"^kubectl --namespace {ns} annotate externalsecret db-sa "), "db-0 remounts db-sa once the Secret changed");
            run.Calls.ToList().FindLastIndex(call => call.Matches($"^kubectl --namespace {ns} exec db-0 -- .*SELECT 1")).ShouldBeGreaterThan(remount, "sa logs in with the mounted password after the remount");
        }

        run.CallsMatching("get externalsecret db-sa-sandbox-tdd").ShouldHaveSingleItem("tdd has no backup copy: looked up, left alone");
        run.Warnings.ShouldBeEmpty(run.Transcript);
    }

    /// <summary>The restart names the app's own Deployments, read from its workload Applications, never a whole namespace.</summary>
    [Test]
    [Capability("CAP-AZ-011")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    public void Should_Rotate_AppDeployments_RestartedByNameFromTheWorkloadApplications()
    {
        var run = Rotation(Database("tdd", deployments: ["web", "worker"]), Missing("uat")).Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.CallsMatching("get applications.argoproj.io").ShouldHaveSingleItem(run.Transcript).Option("--selector").ShouldBe("platform/app=sandbox,environment=tdd,platform/role=workload");
        run.CallsMatching("rollout restart").Select(call => call.Line).ShouldBe(["kubectl --namespace sandbox-tdd rollout restart deployment.apps/web deployment.apps/worker"], run.Transcript);
        run.CallsMatching("rollout status").Select(call => call.Arguments[4]).ShouldBe(["deployment.apps/web", "deployment.apps/worker"], run.Transcript);
        run.CallsMatching("get deployment").ShouldBeEmpty("Deployments come from the Applications, not from a namespace listing");
        run.Warnings.ShouldBe(["Namespace sandbox-uat does not exist: sandbox has no database in uat."], run.Transcript);
        run.Highlights.ShouldBe(["Database passwords of sandbox rotated in: tdd."], run.Transcript);
    }

    /// <summary>When ALTER LOGIN fails, the vault gets the previous password back and the step fails; nothing later runs.</summary>
    /// <param name="login">The login whose ALTER LOGIN fails.</param>
    /// <param name="secret">Its vault key.</param>
    [TestCase("sandbox_migrator", "db-migrator-password")]
    [TestCase("sa", "db-sa-password")]
    [Capability("CAP-AZ-011")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    public void Should_Rotate_AlterLoginFails_VaultHoldsThePasswordThatWorks(string login, string secret)
    {
        var run = Rotation(Database("tdd", alterFails: login), Database("uat")).Run();

        run.Failure.ShouldBe($"ALTER LOGIN {login} failed in sandbox-tdd; kv-sandbox-t-0b24 holds the previous password again.", run.Transcript);
        var writes = run.CallsMatching($"^az keyvault secret set .* --name {secret} ").Select(call => call.File).ToArray();
        writes.Length.ShouldBe(2, run.Transcript);
        writes[0].ShouldNotBe($"old-{secret}");
        writes[1].ShouldBe($"old-{secret}", "the previous password goes back into the vault");
        run.CallsMatching("sandbox-uat").ShouldBeEmpty("the rotation stops at the failure");
        run.CallsMatching("annotate externalsecret db-sa ").ShouldBeEmpty("Secret db-sa keeps the password that works");
        run.CallsMatching("rollout restart").Count.ShouldBe(login == "sa" ? 1 : 0, "the app's logins rotate completely before sa");
    }

    /// <summary>When sa cannot log in with the mounted password, nothing is rotated in that environment.</summary>
    [Test]
    [Capability("CAP-AZ-011")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    public void Should_Rotate_SaCannotLogIn_ChangesNothing()
    {
        var run = Rotation([Kubectl("--namespace sandbox-tdd exec db-0 -- /bin/bash -c .*SELECT 1", exitCode: 1, error: "Login failed for user 'sa'.\n")], Database("tdd")).Run();

        run.Failure.ShouldBe("sa cannot log in to sandbox-tdd/db-0 with the password of Secret db-sa mounted in the pod (what its probes use); nothing was rotated in tdd.", run.Transcript);
        run.CallsMatching("keyvault secret|rollout|annotate").ShouldBeEmpty(run.Transcript);
    }

    /// <summary>When db-0 keeps a mounted sa password that no longer works, its probes fail: the step fails after 3 minutes.</summary>
    [Test]
    [Capability("CAP-AZ-011")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    public void Should_Rotate_MountedSaPasswordStale_FailsTheStep()
    {
        var run = Rotation(
            [
                Kubectl("--namespace sandbox-tdd exec db-0 -- /bin/bash -c .*SELECT 1", times: 1),
                Kubectl("--namespace sandbox-tdd exec db-0 -- /bin/bash -c .*SELECT 1", exitCode: 1, error: "Login failed for user 'sa'.\n"),
            ],
            Database("tdd")).Run();

        run.Failure.ShouldBe("sa cannot log in to sandbox-tdd/db-0 with the password of Secret db-sa mounted in the pod 3 minutes after the rotation, so its probes fail; kv-sandbox-t-0b24 holds the new password: force-sync ExternalSecret db-sa (docs/runbooks/credential-rotation.md section 4).", run.Transcript);
        run.CallsMatching("exec db-0 -- .*SELECT 1").Count.ShouldBe(19, "the check before the rotation, then 18 attempts");
        run.CallsMatching("^sleep 10$").Count.ShouldBe(18, run.Transcript);
        run.Log.ShouldNotContain("Rotated sa in sandbox-tdd; db-sa-password updated in kv-sandbox-t-0b24.", run.Transcript);
    }

    /// <summary>A failed login check of sa still refreshes the Secrets of sa, whose login already changed, then fails.</summary>
    [Test]
    [Capability("CAP-AZ-011")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    public void Should_Rotate_SaLoginCheckFails_StillRefreshesItsSecrets()
    {
        var run = Rotation([Kubectl("--namespace sandbox-tdd exec -i db-0 -- /bin/bash -c read -r password; .* -U sa ", exitCode: 1, error: "Login failed for user 'sa'.\n")], Database("tdd")).Run();

        run.Failure.ShouldBe("Login check failed for sa in sandbox-tdd after rotation; kv-sandbox-t-0b24 holds the new password.", run.Transcript);
        run.IndexOf("annotate externalsecret db-sa ").ShouldBeGreaterThan(run.IndexOf("read -r password; .* -U sa "), run.Transcript);
        run.CallsMatching("annotate pod db-0").ShouldHaveSingleItem(run.Transcript);
    }

    /// <summary>Indexes of the ALTER LOGIN calls in a namespace, in order.</summary>
    private static int[] Statements(ScriptRun run, string ns) =>
        Enumerable.Range(0, run.Calls.Count).Where(index => run.Calls[index].Matches($"^kubectl --namespace {ns} {Alter}")).ToArray();

    private static (string Pattern, string Output, int ExitCode, string? Error, int? Times) Kubectl(string pattern, string output = "", int exitCode = 0, string? error = null, int? times = null) =>
        ("^kubectl " + pattern, output, exitCode, error, times);

    private static (string, string, int, string?, int?)[] Missing(string environment) =>
        [Kubectl($"get namespace sandbox-{environment} --output name$", exitCode: 1, error: $"Error from server (NotFound): namespaces \"sandbox-{environment}\" not found\n")];

    /// <summary>kubectl replies for one app-environment with a database; its workload Application lists the given Deployments.</summary>
    private static (string, string, int, string?, int?)[] Database(string environment, bool backup = false, string[]? deployments = null, string? alterFails = null)
    {
        var ns = $"sandbox-{environment}";
        var replies = new List<(string, string, int, string?, int?)>
        {
            Kubectl($"get namespace {ns} --output name$", $"namespace/{ns}\n"),
            Kubectl($"--namespace {ns} get pod db-0 --output name$", "pod/db-0\n"),
            Kubectl($"--namespace {ns} exec db-0 -- /bin/bash -c .*SELECT 1"),
        };
        foreach (var login in new[] { "sandbox_migrator", "sandbox_app", "sa" })
        {
            var fails = login == alterFails;
            replies.Add(Kubectl($"--namespace {ns} {Alter}.* -i /dev/stdin$", exitCode: fails ? 1 : 0, error: fails ? "Msg 15151, Level 16, State 1: Cannot alter the login.\n" : null, times: 1));
        }

        replies.Add(Kubectl($"--namespace {ns} exec -i db-0 -- /bin/bash -c read -r password; "));
        replies.Add(Kubectl($@"get namespace --selector platform/app=sandbox,environment={environment} --output jsonpath=\{{\.items\[\*\]\.metadata\.name\}}$", ns));
        foreach (var secret in new[] { "db-migrator", "db-app", "db-sa" })
        {
            replies.AddRange(Synced(ns, secret));
        }

        replies.AddRange(backup
            ? Synced("platform-backup", $"db-sa-{ns}")
            : [Kubectl($"--namespace platform-backup get externalsecret db-sa-{ns} --output name$", exitCode: 1, error: "Error from server (NotFound)\n")]);
        replies.Add(Kubectl($"--namespace {ns} annotate pod db-0 platform/db-sa-synced-at=[0-9]+ --overwrite$", "pod/db-0 annotated\n"));
        var resources = (deployments ?? ["web"]).Select(name => (object)new { group = "apps", version = "v1", kind = "Deployment", @namespace = ns, name })
            .Append(new { version = "v1", kind = "Service", @namespace = ns, name = "web" })
            .Append(new { group = "apps", version = "v1", kind = "Deployment", @namespace = "other-namespace", name = "foreign" })
            .ToArray();
        var applications = JsonSerializer.Serialize(new { items = new object[] { new { metadata = new { name = $"sandbox-app-{environment}" }, status = new { resources } }, new { metadata = new { name = "never-synced" } } } });
        replies.Add(Kubectl($@"get applications\.argoproj\.io --namespace argocd --selector platform/app=sandbox,environment={environment},platform/role=workload --output json$", applications));
        replies.Add(Kubectl($"--namespace {ns} rollout restart "));
        replies.Add(Kubectl($"--namespace {ns} rollout status "));
        return [.. replies];
    }

    /// <summary>An ExternalSecret whose Secret changes after the forced sync.</summary>
    private static IEnumerable<(string, string, int, string?, int?)> Synced(string ns, string secret) =>
    [
        Kubectl($"--namespace {ns} get externalsecret {secret} --output name$", $"externalsecret.external-secrets.io/{secret}\n"),
        Kubectl($@"--namespace {ns} get secret {secret} --output jsonpath=\{{\.metadata\.resourceVersion\}}$", "100", times: 1),
        Kubectl($@"--namespace {ns} get secret {secret} --output jsonpath=\{{\.metadata\.resourceVersion\}}$", "101"),
        Kubectl($"--namespace {ns} annotate externalsecret {secret} force-sync=[0-9]+ --overwrite$", $"externalsecret/{secret} annotated\n"),
    ];

    private static RunbookScript Rotation(params (string Pattern, string Output, int ExitCode, string? Error, int? Times)[][] databases)
    {
        var script = RunbookScript.Of(Runbook, "rotate").InTier().With("App.Name", "sandbox")
            .Reply("^az account show --query id --output tsv$", Subscription + "\n")
            .Reply("^az aks get-credentials --resource-group rg-platform-nonprod-aks --name aks-platform-nonprod --file .* --overwrite-existing --only-show-errors$")
            .Reply("^kubelogin convert-kubeconfig --login azurecli --kubeconfig ");
        foreach (var role in new[] { "migrator", "app", "sa" })
        {
            script.Reply($"^az keyvault secret show --vault-name kv-sandbox-[tu]-(0b24|8150) --name db-{role}-password --query value --output tsv$", $"old-db-{role}-password\n");
        }

        script.Reply("^az keyvault secret set --vault-name kv-sandbox-[tu]-(0b24|8150) --name db-[a-z]+-password --file .*/secret.txt --encoding utf-8 --output none$");
        foreach (var reply in databases.SelectMany(replies => replies))
        {
            script.Reply(reply.Pattern, reply.Output, reply.ExitCode, reply.Error, reply.Times);
        }

        return script;
    }
}
