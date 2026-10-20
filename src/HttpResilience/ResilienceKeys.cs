namespace HttpResilience;

/// <summary>Header names and request-option keys shared by the handlers.</summary>
public static class ResilienceKeys
{
    /// <summary>Header carrying the remaining overall time budget, in whole milliseconds (rounded up).</summary>
    public const string DeadlineHeaderName = "X-Request-Deadline-Ms";

    /// <summary>Header that makes a POST safe to retry.</summary>
    public const string IdempotencyKeyHeaderName = "Idempotency-Key";

    internal static readonly HttpRequestOptionsKey<RequestDeadline> Deadline = new("HttpResilience.Deadline");
}

/// <summary>An absolute deadline expressed as a <see cref="TimeProvider.GetTimestamp"/> value.</summary>
internal sealed class RequestDeadline
{
    public RequestDeadline(long timestamp)
    {
        Timestamp = timestamp;
    }

    public long Timestamp { get; }

    public TimeSpan Remaining(TimeProvider time) => time.GetElapsedTime(time.GetTimestamp(), Timestamp);
}
