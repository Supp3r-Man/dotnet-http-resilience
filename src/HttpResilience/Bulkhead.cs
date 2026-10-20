namespace HttpResilience;

public sealed class BulkheadOptions
{
    /// <summary>Calls allowed to run at once.</summary>
    public int MaxConcurrency { get; set; } = 10;

    /// <summary>Calls allowed to wait for a slot. Zero means reject immediately when all slots are taken.</summary>
    public int MaxQueue { get; set; } = 20;

    internal void Validate()
    {
        if (MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency));
        }

        if (MaxQueue < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxQueue));
        }
    }
}

/// <summary>A held bulkhead slot. Dispose releases it; disposing twice is harmless.</summary>
public sealed class BulkheadLease : IDisposable
{
    private readonly Bulkhead _owner;
    private int _released;

    internal BulkheadLease(Bulkhead owner)
    {
        _owner = owner;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _owner.Release();
        }
    }
}

/// <summary>
/// An async concurrency limiter with a bounded FIFO wait queue. A released slot is handed directly to the
/// head of the queue (no barging), so waiters are served strictly in arrival order. When the queue is full,
/// callers are rejected with <see cref="BulkheadRejectedException"/> instead of piling up.
/// </summary>
public sealed class Bulkhead
{
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _queue = new();
    private readonly int _maxConcurrency;
    private readonly int _maxQueue;
    private int _inFlight;
    private int _maxObserved;

    public Bulkhead(BulkheadOptions? options = null)
    {
        options ??= new BulkheadOptions();
        options.Validate();
        _maxConcurrency = options.MaxConcurrency;
        _maxQueue = options.MaxQueue;
    }

    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    public int QueueLength
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>The highest number of slots ever held at once. Never exceeds MaxConcurrency.</summary>
    public int MaxObservedInFlight
    {
        get
        {
            lock (_gate)
            {
                return _maxObserved;
            }
        }
    }

    public ValueTask<BulkheadLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_gate)
        {
            if (_inFlight < _maxConcurrency && _queue.Count == 0)
            {
                _inFlight++;
                if (_inFlight > _maxObserved)
                {
                    _maxObserved = _inFlight;
                }

                return new ValueTask<BulkheadLease>(new BulkheadLease(this));
            }

            if (_queue.Count >= _maxQueue)
            {
                throw new BulkheadRejectedException(_maxConcurrency, _maxQueue);
            }

            waiter = new Waiter();
            waiter.Node = _queue.AddLast(waiter);
        }

        return new ValueTask<BulkheadLease>(WaitAsync(waiter, cancellationToken));
    }

    internal void Release()
    {
        Waiter? next = null;
        lock (_gate)
        {
            if (_queue.First is { } head)
            {
                next = head.Value;
                _queue.RemoveFirst();
                next.Node = null;
                next.Granted = true;
            }
            else
            {
                _inFlight--;
            }
        }

        next?.Completion.TrySetResult();
    }

    private async Task<BulkheadLease> WaitAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => Cancel(waiter, cancellationToken));
        await waiter.Completion.Task.ConfigureAwait(false);
        return new BulkheadLease(this);
    }

    private void Cancel(Waiter waiter, CancellationToken token)
    {
        var removed = false;
        lock (_gate)
        {
            if (!waiter.Granted && waiter.Node is { } node)
            {
                _queue.Remove(node);
                waiter.Node = null;
                removed = true;
            }
        }

        if (removed)
        {
            waiter.Completion.TrySetCanceled(token);
        }
    }

    private sealed class Waiter
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter>? Node { get; set; }

        public bool Granted { get; set; }
    }
}
