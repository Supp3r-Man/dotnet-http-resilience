namespace HttpResilience;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

/// <summary>Reported to <see cref="CircuitBreakerOptions.OnStateChange"/>. Generation is the new generation number.</summary>
public readonly record struct CircuitStateChange(CircuitState From, CircuitState To, long Generation);

public sealed class CircuitBreakerOptions
{
    /// <summary>Number of most recent outcomes kept (count-based sliding window) while closed.</summary>
    public int WindowSize { get; set; } = 20;

    /// <summary>Open when failures / calls in the window is at least this (0 &lt; x &lt;= 1).</summary>
    public double FailureRateThreshold { get; set; } = 0.5;

    /// <summary>The breaker never opens until the window holds at least this many outcomes.</summary>
    public int MinimumCalls { get; set; } = 10;

    /// <summary>How long the breaker stays open before admitting probes.</summary>
    public TimeSpan OpenDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Probe calls admitted while half-open. All must succeed to close; any failure re-opens.</summary>
    public int HalfOpenPermits { get; set; } = 3;

    /// <summary>Called after every state change, outside the breaker's lock. Exceptions thrown by the callback are swallowed.</summary>
    public Action<CircuitStateChange>? OnStateChange { get; set; }

    internal void Validate()
    {
        if (WindowSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(WindowSize));
        }

        if (!(FailureRateThreshold > 0 && FailureRateThreshold <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(FailureRateThreshold));
        }

        if (MinimumCalls < 1 || MinimumCalls > WindowSize)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumCalls), "Must be between 1 and WindowSize.");
        }

        if (OpenDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(OpenDuration));
        }

        if (HalfOpenPermits < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(HalfOpenPermits));
        }
    }
}

/// <summary>Proof that a call was admitted. Outcomes are only counted if the generation still matches.</summary>
public readonly struct CircuitPermit
{
    internal CircuitPermit(long generation, bool isProbe)
    {
        Generation = generation;
        IsProbe = isProbe;
    }

    public long Generation { get; }

    public bool IsProbe { get; }
}

/// <summary>
/// The circuit breaker state machine, independent of HTTP. Closed (count-based window) to Open (timed) to
/// HalfOpen (limited probes) and back. Every transition bumps a generation; outcomes reported with an older
/// generation are ignored so a slow request that started before the breaker tripped cannot corrupt the
/// current state. One instance should be shared by all handlers that talk to the same dependency.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly object _gate = new();
    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _time;
    private readonly bool[] _ring;
    private CircuitState _state = CircuitState.Closed;
    private long _generation;
    private int _filled;
    private int _next;
    private int _failures;
    private long _openedAt;
    private int _probesIssued;
    private int _probesSucceeded;

    public CircuitBreaker(CircuitBreakerOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new CircuitBreakerOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
        _ring = new bool[_options.WindowSize];
    }

    /// <summary>The stored state. An open breaker reports Open until the next call arrives after OpenDuration.</summary>
    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public int CallsInWindow
    {
        get
        {
            lock (_gate)
            {
                return _filled;
            }
        }
    }

    public int FailuresInWindow
    {
        get
        {
            lock (_gate)
            {
                return _failures;
            }
        }
    }

    public int ProbesIssued
    {
        get
        {
            lock (_gate)
            {
                return _probesIssued;
            }
        }
    }

    /// <summary>Ask to make a call. When false, <paramref name="retryAfter"/> says how long until probes are admitted.</summary>
    public bool TryAcquire(out CircuitPermit permit, out TimeSpan retryAfter)
    {
        CircuitStateChange? change = null;
        var admitted = false;
        permit = default;
        retryAfter = TimeSpan.Zero;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_state == CircuitState.Open)
            {
                var elapsed = _time.GetElapsedTime(_openedAt, now);
                if (elapsed >= _options.OpenDuration)
                {
                    change = Transition(CircuitState.HalfOpen, now);
                }
                else
                {
                    retryAfter = _options.OpenDuration - elapsed;
                }
            }

            if (_state == CircuitState.Closed)
            {
                permit = new CircuitPermit(_generation, isProbe: false);
                admitted = true;
            }
            else if (_state == CircuitState.HalfOpen && _probesIssued < _options.HalfOpenPermits)
            {
                _probesIssued++;
                permit = new CircuitPermit(_generation, isProbe: true);
                admitted = true;
            }
        }

        Notify(change);
        return admitted;
    }

    public void RecordSuccess(CircuitPermit permit) => Record(permit, success: true);

    public void RecordFailure(CircuitPermit permit) => Record(permit, success: false);

    /// <summary>Give a permit back without recording an outcome (for example the caller cancelled).</summary>
    public void Release(CircuitPermit permit)
    {
        lock (_gate)
        {
            if (permit.Generation != _generation)
            {
                return;
            }

            if (_state == CircuitState.HalfOpen && permit.IsProbe && _probesIssued > 0)
            {
                _probesIssued--;
            }
        }
    }

    private void Record(CircuitPermit permit, bool success)
    {
        CircuitStateChange? change = null;
        lock (_gate)
        {
            if (permit.Generation != _generation)
            {
                return;
            }

            var now = _time.GetTimestamp();
            switch (_state)
            {
                case CircuitState.Closed:
                    AddToWindow(success);
                    if (_filled >= _options.MinimumCalls
                        && (double)_failures / _filled >= _options.FailureRateThreshold)
                    {
                        change = Transition(CircuitState.Open, now);
                    }

                    break;

                case CircuitState.HalfOpen:
                    if (!success)
                    {
                        change = Transition(CircuitState.Open, now);
                    }
                    else
                    {
                        _probesSucceeded++;
                        if (_probesSucceeded >= _options.HalfOpenPermits)
                        {
                            change = Transition(CircuitState.Closed, now);
                        }
                    }

                    break;
            }
        }

        Notify(change);
    }

    private void AddToWindow(bool success)
    {
        if (_filled == _ring.Length)
        {
            if (_ring[_next])
            {
                _failures--;
            }
        }
        else
        {
            _filled++;
        }

        _ring[_next] = !success;
        if (!success)
        {
            _failures++;
        }

        _next = (_next + 1) % _ring.Length;
    }

    private CircuitStateChange Transition(CircuitState to, long now)
    {
        var from = _state;
        _state = to;
        _generation++;
        Array.Clear(_ring);
        _filled = 0;
        _next = 0;
        _failures = 0;
        _probesIssued = 0;
        _probesSucceeded = 0;
        if (to == CircuitState.Open)
        {
            _openedAt = now;
        }

        return new CircuitStateChange(from, to, _generation);
    }

    private void Notify(CircuitStateChange? change)
    {
        if (change is { } c && _options.OnStateChange is { } callback)
        {
            try
            {
                callback(c);
            }
            catch (Exception)
            {
                // A broken observer must not break requests.
            }
        }
    }
}
