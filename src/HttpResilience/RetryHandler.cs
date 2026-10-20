using System.Net;
using System.Runtime.ExceptionServices;

namespace HttpResilience;

/// <summary>Reported to <see cref="RetryOptions.OnRetry"/> just before a retry delay starts.</summary>
public readonly record struct RetryEvent(int RetryNumber, TimeSpan Delay, string Reason);

public sealed class RetryOptions
{
    /// <summary>Maximum number of retries after the first attempt.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Backoff ceiling for the first retry; doubles on every further retry.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Upper limit of the backoff ceiling.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether a Retry-After header may lengthen the delay.</summary>
    public bool HonourRetryAfter { get; set; } = true;

    /// <summary>Cap applied to whatever Retry-After asks for.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional shared retry budget. Null means no budget (only MaxRetries limits retries).</summary>
    public RetryBudget? Budget { get; set; }

    /// <summary>Source of uniform randoms in [0,1) used for full jitter. Null means <see cref="Random.Shared"/>.</summary>
    public Func<double>? RandomSource { get; set; }

    /// <summary>Request bodies up to this size are buffered so they can be re-sent. Larger or unknown-length bodies are sent once, without retries.</summary>
    public long MaxBufferedContentBytes { get; set; } = 1024 * 1024;

    /// <summary>Optional callback, invoked before each retry delay.</summary>
    public Action<RetryEvent>? OnRetry { get; set; }

    internal void Validate()
    {
        if (MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetries));
        }

        if (BaseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BaseDelay));
        }

        if (MaxDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDelay));
        }

        if (MaxRetryAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryAfter));
        }

        if (MaxBufferedContentBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBufferedContentBytes));
        }
    }
}

/// <summary>
/// Retries safe requests on transient failures. See the README for the full rules. In short:
/// idempotent methods (or POST with an Idempotency-Key), 408/429/5xx and transient exceptions, never caller
/// cancellation, full-jitter exponential backoff, Retry-After honoured and capped, an optional retry budget,
/// and no retry when the remaining overall deadline is shorter than the next delay.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private readonly RetryOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<double> _random;
    private readonly object _randomGate = new();

    public RetryHandler(RetryOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new RetryOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
        _random = _options.RandomSource ?? Random.Shared.NextDouble;
    }

    /// <summary>The backoff ceiling before jitter for the given zero-based retry index: min(MaxDelay, BaseDelay * 2^index).</summary>
    public static TimeSpan BackoffCeiling(RetryOptions options, int retryIndex)
    {
        ArgumentNullException.ThrowIfNull(options);
        var ms = options.BaseDelay.TotalMilliseconds * Math.Pow(2, retryIndex);
        var capped = Math.Min(ms, options.MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped);
    }

    /// <summary>True for GET, HEAD, PUT, DELETE, OPTIONS, and for POST that carries a non-empty Idempotency-Key.</summary>
    public static bool IsRetryableRequest(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var method = request.Method;
        if (method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Put
            || method == HttpMethod.Delete || method == HttpMethod.Options)
        {
            return true;
        }

        if (method == HttpMethod.Post
            && request.Headers.TryGetValues(ResilienceKeys.IdempotencyKeyHeaderName, out var keys))
        {
            return keys.Any(k => !string.IsNullOrWhiteSpace(k));
        }

        return false;
    }

    public static bool IsRetryableStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return code == 408 || code == 429 || code >= 500;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _options.Budget?.RecordRequest();

        var canRetry = IsRetryableRequest(request);
        byte[]? body = null;
        if (canRetry && request.Content is not null)
        {
            var length = request.Content.Headers.ContentLength;
            if (length is null || length > _options.MaxBufferedContentBytes)
            {
                canRetry = false;
            }
            else
            {
                body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        for (var retryIndex = 0; ; retryIndex++)
        {
            var attemptRequest = canRetry ? Clone(request, body) : request;
            HttpResponseMessage? response = null;
            Exception? error = null;
            try
            {
                response = await base.SendAsync(attemptRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                error = ex;
            }

            string? reason = null;
            if (error is not null)
            {
                reason = error.GetType().Name;
            }
            else if (IsRetryableStatus(response!.StatusCode))
            {
                reason = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (reason is null)
            {
                response!.RequestMessage = request;
                return response;
            }

            var delay = TimeSpan.Zero;
            var retry = false;
            if (canRetry && retryIndex < _options.MaxRetries)
            {
                delay = ComputeDelay(retryIndex, response);
                var remaining = RemainingDeadline(request);
                if (remaining is null || remaining.Value > delay)
                {
                    retry = _options.Budget is null || _options.Budget.TryAcquireRetry();
                }
            }

            if (!retry)
            {
                if (error is not null)
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                }

                response!.RequestMessage = request;
                return response;
            }

            response?.Dispose();
            if (!ReferenceEquals(attemptRequest, request))
            {
                attemptRequest.Dispose();
            }

            _options.OnRetry?.Invoke(new RetryEvent(retryIndex + 1, delay, reason));
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken callerToken) => ex switch
    {
        HttpRequestException => true,
        TimeoutRejectedException { Kind: TimeoutKind.Attempt } => true,
        OperationCanceledException => !callerToken.IsCancellationRequested,
        _ => false,
    };

    private TimeSpan ComputeDelay(int retryIndex, HttpResponseMessage? response)
    {
        var ceiling = BackoffCeiling(_options, retryIndex);
        double r;
        lock (_randomGate)
        {
            r = _random();
        }

        r = double.IsNaN(r) ? 0 : Math.Clamp(r, 0, 1);
        var backoff = TimeSpan.FromTicks((long)(ceiling.Ticks * r));
        if (_options.HonourRetryAfter && response is not null
            && RetryAfterParser.TryGetDelay(response, _time.GetUtcNow(), _options.MaxRetryAfter, out var retryAfter)
            && retryAfter > backoff)
        {
            return retryAfter;
        }

        return backoff;
    }

    private TimeSpan? RemainingDeadline(HttpRequestMessage request)
    {
        return request.Options.TryGetValue(ResilienceKeys.Deadline, out var deadline) ? deadline.Remaining(_time) : null;
    }

    private static HttpRequestMessage Clone(HttpRequestMessage original, byte[]? body)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in (IDictionary<string, object?>)original.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        if (body is not null)
        {
            var content = new ByteArrayContent(body);
            foreach (var header in original.Content!.Headers)
            {
                if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            clone.Content = content;
        }

        return clone;
    }
}
