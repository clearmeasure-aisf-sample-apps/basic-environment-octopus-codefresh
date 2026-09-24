using Microsoft.Data.SqlClient;

namespace Sandbox.Web;

/// <summary>
/// Builds the SQL Server connection string of the sandbox from configuration. Either
/// <c>ConnectionStrings:Sandbox</c> is set, or the keys of the platform's database Secrets:
/// <c>DB_HOST</c>, <c>DB_PORT</c>, <c>DB_NAME</c>, <c>DB_USER</c> and <c>DB_PASSWORD</c>.
/// Configuration keys are case-insensitive, so a Secret mapped with <c>envFrom</c> and prefix <c>DB_</c>
/// (keys host, port, database, username, password) also works for all but <c>DB_NAME</c> and <c>DB_USER</c>,
/// which accept <c>DB_database</c> and <c>DB_username</c> too.
/// </summary>
public static class DatabaseSettings
{
    /// <summary>Name of the connection string that overrides the individual keys.</summary>
    public const string ConnectionStringName = "Sandbox";

    /// <summary>
    /// Returns the connection string, or <c>null</c> when the configuration names no database.
    /// </summary>
    /// <param name="configuration">Application configuration (environment variables included).</param>
    /// <param name="trustServerCertificateByDefault">
    /// Whether to skip certificate validation when <c>DB_TRUST_SERVER_CERTIFICATE</c> is unset. The app trusts the
    /// in-cluster server (ADR-IR34 decision 8); the migrator validates it against <c>SSL_CERT_FILE</c>.
    /// </param>
    public static string? BuildConnectionString(IConfiguration configuration, bool trustServerCertificateByDefault)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var explicitConnectionString = configuration.GetConnectionString(ConnectionStringName);
        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            return explicitConnectionString;
        }

        var host = First(configuration, "DB_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var port = First(configuration, "DB_PORT") ?? "1433";
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = First(configuration, "DB_NAME", "DB_DATABASE") ?? "sandbox",
            UserID = First(configuration, "DB_USER", "DB_USERNAME") ?? string.Empty,
            Password = First(configuration, "DB_PASSWORD") ?? string.Empty,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = ReadFlag(configuration, "DB_TRUST_SERVER_CERTIFICATE", trustServerCertificateByDefault),
            ConnectTimeout = 15,
            ApplicationName = "sandbox",
        };
        return builder.ConnectionString;
    }

    private static string? First(IConfiguration configuration, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool ReadFlag(IConfiguration configuration, string key, bool fallback)
    {
        var value = configuration[key];
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
