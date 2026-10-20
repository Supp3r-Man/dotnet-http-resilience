namespace HttpResilience;

public sealed class RetryBudgetOptions
{
    /// <summary>Retries allowed per request seen in the window (0.2 = retries may be 20 percent of requests).</summary>
    public double RetryRatio { get; set; } = 0.2;

    /// <summary>Extra retries always allowed per window so that a quiet client can still retry.</summary>
    public int MinRetriesPerWindow { get; set; } = 3;

    /// <summary>Length of the sliding window.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (RetryRatio < 0 || double.IsNaN(RetryRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(RetryRatio));
        }

        if (MinRetriesPerWindow < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinRetriesPerWindow));
        }

        if (Window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Window));
        }
    }
}

/// <summary>
/// A retry budget: in any sliding window, retries may be at most
/// <c>MinRetriesPerWindow + floor(RetryRatio * requests)</c>. First attempts are never limited by the budget;
/// only retries are. Window events are kept as timestamps, so memory is proportional to traffic in one window.
/// </summary>
public sealed class RetryBudget
{
    private readonly object _gate = new();
    private readonly Queue<long> _requests = new();
    private readonly Queue<long> _retries = new();
    private readonly RetryBudgetOptions _options;
    private readonly TimeProvider _time;

    public RetryBudget(RetryBudgetOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new RetryBudgetOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    public int RequestsInWindow
    {
        get
        {
            lock (_gate)
            {
                Evict(_time.GetTimestamp());
                return _requests.Count;
            }
        }
    }

    public int RetriesInWindow
    {
        get
        {
            lock (_gate)
            {
                Evict(_time.GetTimestamp());
                return _retries.Count;
            }
        }
    }

    /// <summary>Record one logical (first-attempt) request.</summary>
    public void RecordRequest()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            Evict(now);
            _requests.Enqueue(now);
        }
    }

    /// <summary>Take one retry from the budget. Returns false (and takes nothing) when the budget is spent.</summary>
    public bool TryAcquireRetry()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            Evict(now);
            var allowed = _options.MinRetriesPerWindow + (long)Math.Floor((_options.RetryRatio * _requests.Count) + 1e-9);
            if (_retries.Count + 1 > allowed)
            {
                return false;
            }

            _retries.Enqueue(now);
            return true;
        }
    }

    private void Evict(long now)
    {
        while (_requests.Count > 0 && _time.GetElapsedTime(_requests.Peek(), now) >= _options.Window)
        {
            _requests.Dequeue();
        }

        while (_retries.Count > 0 && _time.GetElapsedTime(_retries.Peek(), now) >= _options.Window)
        {
            _retries.Dequeue();
        }
    }
}
