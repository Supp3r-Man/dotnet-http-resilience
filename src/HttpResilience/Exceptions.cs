namespace HttpResilience;

/// <summary>Thrown by <see cref="CircuitBreakerHandler"/> when the circuit is open (fast-fail, no request is sent).</summary>
public sealed class BrokenCircuitException : Exception
{
    /// <summary>How long until the breaker will start admitting probes (zero when half-open probes are exhausted).</summary>
    public TimeSpan RetryAfter { get; }

    public BrokenCircuitException(string message, TimeSpan retryAfter) : base(message)
    {
        RetryAfter = retryAfter;
    }
}

/// <summary>Which limit a <see cref="TimeoutRejectedException"/> refers to.</summary>
public enum TimeoutKind
{
    /// <summary>The per-attempt timeout elapsed. Retryable.</summary>
    Attempt,

    /// <summary>The overall deadline elapsed. Not retryable: there is no time left.</summary>
    Overall,
}

/// <summary>
/// Thrown by <see cref="TimeoutHandler"/> when a timeout (not the caller) cancelled the request.
/// Caller cancellation is never converted into this exception; it surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class TimeoutRejectedException : TimeoutException
{
    public TimeoutKind Kind { get; }

    public TimeSpan Timeout { get; }

    public TimeoutRejectedException(TimeoutKind kind, TimeSpan timeout, Exception? inner = null)
        : base($"The {kind.ToString().ToLowerInvariant()} timeout of {timeout.TotalMilliseconds:0.#} ms elapsed.", inner)
    {
        Kind = kind;
        Timeout = timeout;
    }
}

/// <summary>Thrown by <see cref="BulkheadHandler"/> when both the concurrency slots and the wait queue are full.</summary>
public sealed class BulkheadRejectedException : Exception
{
    public int MaxConcurrency { get; }

    public int MaxQueue { get; }

    public BulkheadRejectedException(int maxConcurrency, int maxQueue)
        : base($"Bulkhead full: {maxConcurrency} in flight and {maxQueue} queued.")
    {
        MaxConcurrency = maxConcurrency;
        MaxQueue = maxQueue;
    }
}
