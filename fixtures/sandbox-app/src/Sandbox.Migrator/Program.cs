using Microsoft.Data.SqlClient;
using Sandbox.Migrator;

// The sandbox's migration console: applies db/scripts (copied next to the executable) that are
// not yet journaled in dbo.SchemaVersions. Exit codes: 0 applied or up to date, 1 a script or the
// connection failed, 2 invalid arguments or settings. The platform's migrate.sh entrypoint retries
// it until MIGRATION_DB_READY_TIMEOUT_SECONDS pass.
var options = MigratorOptions.Parse(args, Environment.GetEnvironmentVariable, out var error);
if (options is null)
{
    Console.Error.WriteLine(error);
    return 2;
}

if (!Directory.Exists(options.ScriptsFolder))
{
    Console.Error.WriteLine($"Scripts folder {options.ScriptsFolder} does not exist.");
    return 2;
}

try
{
    await new MigrationRunner(options.ConnectionString, Console.Out).RunAsync(options.ScriptsFolder, CancellationToken.None);
    return 0;
}
catch (SqlException exception)
{
    Console.Error.WriteLine($"Migration failed: {exception.Message}");
    return 1;
}
