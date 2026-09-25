using System.Text;
using System.Text.Json;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// The supply chain of the release images (every copy of <c>scripts/supply-chain.ps1</c> and
/// <c>scripts/supply-chain-step.ps1</c>): per image digest a Syft SBOM and a step-authored provenance, each attested
/// keyless with a fresh Codefresh OIDC token (CAP-CF-006); every release tag locked with the repository-scoped token
/// (CAP-CF-007); and the reuse check that lets a re-run of the same commit skip the image builds (CAP-CF-014). Stub
/// syft, cosign, curl, az and crane record the calls; no token or password reaches the log.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class SupplyChainScriptTests
{
    private const string Registry = "acrtest.azurecr.io";
    private const string Password = "fake-token-password";
    private const string PipelineCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string WebDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string MigratorDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private static IEnumerable<string> Scripts() => AppScriptSandbox.Copies("supply-chain.ps1");

    private static IEnumerable<string> StepScripts() => AppScriptSandbox.Copies("supply-chain-step.ps1");

    /// <summary>Per digest: SBOM, a fresh token, the SBOM attestation, a fresh token, the provenance attestation.</summary>
    /// <param name="script">A copy of supply-chain.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-006")]
    public void Should_RunSupplyChain_TwoImages_AttestsEachDigestWithFreshTokens(string script)
    {
        using var sandbox = Sandbox();
        var web = $"{Registry}/apps/demo/web@{WebDigest}";
        var migrator = $"{Registry}/apps/demo/migrator@{MigratorDigest}";

        var run = sandbox.Run(script, "-Registry", Registry, "-Image", $"{web},{migrator}", "-Tag", "1.2.3", "-Out", "evidence", "-NoLock");

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.Calls.Where(call => call.Tool != "git").Select(call => call.ToString()).ShouldBe(
        [
            $"syft scan registry:{web} -o spdx-json=evidence/apps_demo_web.spdx.json",
            "curl -fsS -H Authorization: oidc-request-token https://oidc.example.test/token?audience=sigstore",
            $"cosign attest --yes --type spdxjson --predicate evidence/apps_demo_web.spdx.json --identity-token id-token-1 {web}",
            "curl -fsS -H Authorization: oidc-request-token https://oidc.example.test/token?audience=sigstore",
            $"cosign attest --yes --type slsaprovenance1 --predicate evidence/apps_demo_web.provenance.json --identity-token id-token-2 {web}",
            $"syft scan registry:{migrator} -o spdx-json=evidence/apps_demo_migrator.spdx.json",
            "curl -fsS -H Authorization: oidc-request-token https://oidc.example.test/token?audience=sigstore",
            $"cosign attest --yes --type spdxjson --predicate evidence/apps_demo_migrator.spdx.json --identity-token id-token-3 {migrator}",
            "curl -fsS -H Authorization: oidc-request-token https://oidc.example.test/token?audience=sigstore",
            $"cosign attest --yes --type slsaprovenance1 --predicate evidence/apps_demo_migrator.provenance.json --identity-token id-token-4 {migrator}",
        ]);
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(sandbox.Work, "evidence", "apps_demo_web.provenance.json")));
        var definition = provenance.RootElement.GetProperty("buildDefinition");
        definition.GetProperty("externalParameters").GetProperty("revision").GetString().ShouldBe("abc1234def");
        definition.GetProperty("externalParameters").GetProperty("version").GetString().ShouldBe("1.2.3");
        definition.GetProperty("internalParameters").GetProperty("provenanceAuthor").GetString().ShouldEndWith("/scripts/supply-chain.ps1)");
        definition.GetProperty("internalParameters").GetProperty("slsaBuildLevelClaim").GetString().ShouldBe("L2 at most");
        definition.GetProperty("resolvedDependencies")[1].GetProperty("digest").GetProperty("gitCommit").GetString().ShouldBe(PipelineCommit);
        provenance.RootElement.GetProperty("runDetails").GetProperty("byproducts")[0].GetProperty("uri").GetString().ShouldBe(web);
        (run.Output + run.Error).ShouldNotContain("id-token-");
        (run.Output + run.Error).ShouldNotContain("oidc-request-token");
    }

    /// <summary>Every tag of every image is locked, write and delete, with the token of the Docker config.</summary>
    /// <param name="script">A copy of supply-chain.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-007")]
    public void Should_RunSupplyChain_WithLock_LocksEveryTagWithTheRepositoryToken(string script)
    {
        using var sandbox = Sandbox();
        var web = $"{Registry}/apps/demo/web@{WebDigest}";

        var run = sandbox.Run(script, "-Registry", Registry, "-Image", web, "-Tag", "1.2.3,sha-abc1234", "-Out", "evidence");

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.CallsOf("az").Select(call => call.ToString()).ShouldBe(
        [
            $"az acr repository update --name acrtest --image apps/demo/web:1.2.3 --write-enabled false --delete-enabled false --username cf-apps-release --password {Password} --output none",
            $"az acr repository update --name acrtest --image apps/demo/web:sha-abc1234 --write-enabled false --delete-enabled false --username cf-apps-release --password {Password} --output none",
        ]);
        run.Calls.Select(call => call.Tool).Where(tool => tool != "git").Last().ShouldBe("az", "the lock is the last act of the supply chain");
        (run.Output + run.Error).ShouldNotContain(Password);
    }

    /// <summary>No id_token from the OIDC provider (or no request variables) fails before any attestation.</summary>
    /// <param name="script">A copy of supply-chain.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-006")]
    public void Should_RunSupplyChain_NoIdToken_FailsBeforeAnyAttestation(string script)
    {
        using var sandbox = Sandbox();
        var web = $"{Registry}/apps/demo/web@{WebDigest}";
        sandbox.Environment["CURL_MODE"] = "null";

        var noToken = sandbox.Run(script, "-Registry", Registry, "-Image", web, "-Tag", "1.2.3", "-Out", "evidence");
        sandbox.Environment["CF_OIDC_REQUEST_TOKEN"] = null;
        var noRequest = sandbox.Run(script, "-Registry", Registry, "-Image", web, "-Tag", "1.2.3", "-Out", "evidence");
        var notOnRegistry = sandbox.Run(script, "-Registry", Registry, "-Image", $"other.azurecr.io/apps/demo/web@{WebDigest}", "-Tag", "1.2.3", "-Out", "evidence");

        noToken.ExitCode.ShouldBe(1, noToken.Transcript);
        noToken.Error.ShouldContain("the Codefresh OIDC provider returned no id_token");
        noToken.CallsOf("cosign").ShouldBeEmpty();
        noRequest.ExitCode.ShouldBe(1, noRequest.Transcript);
        noRequest.Error.ShouldContain("CF_OIDC_REQUEST_TOKEN is not set");
        noRequest.CallsOf("curl").ShouldBeEmpty();
        notOnRegistry.ExitCode.ShouldBe(1, notOnRegistry.Transcript);
        notOnRegistry.Error.ShouldContain("-Image must be <registry>/<repo>@sha256:<digest> on acrtest.azurecr.io");
        notOnRegistry.CallsOf("syft").ShouldBeEmpty();
    }

    /// <summary>The reuse check answers reuse when every tag is locked, build when none is, and fails on a mix.</summary>
    /// <param name="script">A copy of supply-chain.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-014")]
    public void Should_RunSupplyChain_CheckReuse_AnswersReuseOrBuildAndFailsOnAMix(string script)
    {
        using var sandbox = Sandbox();
        string[] arguments = ["-Registry", Registry, "-CheckReuse", "-Repository", "apps/demo/web,apps/demo/migrator", "-Tag", "1.2.3,sha-abc1234"];

        sandbox.Environment["AZ_DEFAULT"] = "locked";
        var locked = sandbox.Run(script, arguments);
        sandbox.Environment["AZ_DEFAULT"] = "missing";
        var missing = sandbox.Run(script, arguments);
        sandbox.Environment["AZ_DEFAULT"] = "unlocked";
        var unlocked = sandbox.Run(script, arguments);
        sandbox.Environment["AZ_DEFAULT"] = "locked";
        sandbox.Environment["AZ_MISSING"] = "apps/demo/migrator:sha-abc1234";
        var mixed = sandbox.Run(script, arguments);
        sandbox.Environment["AZ_DEFAULT"] = "error";
        var unreadable = sandbox.Run(script, arguments);

        locked.OutputLines.ShouldBe(["reuse"], locked.Transcript);
        locked.CallsOf("az").Count.ShouldBe(4);
        locked.CallsOf("az")[0].ToString().ShouldBe(
            $"az acr repository show --name acrtest --image apps/demo/web:1.2.3 --username cf-apps-release --password {Password} --query changeableAttributes.writeEnabled --output tsv");
        missing.OutputLines.ShouldBe(["build"], missing.Transcript);
        unlocked.OutputLines.ShouldBe(["build"], unlocked.Transcript);
        mixed.ExitCode.ShouldBe(1, mixed.Transcript);
        mixed.Error.ShouldContain("3 of 4 tags are locked, 0 unlocked, 1 missing; a mixed state needs an operator");
        unreadable.ExitCode.ShouldBe(1, unreadable.Transcript);
        unreadable.Error.ShouldContain("cannot read apps/demo/web:1.2.3; failing closed");
        (locked.Output + locked.Error + mixed.Error).ShouldNotContain(Password);
    }

    /// <summary>image_reuse exports IMAGES_REUSED from the reuse check, through a private Docker config that is removed afterwards.</summary>
    /// <param name="script">A copy of supply-chain-step.ps1.</param>
    [TestCaseSource(nameof(StepScripts))]
    [Capability("CAP-CF-014")]
    public void Should_RunSupplyChainStep_ImageReuse_ExportsImagesReusedFromTheLocks(string script)
    {
        using var sandbox = StepSandbox();

        sandbox.Environment["AZ_DEFAULT"] = "locked";
        var rerun = sandbox.Run(script, "-Step", "image_reuse", "-Repository", "apps/demo/web,apps/demo/migrator");
        var seen = File.ReadAllText(Path.Combine(sandbox.Root, "docker-seen"));
        sandbox.Environment["AZ_DEFAULT"] = "missing";
        var first = sandbox.Run(script, "-Step", "image_reuse", "-Repository", "apps/demo/web,apps/demo/migrator");

        rerun.ExitCode.ShouldBe(0, rerun.Transcript);
        rerun.Exports.ShouldBe(["IMAGES_REUSED=true"]);
        rerun.Output.ShouldContain("image_reuse: reuse");
        rerun.CallsOf("az").Select(call => call.Arguments[6]).ShouldBe(["apps/demo/web:1.2.3", "apps/demo/web:sha-abc1234", "apps/demo/migrator:1.2.3", "apps/demo/migrator:sha-abc1234"]);
        var expectedAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"cf-apps-release:{Password}"));
        seen.ShouldContain($"\"{Registry}\"");
        seen.ShouldContain(expectedAuth);
        var folder = seen.Split('\n')[0];
        Directory.Exists(folder).ShouldBeFalse("the Docker config folder is removed when the step ends");
        first.Exports.ShouldBe(["IMAGES_REUSED=false"], first.Transcript);
        (rerun.Output + rerun.Error + first.Output + first.Error).ShouldNotContain(Password);
    }

    /// <summary>supply_chain attests the digests of the VERSION tags and locks VERSION and sha-&lt;short revision&gt;.</summary>
    /// <param name="script">A copy of supply-chain-step.ps1.</param>
    [TestCaseSource(nameof(StepScripts))]
    [Capability("CAP-CF-006")]
    public void Should_RunSupplyChainStep_SupplyChain_AttestsTheVersionDigests(string script)
    {
        using var sandbox = StepSandbox();

        var run = sandbox.Run(script, "-Step", "supply_chain", "-Repository", "apps/demo/web,apps/demo/migrator");

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.CallsOf("crane").Select(call => call.ToString()).ShouldBe([$"crane digest {Registry}/apps/demo/web:1.2.3", $"crane digest {Registry}/apps/demo/migrator:1.2.3"]);
        run.CallsOf("cosign").Select(call => call.Arguments[^1]).ShouldBe(
            [$"{Registry}/apps/demo/web@{WebDigest}", $"{Registry}/apps/demo/web@{WebDigest}", $"{Registry}/apps/demo/migrator@{MigratorDigest}", $"{Registry}/apps/demo/migrator@{MigratorDigest}"]);
        run.CallsOf("az").Select(call => call.Arguments[6]).ShouldBe(["apps/demo/web:1.2.3", "apps/demo/web:sha-abc1234", "apps/demo/migrator:1.2.3", "apps/demo/migrator:sha-abc1234"]);
        File.Exists(Path.Combine(sandbox.Root, "artifacts", "supply-chain", "apps_demo_migrator.provenance.json")).ShouldBeTrue();
    }

    /// <summary>supply_chain_reuse fails unless every tag is locked; then it names the reused digests.</summary>
    /// <param name="script">A copy of supply-chain-step.ps1.</param>
    [TestCaseSource(nameof(StepScripts))]
    [Capability("CAP-CF-014")]
    public void Should_RunSupplyChainStep_SupplyChainReuse_FailsUnlessEveryTagIsLocked(string script)
    {
        using var sandbox = StepSandbox();
        sandbox.Environment["IMAGES_REUSED"] = "true";

        sandbox.Environment["AZ_DEFAULT"] = "locked";
        var locked = sandbox.Run(script, "-Step", "supply_chain_reuse", "-Repository", "apps/demo/web");
        sandbox.Environment["AZ_DEFAULT"] = "unlocked";
        var unlocked = sandbox.Run(script, "-Step", "supply_chain_reuse", "-Repository", "apps/demo/web");

        locked.ExitCode.ShouldBe(0, locked.Transcript);
        locked.Output.ShouldContain($"supply_chain_reuse: reusing {Registry}/apps/demo/web@{WebDigest}");
        locked.CallsOf("cosign").ShouldBeEmpty();
        unlocked.ExitCode.ShouldBe(1, unlocked.Transcript);
        unlocked.Error.ShouldContain("supply_chain_reuse: IMAGES_REUSED is 'true' but the reuse check answered 'build'");
    }

    /// <summary>Without the registry context the step fails closed before any tool runs.</summary>
    /// <param name="script">A copy of supply-chain-step.ps1.</param>
    [TestCaseSource(nameof(StepScripts))]
    [Capability("CAP-CF-007")]
    public void Should_RunSupplyChainStep_MissingRegistryContext_FailsClosed(string script)
    {
        using var sandbox = StepSandbox();
        sandbox.Environment["ACR_TOKEN_PASSWORD"] = null;

        var run = sandbox.Run(script, "-Step", "supply_chain", "-Repository", "apps/demo/web");

        run.ExitCode.ShouldBe(1, run.Transcript);
        run.Error.ShouldContain("supply_chain: ACR_TOKEN_PASSWORD is not set (context platform-registry)");
        run.Calls.ShouldBeEmpty();
    }

    /// <summary>Stub tools, a Docker config with the registry token and the OIDC variables, for supply-chain.ps1.</summary>
    private static AppScriptSandbox Sandbox()
    {
        var sandbox = AppScriptSandbox.Create();
        StubTools(sandbox);
        var config = sandbox.Write(Path.Combine("docker", "config.json"), JsonSerializer.Serialize(new
        {
            auths = new Dictionary<string, object> { [Registry] = new { auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"cf-apps-release:{Password}")) } },
        }));
        sandbox.Environment["DOCKER_CONFIG"] = Path.GetDirectoryName(config);
        return sandbox;
    }

    /// <summary>Stub tools and the environment of a release step (context platform-registry, VERSION, the revision).</summary>
    private static AppScriptSandbox StepSandbox()
    {
        var sandbox = AppScriptSandbox.Create();
        StubTools(sandbox);
        sandbox.CfExport();
        sandbox.Environment["ACR_REGISTRY"] = Registry;
        sandbox.Environment["ACR_TOKEN_NAME"] = "cf-apps-release";
        sandbox.Environment["ACR_TOKEN_PASSWORD"] = Password;
        sandbox.Environment["CF_SHORT_REVISION"] = "abc1234";
        sandbox.Environment["ARTIFACTS_DIR"] = Path.Combine(sandbox.Root, "artifacts");
        return sandbox;
    }

    private static void StubTools(AppScriptSandbox sandbox)
    {
        sandbox.Stub("syft", """if [ "$1" = scan ]; then printf '{"spdxVersion":"SPDX-2.3"}\n' >"${4#spdx-json=}"; fi""");
        sandbox.Stub("curl", """
            count=$(( $(cat "$APP_SCRIPT_CALLS.curl" 2>/dev/null || echo 0) + 1 )); echo "$count" >"$APP_SCRIPT_CALLS.curl"
            if [ "${CURL_MODE:-ok}" = null ]; then printf '{"id_token":null}\n'; else printf '{"id_token":"id-token-%s"}\n' "$count"; fi
            """);
        sandbox.Stub("cosign");
        sandbox.Stub("git", $$"""if [ "$1" = -C ] && [ "$3 $4" = "rev-parse HEAD" ]; then echo {{PipelineCommit}}; fi""");
        sandbox.Stub("crane", $$"""case "$2" in */web:*) echo {{WebDigest}} ;; *) echo {{MigratorDigest}} ;; esac""");
        sandbox.Stub("az", """
            if [ "$1 $2 $3" = "acr repository show" ]; then
              { echo "$DOCKER_CONFIG"; cat "$DOCKER_CONFIG/config.json"; } >"$(dirname "$APP_SCRIPT_CALLS")/docker-seen"
              state="${AZ_DEFAULT:-missing}"; [ "$7" = "${AZ_MISSING:-}" ] && state=missing
              case "$state" in
                locked) echo False ;;
                unlocked) echo true ;;
                missing) echo "ERROR: The requested resource does not exist." >&2; exit 1 ;;
                error) echo "ERROR: unauthorized: authentication required" >&2; exit 1 ;;
              esac
            fi
            """);
        sandbox.Environment["CF_OIDC_REQUEST_URL"] = "https://oidc.example.test/token";
        sandbox.Environment["CF_OIDC_REQUEST_TOKEN"] = "oidc-request-token";
        sandbox.Environment["CF_REVISION"] = "abc1234def";
        sandbox.Environment["CF_BRANCH"] = "main";
        sandbox.Environment["VERSION"] = "1.2.3";
    }
}
