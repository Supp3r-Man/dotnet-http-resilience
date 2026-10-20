namespace HttpResilience;

public sealed class TimeoutOptions
{
    /// <summary>Limit for one attempt (time until response headers). Null means no per-attempt limit.</summary>
    public TimeSpan? AttemptTimeout { get; set; }

    /// <summary>
    /// Deadline for the whole logical request. Established the first time a request passes through a handler
    /// that has this set; a request keeps the earliest deadline it has been given. Null means none.
    /// </summary>
    public TimeSpan? OverallTimeout { get; set; }

    /// <summary>Whether to stamp the remaining time into <see cref="HeaderName"/> on every pass.</summary>
    public bool PropagateDeadline { get; set; } = true;

    public string HeaderName { get; set; } = ResilienceKeys.DeadlineHeaderName;

    internal void Validate()
    {
        if (AttemptTimeout is { } a && a <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AttemptTimeout));
        }

        if (OverallTimeout is { } o && o <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(OverallTimeout));
        }
    }
}

/// <summary>
/// Enforces a per-attempt timeout and/or an overall deadline using <see cref="TimeProvider"/>.
/// The overall deadline is stored on the request (so retries share it) and the remaining time is written to the
/// X-Request-Deadline-Ms header for the downstream service. A timeout surfaces as <see cref="TimeoutRejectedException"/>;
/// caller cancellation surfaces as <see cref="OperationCanceledException"/> and wins if both happen.
/// Timeouts cover the time until the handler chain returns (response headers), not the body download.
/// </summary>
public sealed class TimeoutHandler : DelegatingHandler
{
    private readonly TimeoutOptions _options;
    private readonly TimeProvider _time;

    public TimeoutHandler(TimeoutOptions options, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var now = _time.GetTimestamp();
        request.Options.TryGetValue(ResilienceKeys.Deadline, out var deadline);
        if (_options.OverallTimeout is { } overall)
        {
            var candidate = now + (long)(overall.TotalSeconds * _time.TimestampFrequency);
            if (deadline is null || candidate < deadline.Timestamp)
            {
                deadline = new RequestDeadline(candidate);
                request.Options.Set(ResilienceKeys.Deadline, deadline);
            }
        }

        TimeSpan? remaining = deadline is null ? null : _time.GetElapsedTime(now, deadline.Timestamp);
        if (remaining is { } left && left <= TimeSpan.Zero)
        {
            throw new TimeoutRejectedException(TimeoutKind.Overall, _options.OverallTimeout ?? TimeSpan.Zero);
        }

        TimeSpan? effective = null;
        var kind = TimeoutKind.Attempt;
        if (_options.AttemptTimeout is { } attempt)
        {
            effective = attempt;
        }

        if (remaining is { } rem && (effective is null || rem < effective.Value))
        {
            effective = rem;
            kind = TimeoutKind.Overall;
        }

        if (remaining is { } toSend && _options.PropagateDeadline)
        {
            request.Headers.Remove(_options.HeaderName);
            var ms = (long)Math.Ceiling(toSend.TotalMilliseconds);
            request.Headers.TryAddWithoutValidation(_options.HeaderName, ms.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (effective is null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        using var timeoutCts = new CancellationTokenSource(effective.Value, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await base.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutRejectedException(kind, effective.Value, ex);
        }
    }
}
