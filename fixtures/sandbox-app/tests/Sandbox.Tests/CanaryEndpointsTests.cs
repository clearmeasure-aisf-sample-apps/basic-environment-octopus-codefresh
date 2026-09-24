using Microsoft.AspNetCore.Http.HttpResults;
using Sandbox.Web;

namespace Sandbox.Tests;

[TestFixture]
public sealed class CanaryEndpointsTests
{
    [Test]
    public async Task Should_GetAsync_NoCanaryWritten_ReturnsNotFound()
    {
        var store = new StubCanaryStore();

        var result = await CanaryEndpoints.GetAsync(store, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFound>();
    }

    [Test]
    public async Task Should_GetAsync_CanaryWritten_ReturnsIt()
    {
        var canary = new Canary("before-sleep", new DateTime(2026, 9, 24, 1, 2, 3, DateTimeKind.Utc));
        var store = new StubCanaryStore(canary);

        var result = await CanaryEndpoints.GetAsync(store, CancellationToken.None);

        result.Result.ShouldBeOfType<Ok<Canary>>().Value.ShouldBe(canary);
    }

    [Test]
    public async Task Should_GetAsync_NoDatabase_ReturnsServiceUnavailable()
    {
        var result = await CanaryEndpoints.GetAsync(null, CancellationToken.None);

        result.Result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(503);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task Should_PutAsync_EmptyValue_ReturnsBadRequest(string? value)
    {
        var store = new StubCanaryStore();

        var result = await CanaryEndpoints.PutAsync(new CanaryRequest(value), store, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequest<string>>();
        store.Stored.ShouldBeNull();
    }

    [Test]
    public async Task Should_PutAsync_ValueLongerThanColumn_ReturnsBadRequest()
    {
        var store = new StubCanaryStore();

        var result = await CanaryEndpoints.PutAsync(new CanaryRequest(new string('x', CanaryEndpoints.MaxValueLength + 1)), store, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequest<string>>();
    }

    [Test]
    public async Task Should_PutAsync_ValidValue_StoresTrimmedValue()
    {
        var store = new StubCanaryStore();

        var result = await CanaryEndpoints.PutAsync(new CanaryRequest("  conformance:run-42  "), store, CancellationToken.None);

        result.Result.ShouldBeOfType<Ok<Canary>>().Value!.Value.ShouldBe("conformance:run-42");
        store.Stored!.Value.ShouldBe("conformance:run-42");
    }

    [Test]
    public async Task Should_PutAsync_NoDatabase_ReturnsServiceUnavailable()
    {
        var result = await CanaryEndpoints.PutAsync(new CanaryRequest("value"), null, CancellationToken.None);

        result.Result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(503);
    }
}
