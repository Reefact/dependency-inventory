using System.Net;

namespace DependencyInventory;

public sealed record RetryOptions
{
    /// <summary>Total number of attempts, the first one included.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Upper bound of a single wait, whatever Retry-After asks for.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>First backoff step when the server gives no Retry-After; doubled at each attempt.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Timeout of one attempt, response body included; waits between attempts are not counted.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(120);
}

/// <summary>
/// A retry about to happen: what failed (an HTTP status, or a network error or timeout when
/// <see cref="Status"/> is null), the attempt that failed and the wait before the next one.
/// </summary>
public sealed record RetryNotice(HttpStatusCode? Status, Exception? Error, int Attempt, int MaxAttempts, TimeSpan Delay);

/// <summary>
/// Retries transient failures: rate limiting (HTTP 429), unavailable gateways or services (HTTP 502, 503,
/// 504), network errors and per-attempt timeouts. It waits as long as Retry-After asks, or with an
/// exponential backoff and jitter when it is absent. When the attempts are exhausted, the last response
/// is returned, or the last error rethrown, so the caller reports it as before. Other statuses (400, 401,
/// 403, 404...) and cancellation by the caller are never retried.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private static readonly HashSet<HttpStatusCode> TransientStatuses =
    [
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
    ];

    private readonly RetryOptions _options;
    private readonly Action<RetryNotice>? _onRetry;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _random;

    public RetryHandler(RetryOptions options, Action<RetryNotice>? onRetry = null, TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? random = null)
    {
        _options = options;
        _onRetry = onRetry;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? Task.Delay;
        _random = random ?? Random.Shared.NextDouble;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The requests are bodiless GETs, so the same message can be sent again.
        for (var attempt = 1; ; attempt++)
        {
            var lastAttempt = attempt >= _options.MaxAttempts;
            HttpResponseMessage response;
            try
            {
                response = await SendAttemptAsync(request, cancellationToken);
            }
            catch (Exception ex) when (!lastAttempt && IsTransient(ex, cancellationToken))
            {
                await WaitAsync(null, ex, attempt, Backoff(attempt), cancellationToken);
                continue;
            }

            if (lastAttempt || !TransientStatuses.Contains(response.StatusCode))
                return response;

            var status = response.StatusCode;
            var delay = DelayBeforeRetry(response, attempt);
            response.Dispose();
            await WaitAsync(status, null, attempt, delay, cancellationToken);
        }
    }

    private Task WaitAsync(HttpStatusCode? status, Exception? error, int attempt, TimeSpan delay, CancellationToken ct)
    {
        _onRetry?.Invoke(new RetryNotice(status, error, attempt, _options.MaxAttempts, delay));
        return _delay(delay, ct);
    }

    /// <summary>A network error, or an attempt timeout; never a cancellation asked by the caller.</summary>
    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        (ex is HttpRequestException || ex is TaskCanceledException { InnerException: TimeoutException });

    private async Task<HttpResponseMessage> SendAttemptAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.AttemptTimeout);
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, timeout.Token);
            // The clients read with ResponseHeadersRead: buffering the body here puts its transfer under
            // the attempt timeout, and an interrupted transfer is retried like any network error.
            await response.Content.LoadIntoBufferAsync(timeout.Token);
            return response;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            response?.Dispose();
            // Same exception type as an HttpClient.Timeout expiry, which this per-attempt timeout replaces.
            throw new TaskCanceledException(
                $"The request exceeded the {_options.AttemptTimeout.TotalSeconds:0} s timeout.",
                new TimeoutException(ex.Message, ex));
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    private TimeSpan DelayBeforeRetry(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return Capped(delta);
        if (retryAfter?.Date is { } date)
            return Capped(date - _time.GetUtcNow());
        return Backoff(attempt);
    }

    /// <summary>Exponential backoff with jitter: half the step is fixed, the other half random.</summary>
    private TimeSpan Backoff(int attempt)
    {
        var step = _options.BaseDelay * Math.Pow(2, attempt - 1);
        return Capped(step / 2 + step / 2 * _random());
    }

    private TimeSpan Capped(TimeSpan delay) =>
        delay < TimeSpan.Zero ? TimeSpan.Zero : delay > _options.MaxDelay ? _options.MaxDelay : delay;
}

/// <summary>The HTTP pipeline shared by the Azure DevOps and GitLab clients.</summary>
public static class RepositoryHttpClient
{
    public static HttpClient Create(Action<RetryNotice>? onRetry)
    {
        var handler = new RetryHandler(new RetryOptions(), onRetry)
        {
            InnerHandler = new HttpClientHandler { AllowAutoRedirect = false }
        };
        // The timeout is applied per attempt by RetryHandler, so waits between attempts do not consume it.
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DependencyInventory/1.0");
        return http;
    }
}
