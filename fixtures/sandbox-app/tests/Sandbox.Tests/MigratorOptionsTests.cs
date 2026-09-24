using Microsoft.Data.SqlClient;
using Sandbox.Migrator;

namespace Sandbox.Tests;

[TestFixture]
public sealed class MigratorOptionsTests
{
    [Test]
    public void Should_Parse_SecretVariables_ValidatesServerCertificate()
    {
        var environment = Environment(new() { ["DB_HOST"] = "db", ["DB_NAME"] = "sandbox", ["DB_USER"] = "sandbox_migrator", ["DB_PASSWORD"] = "not-a-real-password" });

        var options = MigratorOptions.Parse(["update"], environment, out var error);

        error.ShouldBeEmpty();
        var builder = new SqlConnectionStringBuilder(options!.ConnectionString);
        builder.DataSource.ShouldBe("db,1433");
        builder.UserID.ShouldBe("sandbox_migrator");
        builder.Encrypt.ShouldBe(SqlConnectionEncryptOption.Mandatory);
        builder.TrustServerCertificate.ShouldBeFalse();
        options.ScriptsFolder.ShouldBe(MigratorOptions.DefaultScriptsFolder);
    }

    [Test]
    public void Should_Parse_EnvFromPrefixedSecretKeys_ReadsThem()
    {
        var environment = Environment(new() { ["DB_host"] = "db", ["DB_port"] = "14330", ["DB_database"] = "sandbox", ["DB_username"] = "sandbox_migrator" });

        var options = MigratorOptions.Parse([], environment, out _);

        var builder = new SqlConnectionStringBuilder(options!.ConnectionString);
        builder.DataSource.ShouldBe("db,14330");
        builder.InitialCatalog.ShouldBe("sandbox");
    }

    [Test]
    public void Should_Parse_TrustFlagTrue_TrustsServerCertificate()
    {
        var environment = Environment(new() { ["DB_HOST"] = "localhost", ["DB_TRUST_SERVER_CERTIFICATE"] = "true" });

        var options = MigratorOptions.Parse([], environment, out _);

        new SqlConnectionStringBuilder(options!.ConnectionString).TrustServerCertificate.ShouldBeTrue();
    }

    [Test]
    public void Should_Parse_ScriptsOption_UsesFolder()
    {
        var environment = Environment(new() { ["DB_HOST"] = "db" });

        var options = MigratorOptions.Parse(["update", "--scripts", "/app/scripts"], environment, out _);

        options!.ScriptsFolder.ShouldBe("/app/scripts");
    }

    [Test]
    public void Should_Parse_NoServer_ReturnsError()
    {
        var options = MigratorOptions.Parse([], Environment(new()), out var error);

        options.ShouldBeNull();
        error.ShouldContain("DB_HOST");
    }

    [Test]
    public void Should_Parse_UnknownArgument_ReturnsError()
    {
        var options = MigratorOptions.Parse(["rebuild"], Environment(new() { ["DB_HOST"] = "db" }), out var error);

        options.ShouldBeNull();
        error.ShouldContain("rebuild");
    }

    private static Func<string, string?> Environment(Dictionary<string, string?> values) =>
        name => values.TryGetValue(name, out var value) ? value : null;
}
