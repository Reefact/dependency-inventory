using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace DependencyInventory.Tests;

public sealed class RetryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly List<TimeSpan> _delays = [];
    private readonly List<RetryNotice> _notices = [];

    private (HttpClient Client, ScriptedHandler Server) Create(params Step[] steps) =>
        Create(new RetryOptions(), steps);

    private (HttpClient Client, ScriptedHandler Server) Create(RetryOptions options, params Step[] steps)
    {
        var server = new ScriptedHandler(steps);
        var handler = new RetryHandler(options, _notices.Add, new FixedTime(Now),
            (delay, _) => { _delays.Add(delay); return Task.CompletedTask; }, () => 0.5)
        {
            InnerHandler = server
        };
        return (new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, server);
    }

    private static Step Status(HttpStatusCode status, RetryConditionHeaderValue? retryAfter = null) => _ =>
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = retryAfter;
        return Task.FromResult(response);
    };

    private static readonly Step Ok = Status(HttpStatusCode.OK);
    private static readonly Step TooManyRequests = Status(HttpStatusCode.TooManyRequests);
    private static readonly Step NetworkError = _ => throw new HttpRequestException("connection reset");

    private static Step Body(string text) => _ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });

    private static readonly Step HeadersThenHangingBody = _ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new FaultyContent(hang: true) });

    private static readonly Step HeadersThenBrokenBody = _ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new FaultyContent(hang: false) });

    private static readonly Step Hang = async ct =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    };

    // --- HTTP 429 (issue #1) ---------------------------------------------------------------

    [Fact]
    public async Task WaitsTheRetryAfterDeltaThenRetries()
    {
        var (client, server) = Create(
            Status(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(TimeSpan.FromSeconds(7))), Ok);

        var response = await client.GetAsync("https://example.test/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.Calls);
        Assert.Equal([TimeSpan.FromSeconds(7)], _delays);
        Assert.Equal(new RetryNotice(HttpStatusCode.TooManyRequests, null, 1, 5, TimeSpan.FromSeconds(7)),
            Assert.Single(_notices));
    }

    [Fact]
    public async Task WaitsUntilTheRetryAfterDate()
    {
        var (client, _) = Create(
            Status(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(Now.AddSeconds(30))), Ok);

        await client.GetAsync("https://example.test/");

        Assert.Equal([TimeSpan.FromSeconds(30)], _delays);
    }

    [Fact]
    public async Task RetryAfterDateInThePastMeansNoWait()
    {
        var (client, _) = Create(
            Status(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(Now.AddSeconds(-30))), Ok);

        await client.GetAsync("https://example.test/");

        Assert.Equal([TimeSpan.Zero], _delays);
    }

    [Fact]
    public async Task WithoutRetryAfterBacksOffExponentiallyWithJitter()
    {
        var (client, _) = Create(TooManyRequests, TooManyRequests, TooManyRequests, Ok);

        await client.GetAsync("https://example.test/");

        // Base 1 s doubled at each attempt; the random half is 0.5 here, so 75 % of each step.
        Assert.Equal([TimeSpan.FromSeconds(0.75), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3)], _delays);
    }

    [Fact]
    public async Task WaitsAreCappedAtTheMaximumDelay()
    {
        var (client, _) = Create(
            Status(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(TimeSpan.FromMinutes(10))), Ok);

        await client.GetAsync("https://example.test/");

        Assert.Equal([TimeSpan.FromSeconds(60)], _delays);
    }

    [Fact]
    public async Task ReturnsTheLast429WhenAttemptsAreExhausted()
    {
        var (client, server) = Create(new RetryOptions { MaxAttempts = 3 },
            TooManyRequests, TooManyRequests, TooManyRequests, Ok);

        var response = await client.GetAsync("https://example.test/");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(3, server.Calls);
        Assert.Equal(2, _delays.Count);
    }

    // --- Transient statuses, network errors and timeouts (issue #2) ---------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task UnavailableServicesAreRetriedWithBackoff(HttpStatusCode status)
    {
        var (client, server) = Create(Status(status), Status(status), Ok);

        var response = await client.GetAsync("https://example.test/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, server.Calls);
        Assert.Equal([TimeSpan.FromSeconds(0.75), TimeSpan.FromSeconds(1.5)], _delays);
        Assert.All(_notices, notice => Assert.Equal(status, notice.Status));
    }

    [Fact]
    public async Task RetryAfterOnA503IsHonoured()
    {
        var (client, _) = Create(
            Status(HttpStatusCode.ServiceUnavailable, new RetryConditionHeaderValue(TimeSpan.FromSeconds(12))), Ok);

        await client.GetAsync("https://example.test/");

        Assert.Equal([TimeSpan.FromSeconds(12)], _delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherStatusesAreNotRetried(HttpStatusCode status)
    {
        var (client, server) = Create(Status(status), Ok);

        var response = await client.GetAsync("https://example.test/");

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, server.Calls);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task NetworkErrorsAreRetried()
    {
        var (client, server) = Create(NetworkError, Ok);

        var response = await client.GetAsync("https://example.test/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.Calls);
        var notice = Assert.Single(_notices);
        Assert.Null(notice.Status);
        Assert.IsType<HttpRequestException>(notice.Error);
    }

    [Fact]
    public async Task TheLastNetworkErrorIsRethrownWhenAttemptsAreExhausted()
    {
        var (client, server) = Create(new RetryOptions { MaxAttempts = 3 }, NetworkError, NetworkError, NetworkError);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.test/"));
        Assert.Equal(3, server.Calls);
        Assert.Equal(2, _delays.Count);
    }

    [Fact]
    public async Task AnAttemptTimeoutIsRetried()
    {
        var (client, server) = Create(new RetryOptions { AttemptTimeout = TimeSpan.FromMilliseconds(50) }, Hang, Ok);

        var response = await client.GetAsync("https://example.test/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.Calls);
        Assert.IsType<TaskCanceledException>(Assert.Single(_notices).Error);
    }

    [Fact]
    public async Task TheLastTimeoutFailsAsATimeoutWhenAttemptsAreExhausted()
    {
        var (client, server) = Create(
            new RetryOptions { MaxAttempts = 2, AttemptTimeout = TimeSpan.FromMilliseconds(50) }, Hang, Hang);

        var exception = await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.GetAsync("https://example.test/"));
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task ABodyThatNeverCompletesTimesOutAndIsRetried()
    {
        var (client, server) = Create(new RetryOptions { AttemptTimeout = TimeSpan.FromMilliseconds(50) },
            HeadersThenHangingBody, Body("done"));

        var response = await client.GetAsync("https://example.test/", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal("done", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, server.Calls);
        Assert.IsType<TaskCanceledException>(Assert.Single(_notices).Error);
    }

    [Fact]
    public async Task ABodyThatNeverCompletesFailsAsATimeoutWhenAttemptsAreExhausted()
    {
        var (client, server) = Create(
            new RetryOptions { MaxAttempts = 2, AttemptTimeout = TimeSpan.FromMilliseconds(50) },
            HeadersThenHangingBody, HeadersThenHangingBody);

        var exception = await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.GetAsync("https://example.test/", HttpCompletionOption.ResponseHeadersRead));
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task AnInterruptedBodyIsRetried()
    {
        var (client, server) = Create(HeadersThenBrokenBody, Body("done"));

        var response = await client.GetAsync("https://example.test/", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal("done", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, server.Calls);
        Assert.Null(Assert.Single(_notices).Status);
    }

    [Fact]
    public async Task CancellationByTheCallerIsNeverRetried()
    {
        using var cancellation = new CancellationTokenSource();
        var (client, server) = Create(_ =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException("cancelled", new TimeoutException());
        }, Ok);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("https://example.test/", cancellation.Token));
        Assert.Equal(1, server.Calls);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task CancellationDuringTheWaitStopsTheRetries()
    {
        using var cancellation = new CancellationTokenSource();
        var server = new ScriptedHandler([TooManyRequests, Ok]);
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

    public delegate Task<HttpResponseMessage> Step(CancellationToken ct);

    private sealed class ScriptedHandler(IEnumerable<Step> steps) : HttpMessageHandler
    {
        private readonly Queue<Step> _steps = new(steps);

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return _steps.Dequeue()(cancellationToken);
        }
    }

    /// <summary>A body whose transfer never ends, or breaks after the headers were received.</summary>
    private sealed class FaultyContent(bool hang) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync("partial"u8.ToArray(), cancellationToken);
            if (hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new IOException("connection reset during the body");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
