using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace RuleHealth.Core.Auth;

/// <summary>
/// HttpClient pipeline step for one API: adds the bearer token and retries throttled calls (429/503),
/// honouring Retry-After. On a 401 it gets a new access token once and retries; a second 401 is
/// returned to the caller (which names the missing permission). One instance per HttpClient, so one
/// API per instance.
/// </summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    private readonly Func<bool, CancellationToken, Task<string>> _getToken;
    private readonly Action<string>? _log;

    /// <param name="getToken">(forceRefresh, cancellation) → access token; usually <see cref="TenantSession.GetAccessTokenAsync"/>.</param>
    /// <param name="log">Receives one line per HTTP call (method, URL, status, duration). Never the token.</param>
    public BearerTokenHandler(Func<bool, CancellationToken, Task<string>> getToken, Action<string>? log)
        : base(new HttpClientHandler())
    {
        _getToken = getToken;
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var forceRefresh = false;
        var refreshed = false;
        for (var attempt = 1; ; attempt++)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _getToken(forceRefresh, cancellationToken));
            forceRefresh = false;

            var watch = Stopwatch.StartNew();
            var response = await base.SendAsync(request, cancellationToken);
            _log?.Invoke($"{request.Method} {request.RequestUri} -> {(int)response.StatusCode} ({watch.ElapsedMilliseconds} ms)");

            // An access token can be revoked or invalidated before it expires (CAE): try one new one.
            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                refreshed = forceRefresh = true;
                _log?.Invoke("  401, getting a new access token and retrying once");
                response.Dispose();
                continue;
            }

            if (!IsRetryable(response.StatusCode) || attempt >= MaxAttempts)
                return response;

            var delay = RetryDelay(response, attempt);
            _log?.Invoke($"  throttled, retry {attempt}/{MaxAttempts - 1} in {delay.TotalSeconds:0.#} s");
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? delay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is null || delay <= TimeSpan.Zero)
        {
            // Exponential backoff with jitter: ~2, 4, 8, 16 s.
            var seconds = Math.Pow(2, attempt) + Random.Shared.NextDouble();
            delay = TimeSpan.FromSeconds(seconds);
        }
        return delay.Value > MaxDelay ? MaxDelay : delay.Value;
    }
}
