using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.SqlClient;

namespace Sandbox.Web;

/// <summary>Body of <c>PUT /data/canary</c>.</summary>
/// <param name="Value">Text to store, 1 to 200 characters.</param>
public sealed record CanaryRequest(string? Value);

/// <summary>Handlers of <c>/data/canary</c>, kept free of hosting so tests call them directly.</summary>
public static class CanaryEndpoints
{
    /// <summary>Longest canary value the table stores.</summary>
    public const int MaxValueLength = 200;

    /// <summary><c>GET /data/canary</c>: the stored canary, 404 before the first write, 503 without a database.</summary>
    /// <param name="store">Canary store, or <c>null</c> when no database is configured.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    public static async Task<Results<Ok<Canary>, NotFound, ProblemHttpResult>> GetAsync(ICanaryStore? store, CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return NoDatabase();
        }

        try
        {
            var canary = await store.GetAsync(cancellationToken);
            return canary is null ? TypedResults.NotFound() : TypedResults.Ok(canary);
        }
        catch (SqlException exception)
        {
            return DatabaseUnavailable(exception);
        }
    }

    /// <summary><c>PUT /data/canary</c>: validates and stores the canary.</summary>
    /// <param name="request">Request body.</param>
    /// <param name="store">Canary store, or <c>null</c> when no database is configured.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    public static async Task<Results<Ok<Canary>, BadRequest<string>, ProblemHttpResult>> PutAsync(CanaryRequest? request, ICanaryStore? store, CancellationToken cancellationToken)
    {
        var value = request?.Value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength)
        {
            return TypedResults.BadRequest($"value must hold 1 to {MaxValueLength} characters");
        }

        if (store is null)
        {
            return NoDatabase();
        }

        try
        {
            return TypedResults.Ok(await store.SetAsync(value, cancellationToken));
        }
        catch (SqlException exception)
        {
            return DatabaseUnavailable(exception);
        }
    }

    private static ProblemHttpResult NoDatabase() =>
        TypedResults.Problem("No database is configured (DB_HOST or ConnectionStrings__Sandbox).", statusCode: StatusCodes.Status503ServiceUnavailable);

    private static ProblemHttpResult DatabaseUnavailable(SqlException exception) =>
        TypedResults.Problem($"The database is unavailable: {exception.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
}
