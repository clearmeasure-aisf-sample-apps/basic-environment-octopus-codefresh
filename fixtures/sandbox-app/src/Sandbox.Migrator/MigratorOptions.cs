using Microsoft.Data.SqlClient;

namespace Sandbox.Migrator;

/// <summary>
/// Settings of one migrator run, from arguments and environment variables. The connection comes from
/// <c>ConnectionStrings__Sandbox</c>, or from <c>DB_HOST</c>, <c>DB_PORT</c>, <c>DB_NAME</c> (or <c>DB_DATABASE</c>),
/// <c>DB_USER</c> (or <c>DB_USERNAME</c>) and <c>DB_PASSWORD</c>, the keys of Secret <c>db-migrator</c>.
/// The server certificate is validated (trust anchors from <c>SSL_CERT_FILE</c>) unless
/// <c>DB_TRUST_SERVER_CERTIFICATE=true</c>.
/// </summary>
/// <param name="ConnectionString">SQL Server connection string.</param>
/// <param name="ScriptsFolder">Folder holding the scripts.</param>
public sealed record MigratorOptions(string ConnectionString, string ScriptsFolder)
{
    /// <summary>Default scripts folder: <c>scripts</c> next to the executable.</summary>
    public static string DefaultScriptsFolder => Path.Combine(AppContext.BaseDirectory, "scripts");

    /// <summary>Parses <c>[update] [--scripts &lt;folder&gt;]</c>; returns <c>null</c> and a message when invalid.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="environment">Environment variable lookup.</param>
    /// <param name="error">Why the input is invalid.</param>
    public static MigratorOptions? Parse(IReadOnlyList<string> args, Func<string, string?> environment, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        error = string.Empty;
        var scripts = DefaultScriptsFolder;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "update":
                    break;
                case "--scripts" when index + 1 < args.Count:
                    scripts = args[++index];
                    break;
                default:
                    error = $"Unknown argument '{args[index]}'. Usage: Sandbox.Migrator [update] [--scripts <folder>]";
                    return null;
            }
        }

        var explicitConnectionString = environment("ConnectionStrings__Sandbox");
        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            return new MigratorOptions(explicitConnectionString, scripts);
        }

        var host = First(environment, "DB_HOST", "DB_host");
        if (host is null)
        {
            error = "Set DB_HOST (and DB_PORT, DB_NAME, DB_USER, DB_PASSWORD) or ConnectionStrings__Sandbox.";
            return null;
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{First(environment, "DB_PORT", "DB_port") ?? "1433"}",
            InitialCatalog = First(environment, "DB_NAME", "DB_DATABASE", "DB_database") ?? "sandbox",
            UserID = First(environment, "DB_USER", "DB_USERNAME", "DB_username") ?? string.Empty,
            Password = First(environment, "DB_PASSWORD", "DB_password") ?? string.Empty,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = bool.TryParse(environment("DB_TRUST_SERVER_CERTIFICATE"), out var trust) && trust,
            ConnectTimeout = 15,
            ApplicationName = "sandbox-migrator",
        };
        return new MigratorOptions(builder.ConnectionString, scripts);
    }

    private static string? First(Func<string, string?> environment, params string[] names)
    {
        foreach (var name in names)
        {
            var value = environment(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
