using Platform.Onboarding.Cli;
using Platform.Onboarding.Repository;

namespace Platform.Onboarding.Tests.Support;

/// <summary>
/// A throw-away environment repository with the committed descriptor schema (apps/schema.json of this repository, the
/// single source of the shape rules) and small fixture starters for each role.
/// </summary>
internal sealed class TempRepository : IDisposable
{
    public TempRepository()
    {
        Root = Path.Combine(Path.GetTempPath(), "onboarding-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "apps"));
        File.Copy(CommittedSchemaPath, Path.Combine(Root, "apps", "schema.json"));
    }

    public string Root { get; }

    public PlatformRepository Repository => new(Root);

    /// <summary>apps/schema.json of the repository that holds these tests.</summary>
    public static string CommittedSchemaPath
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "apps", "schema.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException("apps/schema.json not found above the test assembly");
        }
    }

    public string Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }

    public string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    public bool Exists(string relative)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>Fixture starters in the documented layout: one per role, using both token spellings.</summary>
    public TempRepository WithStarters()
    {
        Write("codefresh/templates/minimal/README.md", "Starter description, never copied.\n");
        Write("codefresh/templates/minimal/pipelines/ci.yml", "version: \"1.0\"\n# app <app> from <app-repo> on <app-branch>\nsteps: {}\n");
        Write("codefresh/templates/minimal/pipelines/release.yml", "version: \"1.0\"\nsteps:\n  build:\n    image_name: apps/<app>/web\n");
        Write("codefresh/templates/minimal/specs/ci.yml", "version: \"1.0\"\nkind: pipeline\nmetadata:\n  name: <app>/ci\n  project: <app>\n");
        Write("codefresh/templates/minimal/specs/release.yml", "version: \"1.0\"\nkind: pipeline\nmetadata:\n  name: __APP__/release\n  project: __APP__\n");
        Write("octopus/templates/deploy-minimal/deployment_process.ocl", "step \"wake\" {\n  name = \"Wake <project>\"\n}\n");
        Write("octopus/templates/deploy-minimal/variables.ocl", "variable \"App.Name\" {\n  value \"__APP__\" {}\n}\n");
        Write("octopus/templates/db-runbooks/runbooks/db-restore.ocl", "name = \"db-restore\"\n");
        Write("gitops/templates/kustomize/app/base/kustomization.yaml", "resources: [deployment.yaml]\n");
        Write("gitops/templates/kustomize/app/base/deployment.yaml", "kind: Deployment\nmetadata:\n  name: web\n");
        Write("gitops/templates/kustomize/envs/<env>/<deployable>/kustomization.yaml",
            "namespace: <namespace>\nresources: [../../../app/base]\nimages:\n  - name: <acr-name>.azurecr.io/apps/<app>/web\n    newTag: 0.0.0-bootstrap\n");
        Write("gitops/templates/kustomize/envs/__ENV__/db/kustomization.yaml", "namespace: <app>-__ENV__\n");
        return this;
    }

    /// <summary>Runs the command line in-process.</summary>
    public (int ExitCode, string Output, string Error) Run(IEnvironment? environment, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = new OnboardingCli(output, error, environment ?? new StubEnvironment(), Root).Run(args);
        return (code, output.ToString(), error.ToString());
    }

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
}
