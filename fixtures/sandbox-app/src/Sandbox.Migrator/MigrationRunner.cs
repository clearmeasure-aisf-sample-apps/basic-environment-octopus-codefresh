using Microsoft.Data.SqlClient;

namespace Sandbox.Migrator;

/// <summary>Applies pending scripts to the database, each in its own transaction, and journals them.</summary>
public sealed class MigrationRunner
{
    private const string JournalSql = """
        IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
            CREATE TABLE dbo.SchemaVersions (
                ScriptName nvarchar(255) NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
                AppliedAtUtc datetime2 NOT NULL);
        """;

    private readonly string connectionString;
    private readonly TextWriter log;

    /// <summary>Creates the runner.</summary>
    /// <param name="connectionString">Connection string of the app database (the migrator login).</param>
    /// <param name="log">Where progress goes.</param>
    public MigrationRunner(string connectionString, TextWriter log)
    {
        this.connectionString = connectionString;
        this.log = log;
    }

    /// <summary>Runs every pending script of <paramref name="scriptsFolder"/>; returns how many ran.</summary>
    /// <param name="scriptsFolder">Folder holding the <c>*.sql</c> scripts.</param>
    /// <param name="cancellationToken">Cancels the run between statements.</param>
    public async Task<int> RunAsync(string scriptsFolder, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, JournalSql, cancellationToken);

        var applied = new List<string>();
        await using (var select = new SqlCommand("SELECT ScriptName FROM dbo.SchemaVersions;", connection))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(reader.GetString(0));
            }
        }

        var available = Directory.EnumerateFiles(scriptsFolder, "*.sql").Select(Path.GetFileName).OfType<string>();
        var pending = ScriptPlan.Pending(available, applied);
        foreach (var script in pending)
        {
            log.WriteLine($"Executing {script}");
            var text = await File.ReadAllTextAsync(Path.Combine(scriptsFolder, script), cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var batch in ScriptPlan.Batches(text))
            {
                await ExecuteAsync(connection, transaction, batch, cancellationToken);
            }

            await using (var journal = new SqlCommand("INSERT INTO dbo.SchemaVersions (ScriptName, AppliedAtUtc) VALUES (@name, SYSUTCDATETIME());", connection, transaction))
            {
                journal.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 255) { Value = script });
                await journal.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        log.WriteLine(pending.Count == 0 ? "The database is up to date." : $"Applied {pending.Count} script(s).");
        return pending.Count;
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
