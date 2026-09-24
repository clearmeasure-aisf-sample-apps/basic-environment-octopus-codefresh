using Sandbox.Web;

// The platform's conformance fixture (ADR-IR34 "Testability hooks"): /healthz for probes,
// /version for the deployed release, and /data/canary, a SQL-backed row that the conformance suite
// writes before a disruption (sleep, rebuild, restore) and reads afterwards.
var builder = WebApplication.CreateBuilder(args);

var connectionString = DatabaseSettings.BuildConnectionString(builder.Configuration, trustServerCertificateByDefault: true);
if (connectionString is not null)
{
    builder.Services.AddSingleton<ICanaryStore>(new SqlCanaryStore(connectionString));
}

var app = builder.Build();
var version = VersionInfo.From(app.Configuration);

app.MapGet("/healthz", () => TypedResults.Ok(new { status = "ok" }));
app.MapGet("/version", () => TypedResults.Ok(version));
app.MapGet("/data/canary", (IServiceProvider services, CancellationToken cancellationToken) =>
    CanaryEndpoints.GetAsync(services.GetService<ICanaryStore>(), cancellationToken));
app.MapPut("/data/canary", (CanaryRequest? request, IServiceProvider services, CancellationToken cancellationToken) =>
    CanaryEndpoints.PutAsync(request, services.GetService<ICanaryStore>(), cancellationToken));

app.Run();

/// <summary>Entry point, public for tests.</summary>
public partial class Program
{
}
