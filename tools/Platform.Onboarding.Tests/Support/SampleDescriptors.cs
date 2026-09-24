namespace Platform.Onboarding.Tests.Support;

/// <summary>Descriptor texts for tests.</summary>
internal static class SampleDescriptors
{
    public static string Minimal(string app = "demoapp", string repository = "clearmeasure-aisf-sample-apps/20260924-001") => $"""
        schema: 1
        name: {app}
        repositories:
          - name: {repository}
            defaultBranch: main
        codefresh:
          projects: [{app}]
        octopus:
          projects:
            - name: {app}
        deployables:
          - name: app
            octopusProject: {app}
            packaging: kustomize
            images: [web]
        """;

    public static string WithDatabase(string app = "demoapp") => Minimal(app) + "\ndatabase:\n  engine: mssql-2022-express\n";
}
