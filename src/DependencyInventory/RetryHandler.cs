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

    /// <summary>Timeout of one attempt, up to the response headers; waits between attempts are not counted.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(120);
}

/// <summary>A retry about to happen: the status that caused it, the attempt that failed and the wait.</summary>
public sealed record RetryNotice(HttpStatusCode Status, int Attempt, int MaxAttempts, TimeSpan Delay);

/// <summary>
/// Retries rate-limited requests (HTTP 429), waiting as long as Retry-After asks, or with an exponential
/// backoff and jitter when it is absent. When the attempts are exhausted, the last response is returned
/// so the caller reports it as before.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
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
            var response = await SendAttemptAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= _options.MaxAttempts)
                return response;

            var delay = DelayBeforeRetry(response, attempt);
            response.Dispose();
            _onRetry?.Invoke(new RetryNotice(HttpStatusCode.TooManyRequests, attempt, _options.MaxAttempts, delay));
            await _delay(delay, cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendAttemptAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.AttemptTimeout);
        try
        {
            return await base.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Same exception type as an HttpClient.Timeout expiry, which this per-attempt timeout replaces.
            throw new TaskCanceledException(
                $"La requête a dépassé le délai de {_options.AttemptTimeout.TotalSeconds:0} s.",
                new TimeoutException(ex.Message, ex));
        }
    }

    private TimeSpan DelayBeforeRetry(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan delay;
        if (retryAfter?.Delta is { } delta)
            delay = delta;
        else if (retryAfter?.Date is { } date)
            delay = date - _time.GetUtcNow();
        else
        {
            // Exponential backoff with jitter: half the step is fixed, the other half random.
            var step = _options.BaseDelay * Math.Pow(2, attempt - 1);
            delay = step / 2 + step / 2 * _random();
        }

        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        return delay > _options.MaxDelay ? _options.MaxDelay : delay;
    }
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
