using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Sandbox.Web;

namespace Sandbox.Tests;

[TestFixture]
public sealed class DatabaseSettingsTests
{
    [Test]
    public void Should_BuildConnectionString_NoDatabaseKeys_ReturnsNull()
    {
        var configuration = Configuration(new Dictionary<string, string?>());

        var connectionString = DatabaseSettings.BuildConnectionString(configuration, trustServerCertificateByDefault: true);

        connectionString.ShouldBeNull();
    }

    [Test]
    public void Should_BuildConnectionString_SecretKeys_TrustsServerByDefault()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["DB_host"] = "db",
            ["DB_port"] = "1433",
            ["DB_database"] = "sandbox",
            ["DB_username"] = "sandbox_app",
            ["DB_password"] = "not-a-real-password",
        });

        var builder = new SqlConnectionStringBuilder(DatabaseSettings.BuildConnectionString(configuration, trustServerCertificateByDefault: true));

        builder.DataSource.ShouldBe("db,1433");
        builder.InitialCatalog.ShouldBe("sandbox");
        builder.UserID.ShouldBe("sandbox_app");
        builder.Encrypt.ShouldBe(SqlConnectionEncryptOption.Mandatory);
        builder.TrustServerCertificate.ShouldBeTrue();
    }

    [Test]
    public void Should_BuildConnectionString_TrustFlagFalse_ValidatesCertificate()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["DB_HOST"] = "db",
            ["DB_TRUST_SERVER_CERTIFICATE"] = "false",
        });

        var builder = new SqlConnectionStringBuilder(DatabaseSettings.BuildConnectionString(configuration, trustServerCertificateByDefault: true));

        builder.TrustServerCertificate.ShouldBeFalse();
        builder.DataSource.ShouldBe("db,1433");
    }

    [Test]
    public void Should_BuildConnectionString_ExplicitConnectionString_Wins()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sandbox"] = "Server=elsewhere;Database=x",
            ["DB_HOST"] = "db",
        });

        var connectionString = DatabaseSettings.BuildConnectionString(configuration, trustServerCertificateByDefault: true);

        connectionString.ShouldBe("Server=elsewhere;Database=x");
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
