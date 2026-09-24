using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-CF-011: Codefresh never deploys. Proven offline, because a live attempt would need the very credential that must
/// not exist. Every pipeline (apps, platform and starters) has no deploy, approval, Helm, launch-composition or Argo CD
/// step, and neither its commands nor its scripts call kubectl, <c>helm install|upgrade|rollback|uninstall</c>, argocd,
/// <c>az aks</c>, <c>az login</c> or kubelogin. No context carries a cluster or Azure credential except
/// <c>platform-conformance</c>, the recorded single-operator exception attached only to the conformance pipelines;
/// no app pipeline, spec or script names one.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class NeverDeployTests
{
    private const string ConformanceContext = "platform-conformance";
    private static readonly string[] ForbiddenStepTypes = ["deploy", "approval", "helm", "launch-composition"];
    private static readonly string[] AppForbiddenText = [ConformanceContext, "AZURE_CLIENT_SECRET", "ARM_CLIENT_SECRET", "KUBECONFIG"];

    /// <summary>No step deploys or reaches a cluster.</summary>
    [Test]
    [Capability("CAP-CF-011")]
    public void Should_ReadPipelines_EveryStepAndScript_NeverDeploysOrReachesACluster()
    {
        var pipelines = CodefreshRepository.Pipelines;

        var steps = pipelines.SelectMany(pipeline => CodefreshRepository.Steps(CodefreshRepository.Load(pipeline)).Select(step => (Pipeline: pipeline, Step: step))).ToArray();
        var badTypes = steps
            .Where(entry => ForbiddenStepTypes.Contains(entry.Step.Type, StringComparer.OrdinalIgnoreCase) || ClusterStepType().IsMatch(entry.Step.Type))
            .Select(entry => $"{entry.Pipeline} {entry.Step.Path}: type {entry.Step.Type}");
        var badCommands = steps
            .SelectMany(entry => entry.Step.Commands.Where(command => ClusterCommand().IsMatch(command)).Select(command => $"{entry.Pipeline} {entry.Step.Path}: {command.Trim()}"));
        var badScripts = CodefreshRepository.Scripts
            .SelectMany(script => CodeLines(CodefreshRepository.Read(script)).Where(line => ClusterCommand().IsMatch(line)).Select(line => $"{script}: {line.Trim()}"));

        pipelines.ShouldNotBeEmpty("no pipeline YAML under codefresh/");
        steps.ShouldNotBeEmpty();
        badTypes.Concat(badCommands).Concat(badScripts).ShouldBeEmpty("deploy steps or cluster commands in Codefresh");
    }

    /// <summary>Only the conformance pipelines carry an Azure credential; apps never name one; no context holds a cluster credential.</summary>
    [Test]
    [Capability("CAP-CF-011")]
    public void Should_ReadSpecsAndContexts_NoPipelineButConformance_CarriesAClusterOrAzureCredential()
    {
        var credentialContexts = CodefreshRepository.Integrations
            .SelectMany(file => CodefreshRepository.Items(CodefreshRepository.Get(CodefreshRepository.Load(file), "contexts")))
            .Where(context => CodefreshRepository.Get(context, "data") is IDictionary<object, object?> data && data.Keys.Any(key => CredentialKey().IsMatch(key.ToString() ?? string.Empty)))
            .Select(context => CodefreshRepository.Get(context, "name") as string ?? string.Empty)
            .ToArray();
        var attached = CodefreshRepository.Specs
            .Select(spec => (Spec: spec, Document: CodefreshRepository.Load(spec)))
            .SelectMany(entry => CodefreshRepository.Contexts(entry.Document)
                .Where(context => credentialContexts.Contains(context))
                .Select(context => (entry.Spec, Pipeline: CodefreshRepository.Get(CodefreshRepository.Get(entry.Document, "metadata"), "name") as string ?? string.Empty, Context: context)))
            .ToArray();
        var appMentions = CodefreshRepository.Pipelines.Concat(CodefreshRepository.Specs).Concat(CodefreshRepository.Scripts)
            .Where(CodefreshRepository.IsAppFile)
            .SelectMany(file => CodeLines(CodefreshRepository.Read(file))
                .Where(line => AppForbiddenText.Any(text => line.Contains(text, StringComparison.Ordinal)) || AzureLogin().IsMatch(line))
                .Select(line => $"{file}: {line.Trim()}"));

        credentialContexts.ShouldBe(new[] { ConformanceContext }, "contexts that hold an Azure or cluster credential");
        attached.Where(entry => !ConformancePipeline().IsMatch(entry.Pipeline))
            .Select(entry => $"{entry.Spec}: {entry.Pipeline} attaches {entry.Context}")
            .ShouldBeEmpty("pipelines other than platform-env/conformance* with an Azure or cluster credential");
        appMentions.ShouldBeEmpty("app pipelines, specs or scripts that name an Azure or cluster credential");
    }

    private static IEnumerable<string> CodeLines(string text) =>
        text.Split('\n').Where(line => !line.TrimStart().StartsWith('#'));

    [GeneratedRegex(@"argo-?cd|kubectl|kubernetes|(^|[/-])helm($|[/-])", RegexOptions.IgnoreCase)]
    private static partial Regex ClusterStepType();

    [GeneratedRegex(@"(^|[^\w./-])(kubectl\s|helm\s+(install|upgrade|rollback|uninstall)\b|argocd\s|az\s+aks\s|az\s+login\b|kubelogin\s)")]
    private static partial Regex ClusterCommand();

    [GeneratedRegex(@"^(AZURE_CLIENT_SECRET|AZURE_FEDERATED_TOKEN\w*|ARM_\w+|KUBECONFIG|KUBE_\w+|ARGOCD_\w+)$")]
    private static partial Regex CredentialKey();

    [GeneratedRegex(@"(^|[^\w-])az\s+(login|account\s+get-access-token)\b")]
    private static partial Regex AzureLogin();

    [GeneratedRegex(@"^platform-env/conformance(-[a-z0-9-]+)?$")]
    private static partial Regex ConformancePipeline();
}
