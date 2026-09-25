using System.IO.Compression;
using System.Runtime.Versioning;
using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-006, offline half: the image contexts of the release images are staged from the app's own build output and
/// the environment repo's Dockerfiles: <c>stage-built.ps1</c> (the UI from the nupkg exactly as build.yml extracts it, or
/// from dotnet publish for previews; Worker and DbUp migrator from dotnet publish), <c>stage-images.ps1</c> (the sandbox
/// fixture) and <c>stage-dockerfiles.ps1</c> (the multi-image starter). A stub dotnet records the publish calls and
/// writes the assemblies.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
[UnsupportedOSPlatform("windows")]
public class StageScriptTests
{
    private const string UiDll = "ClearMeasure.Bootcamp.UI.Server.dll";

    private const string Publish = """
        if [ "$1" = publish ]; then
          out=""; prev=""; for a in "$@"; do [ "$prev" = "-o" ] && out="$a"; prev="$a"; done
          mkdir -p "$out"
          case "$2" in
            */UI.Server.csproj) printf 'MZ' >"$out/ClearMeasure.Bootcamp.UI.Server.dll" ;;
            */Worker.csproj) printf 'MZ' >"$out/Worker.dll" ;;
            */Database.csproj) printf 'MZ' >"$out/ClearMeasure.Bootcamp.Database.dll" ;;
            *) printf 'MZ' >"$out/$(basename "$2" .csproj).dll" ;;
          esac
        fi
        """;

    private static IEnumerable<string> BuiltScripts() => AppScriptSandbox.Copies("stage-built.ps1");

    private static IEnumerable<string> ImagesScripts() => AppScriptSandbox.Copies("stage-images.ps1");

    private static IEnumerable<string> DockerfileScripts() => AppScriptSandbox.Copies("stage-dockerfiles.ps1");

    /// <summary>
    /// The UI comes from the nupkg without package metadata and with readable files; Worker and migrator from dotnet
    /// publish, with the environment repo's Dockerfiles and an executable migrate.sh; the UI Dockerfile gets the OCI labels.
    /// </summary>
    /// <param name="script">A copy of stage-built.ps1.</param>
    [TestCaseSource(nameof(BuiltScripts))]
    [Capability("CAP-CF-006")]
    public void Should_RunStageBuilt_Nupkg_StagesTheThreeContexts(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        KitToolbox.Require("unzip");
        KitToolbox.Require("tar");
        var (repository, containers) = AppCheckout(sandbox);
        var output = Path.Combine(sandbox.Root, "contexts");
        sandbox.Stub("dotnet", Publish);

        var run = sandbox.RunIn(repository, script, "-Version", "2.5.9", "-Out", output, "-Containers", containers);

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.CallsOf("dotnet").Select(call => call.ToString()).ShouldBe(
        [
            $"dotnet publish {repository}/src/Worker/Worker.csproj --configuration Release --no-restore --no-build --nologo -o {output}/worker/publish",
            $"dotnet publish {repository}/src/Database/Database.csproj --configuration Release --no-restore --no-build --nologo -o {output}/db-migrator/publish",
        ]);
        var built = Path.Combine(output, "ui", "built");
        Files(built).ShouldBe([".hidden", UiDll, "app.json", "sub/keep.txt", "wwwroot/index.html"], "the dll's folder without package metadata");
        (File.GetUnixFileMode(Path.Combine(built, "app.json")) & UnixFileMode.UserRead).ShouldBe(UnixFileMode.UserRead, "files are made readable");
        var dockerfile = File.ReadAllText(Path.Combine(output, "ui", "Dockerfile"));
        dockerfile.ShouldStartWith("FROM mcr.microsoft.com/dotnet/aspnet:10.0\n");
        dockerfile.ShouldContain("LABEL org.opencontainers.image.source=\"https://github.com/");
        dockerfile.ShouldContain("org.opencontainers.image.version=\"${VERSION}\"");
        File.ReadAllText(Path.Combine(output, "worker", "Dockerfile")).ShouldBe("FROM runtime\n# worker\n");
        File.Exists(Path.Combine(output, "db-migrator", "scripts", "001_init.sql")).ShouldBeTrue();
        File.GetUnixFileMode(Path.Combine(output, "db-migrator", "migrate.sh")).ShouldBe(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>Previews publish the UI instead of reading a nupkg; -Only limits the contexts.</summary>
    /// <param name="script">A copy of stage-built.ps1.</param>
    [TestCaseSource(nameof(BuiltScripts))]
    [Capability("CAP-CF-006")]
    public void Should_RunStageBuilt_PublishSource_PublishesTheUi(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var (repository, containers) = AppCheckout(sandbox);
        File.Delete(Path.Combine(repository, "build", "ChurchBulletin.UI.2.5.9.nupkg"));
        var output = Path.Combine(sandbox.Root, "contexts");
        sandbox.Stub("dotnet", Publish);

        var run = sandbox.RunIn(repository, script, "-Version", "2.5.9-ci.abcdef0", "-UiSource", "publish", "-Only", "ui", "-Out", output, "-Containers", containers);

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.CallsOf("dotnet").Select(call => call.ToString()).ShouldBe(
            [$"dotnet publish {repository}/src/UI/Server/UI.Server.csproj --configuration Release --no-restore --no-build --nologo -o {output}/ui/built"]);
        Directory.GetDirectories(output).Select(folder => new DirectoryInfo(folder).Name).ShouldBe(["ui"]);
        run.Error.ShouldContain("ui context ready at");
    }

    /// <summary>A missing nupkg, a nupkg without the UI assembly or a missing Dockerfile fails the stage.</summary>
    /// <param name="script">A copy of stage-built.ps1.</param>
    [TestCaseSource(nameof(BuiltScripts))]
    [Capability("CAP-CF-006")]
    public void Should_RunStageBuilt_MissingInput_Fails(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var (repository, containers) = AppCheckout(sandbox);
        var output = Path.Combine(sandbox.Root, "contexts");
        sandbox.Stub("dotnet", Publish);

        var noPackage = sandbox.RunIn(repository, script, "-Version", "9.9.9", "-Out", output, "-Containers", containers);
        File.Delete(Path.Combine(containers, "worker", "Dockerfile"));
        var noDockerfile = sandbox.RunIn(repository, script, "-Version", "2.5.9", "-Only", "worker", "-Out", output, "-Containers", containers);

        noPackage.ExitCode.ShouldBe(1, noPackage.Transcript);
        noPackage.Error.ShouldContain("ChurchBulletin.UI.9.9.9.nupkg; run Package-Everything with BUILD_BUILDNUMBER=9.9.9");
        noDockerfile.ExitCode.ShouldBe(1, noDockerfile.Transcript);
        noDockerfile.Error.ShouldContain("worker: missing");
    }

    /// <summary>The sandbox migrator carries db/scripts, plus db/toggles when the commit carries toggles/failing-migration.</summary>
    /// <param name="script">A copy of stage-images.ps1.</param>
    [TestCaseSource(nameof(ImagesScripts))]
    [Capability("CAP-CF-006")]
    public void Should_RunStageImages_FailingMigrationMarker_AddsTheToggleScripts(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var repository = Path.Combine(sandbox.Root, "sandbox");
        sandbox.Write(Path.Combine(repository, "Sandbox.sln"), string.Empty);
        sandbox.Write(Path.Combine(repository, "db", "scripts", "001_a.sql"), "select 1;\n");
        sandbox.Write(Path.Combine(repository, "db", "toggles", "900_fail.sql"), "raiserror;\n");
        var containers = Path.Combine(sandbox.Root, "containers");
        sandbox.Write(Path.Combine(containers, "web", "Dockerfile"), "FROM web\n");
        sandbox.Write(Path.Combine(containers, "migrator", "Dockerfile"), "FROM migrator\n");
        sandbox.Write(Path.Combine(containers, "migrator", "migrate.sh"), "#!/bin/sh\n");
        sandbox.Stub("dotnet", Publish);

        var plain = sandbox.RunIn(repository, script, "-Out", Path.Combine(sandbox.Root, "plain"), "-Containers", containers);
        sandbox.Write(Path.Combine(repository, "toggles", "failing-migration"), string.Empty);
        var toggled = sandbox.RunIn(repository, script, "-Out", Path.Combine(sandbox.Root, "toggled"), "-Containers", containers);

        plain.ExitCode.ShouldBe(0, plain.Transcript);
        Files(Path.Combine(sandbox.Root, "plain", "migrator", "scripts")).ShouldBe(["001_a.sql"]);
        plain.Error.ShouldContain("web, migrator (1 script(s))");
        toggled.ExitCode.ShouldBe(0, toggled.Transcript);
        Files(Path.Combine(sandbox.Root, "toggled", "migrator", "scripts")).ShouldBe(["001_a.sql", "900_fail.sql"]);
        toggled.Error.ShouldContain("toggles/failing-migration is on");
        File.ReadAllText(Path.Combine(sandbox.Root, "toggled", "web", "Dockerfile")).ShouldBe("FROM web\n");
        toggled.CallsOf("dotnet").Select(call => call.Arguments[1]).ShouldBe([$"{repository}/src/Sandbox.Web/Sandbox.Web.csproj", $"{repository}/src/Sandbox.Migrator/Sandbox.Migrator.csproj"]);
    }

    /// <summary>The multi-image starter copies each image's Dockerfile to .platform/&lt;image&gt;.Dockerfile of the checkout.</summary>
    /// <param name="script">A copy of stage-dockerfiles.ps1.</param>
    [TestCaseSource(nameof(DockerfileScripts))]
    [Capability("CAP-CF-006")]
    public void Should_RunStageDockerfiles_Images_CopyEachDockerfile(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var containers = Path.Combine(sandbox.Root, "containers");
        sandbox.Write(Path.Combine(containers, "web", "Dockerfile"), "FROM web\n");
        sandbox.Write(Path.Combine(containers, "migrator", "Dockerfile"), "FROM migrator\n");

        var run = sandbox.Run(script, "-Image", "web,migrator", "-Containers", containers);
        var missing = sandbox.Run(script, "-Image", "api", "-Containers", containers);

        run.ExitCode.ShouldBe(0, run.Transcript);
        File.ReadAllText(Path.Combine(sandbox.Work, ".platform", "web.Dockerfile")).ShouldBe("FROM web\n");
        File.ReadAllText(Path.Combine(sandbox.Work, ".platform", "migrator.Dockerfile")).ShouldBe("FROM migrator\n");
        missing.ExitCode.ShouldBe(1, missing.Transcript);
    }

    /// <summary>The relative paths of the files under a folder, sorted.</summary>
    private static string[] Files(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// An app checkout after Package-Everything: root Dockerfile, build/ChurchBulletin.UI.2.5.9.nupkg (package metadata
    /// around lib/net10.0/, where the UI assembly sits with an unreadable file and nested metadata folders) and
    /// src/Database/scripts; and a containers folder with the Worker and migrator Dockerfiles.
    /// </summary>
    private static (string Repository, string Containers) AppCheckout(AppScriptSandbox sandbox)
    {
        var repository = Path.Combine(sandbox.Root, "app");
        sandbox.Git(sandbox.Root, "init", "-q", "-b", "master", repository);
        sandbox.Write(Path.Combine(repository, "Dockerfile"), "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nCOPY built/ /app/\n");
        sandbox.Write(Path.Combine(repository, "src", "Database", "scripts", "001_init.sql"), "select 1;\n");
        var package = Path.Combine(repository, "build", "ChurchBulletin.UI.2.5.9.nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            void Add(string name, string content, int mode = 0b110_100_100)
            {
                var entry = zip.CreateEntry(name);
                entry.ExternalAttributes = (0x8000 | mode) << 16;
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }

            Add("ChurchBulletin.UI.nuspec", "<package/>");
            Add("[Content_Types].xml", "<Types/>");
            Add("_rels/.rels", "<Relationships/>");
            Add("package/services/metadata/core-properties/abc.psmdcp", "<core/>");
            Add($"lib/net10.0/{UiDll}", "MZ", 0b110_000_000);
            Add("lib/net10.0/app.json", "{}", 0);
            Add("lib/net10.0/.hidden", "h");
            Add("lib/net10.0/wwwroot/index.html", "<html/>");
            Add("lib/net10.0/extra.nuspec", "n");
            Add("lib/net10.0/[Content_Types].xml", "c");
            Add("lib/net10.0/_rels/y.rels", "r");
            Add("lib/net10.0/sub/keep.txt", "k");
            Add("lib/net10.0/package/services/metadata/m.txt", "m");
        }

        var containers = Path.Combine(sandbox.Root, "containers");
        sandbox.Write(Path.Combine(containers, "worker", "Dockerfile"), "FROM runtime\n# worker\n");
        sandbox.Write(Path.Combine(containers, "db-migrator", "Dockerfile"), "FROM runtime\n# migrator\n");
        sandbox.Write(Path.Combine(containers, "db-migrator", "migrate.sh"), "#!/bin/sh\nexec dotnet ClearMeasure.Bootcamp.Database.dll\n");
        return (repository, containers);
    }
}
