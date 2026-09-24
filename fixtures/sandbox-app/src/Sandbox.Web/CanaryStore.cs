using Microsoft.Data.SqlClient;

namespace Sandbox.Web;

/// <summary>The canary row that conformance tests write before a disruption and read after it.</summary>
/// <param name="Value">The stored text.</param>
/// <param name="UpdatedAtUtc">When it was last written.</param>
public sealed record Canary(string Value, DateTime UpdatedAtUtc);

/// <summary>Reads and writes the single canary row.</summary>
public interface ICanaryStore
{
    /// <summary>Returns the canary, or <c>null</c> when none was written yet.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<Canary?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Writes the canary and returns what was stored.</summary>
    /// <param name="value">Validated text.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    Task<Canary> SetAsync(string value, CancellationToken cancellationToken);
}

/// <summary>SQL Server implementation over table <c>dbo.Canary</c> (script 0001).</summary>
public sealed class SqlCanaryStore : ICanaryStore
{
    private readonly string connectionString;

    /// <summary>Creates the store.</summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    public SqlCanaryStore(string connectionString)
    {
        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<Canary?> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT [Value], [UpdatedAtUtc] FROM dbo.Canary WHERE Id = 1;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Canary(reader.GetString(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
    }

    /// <inheritdoc />
    public async Task<Canary> SetAsync(string value, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.Canary SET [Value] = @value, [UpdatedAtUtc] = @now WHERE Id = 1;
            IF @@ROWCOUNT = 0 INSERT INTO dbo.Canary (Id, [Value], [UpdatedAtUtc]) VALUES (1, @value, @now);
            """;
        var now = DateTime.UtcNow;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@value", System.Data.SqlDbType.NVarChar, CanaryEndpoints.MaxValueLength) { Value = value });
        command.Parameters.Add(new SqlParameter("@now", System.Data.SqlDbType.DateTime2) { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new Canary(value, now);
    }
}
