using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace DependencyInventory.Tests;

public sealed class RetryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly List<TimeSpan> _delays = [];
    private readonly List<RetryNotice> _notices = [];

    private (HttpClient Client, ScriptedHandler Server) Create(params Func<HttpResponseMessage>[] responses) =>
        Create(new RetryOptions(), responses);

    private (HttpClient Client, ScriptedHandler Server) Create(
        RetryOptions options, params Func<HttpResponseMessage>[] responses)
    {
        var server = new ScriptedHandler(responses);
        var handler = new RetryHandler(options, _notices.Add, new FixedTime(Now),
            (delay, _) => { _delays.Add(delay); return Task.CompletedTask; }, () => 0.5)
        {
            InnerHandler = server
        };
        return (new HttpClient(handler), server);
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);

    private static HttpResponseMessage TooManyRequests(RetryConditionHeaderValue? retryAfter = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = retryAfter;
        return response;
    }

    [Fact]
    public async Task WaitsTheRetryAfterDeltaThenRetries()
    {
        var (client, server) = Create(
            () => TooManyRequests(new RetryConditionHeaderValue(TimeSpan.FromSeconds(7))), Ok);

        var response = await client.GetAsync("https://example.test/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.Calls);
        Assert.Equal([TimeSpan.FromSeconds(7)], _delays);
        var notice = Assert.Single(_notices);
        Assert.Equal(new RetryNotice(HttpStatusCode.TooManyRequests, 1, 5, TimeSpan.FromSeconds(7)), notice);
    }

    [Fact]
    public async Task WaitsUntilTheRetryAfterDate()
    {
        var (client, _) = Create(
            () => TooManyRequests(new RetryConditionHeaderValue(Now.AddSeconds(30))), Ok);

        await client.GetAsync("https://example.test/");

        Assert.Equal([TimeSpan.FromSeconds(30)], _delays);
    }

    [Fact]
    public async Task RetryAfterDateInThePastMeansNoWait()
    {
        var (client, _) = Create(
            () => TooManyRequests(new RetryConditionHeaderValue(Now.AddSeconds(-30))), Ok);

        await client.GetAsync("https://example.test/");

        Assert.Equal([TimeSpan.Zero], _delays);
    }

    [Fact]
    public async Task WithoutRetryAfterBacksOffExponentiallyWithJitter()
    {
        var (client, _) = Create(() => TooManyRequests(), () => TooManyRequests(), () => TooManyRequests(), Ok);

        await client.GetAsync("https://example.test/");

        // Base 1 s doubled at each attempt; the random half is 0.5 here, so 75 % of each step.
        Assert.Equal([TimeSpan.FromSeconds(0.75), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3)], _delays);
    }

    [Fact]
    public async Task WaitsAreCappedAtTheMaximumDelay()
    {
        var (client, _) = Create(
            () => TooManyRequests(new RetryConditionHeaderValue(TimeSpan.FromMinutes(10))), Ok);

        await client.GetAsync("https://example.test/");

        Assert.Equal([TimeSpan.FromSeconds(60)], _delays);
    }

    [Fact]
    public async Task ReturnsTheLast429WhenAttemptsAreExhausted()
    {
        var (client, server) = Create(new RetryOptions { MaxAttempts = 3 },
            () => TooManyRequests(), () => TooManyRequests(), () => TooManyRequests(), Ok);

        var response = await client.GetAsync("https://example.test/");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(3, server.Calls);
        Assert.Equal(2, _delays.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task OtherStatusesAreNotRetried(HttpStatusCode status)
    {
        var (client, server) = Create(() => new HttpResponseMessage(status), Ok);

        var response = await client.GetAsync("https://example.test/");

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, server.Calls);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task CancellationDuringTheWaitStopsTheRetries()
    {
        using var cancellation = new CancellationTokenSource();
        var server = new ScriptedHandler([() => TooManyRequests(), Ok]);
        var handler = new RetryHandler(new RetryOptions(), delay: (_, ct) =>
        {
            cancellation.Cancel();
            return Task.Delay(Timeout.Infinite, ct);
        }) { InnerHandler = server };
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("https://example.test/", cancellation.Token));
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task AnAttemptLongerThanItsTimeoutFailsAsATimeout()
    {
        var server = new ScriptedHandler([]) { Hang = true };
        var handler = new RetryHandler(new RetryOptions { AttemptTimeout = TimeSpan.FromMilliseconds(50) })
        {
            InnerHandler = server
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var exception = await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.GetAsync("https://example.test/"));
        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    private sealed class ScriptedHandler(IEnumerable<Func<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new(responses);

        public int Calls { get; private set; }
        public bool Hang { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            return _responses.Dequeue()();
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
