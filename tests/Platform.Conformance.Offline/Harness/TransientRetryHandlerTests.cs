using System.Net;
using System.Net.Http.Headers;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves that the REST clients retry reads after transient failures and never resend a write.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class TransientRetryHandlerTests
{
    private static readonly Uri Resource = new("https://api.example.test/api/tasks/ServerTasks-1");

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenSendAsync_GetResetTwiceThenAnswered_RetriesWithBackoffAndReturnsTheAnswer()
    {
        var attempts = 0;
        var stub = new StubHttpMessageHandler(_ => ++attempts <= 2
            ? throw new HttpRequestException(HttpRequestError.ResponseEnded, "Connection reset by peer")
            : StubHttpMessageHandler.Json("""{ "Id": "ServerTasks-1" }"""));
        var delays = new List<TimeSpan>();
        using var http = Client(stub, delays);

        using var response = await http.GetAsync(Resource);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        stub.Requests.Count.ShouldBe(3);
        delays.ShouldBe([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]);
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenSendAsync_GetAlwaysAnswers503_ReturnsTheLastAnswerAfterThreeRetries()
    {
        var stub = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Text("busy", HttpStatusCode.ServiceUnavailable));
        var delays = new List<TimeSpan>();
        using var http = Client(stub, delays);

        using var response = await http.GetAsync(Resource);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        stub.Requests.Count.ShouldBe(TransientRetryHandler.MaxRetries + 1);
        delays.Count.ShouldBe(TransientRetryHandler.MaxRetries);
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenSendAsync_GetAnswers429WithRetryAfter_WaitsThatLongThenRetries()
    {
        var attempts = 0;
        var stub = new StubHttpMessageHandler(_ =>
        {
            if (++attempts > 1)
            {
                return StubHttpMessageHandler.Json("{}");
            }

            var throttled = StubHttpMessageHandler.Text("slow down", HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return throttled;
        });
        var delays = new List<TimeSpan>();
        using var http = Client(stub, delays);

        using var response = await http.GetAsync(Resource);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        delays.ShouldBe([TimeSpan.FromSeconds(5)]);
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenSendAsync_GetAnswers404_ReturnsItWithoutRetrying()
    {
        var stub = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound));
        var delays = new List<TimeSpan>();
        using var http = Client(stub, delays);

        using var response = await http.GetAsync(Resource);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        stub.Requests.ShouldHaveSingleItem();
        delays.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenSendAsync_PostConnectionReset_SendsItOnceAndRethrows()
    {
        var stub = new StubHttpMessageHandler(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "Connection reset by peer"));
        var delays = new List<TimeSpan>();
        using var http = Client(stub, delays);

        await Should.ThrowAsync<HttpRequestException>(() => http.PostAsync(new Uri("https://api.example.test/api/Spaces-1/releases/create/v1"), new StringContent("{}")));

        stub.Requests.ShouldHaveSingleItem().Method.ShouldBe("POST");
        delays.ShouldBeEmpty();
    }

    private static HttpClient Client(StubHttpMessageHandler stub, List<TimeSpan> delays) =>
        new(new TransientRetryHandler(stub, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }));
}
