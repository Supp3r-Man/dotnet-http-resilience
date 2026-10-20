using System.Net;

namespace HttpResilience.Tests;

public class BulkheadTests
{
    private static Bulkhead Make(int max, int queue) => new(new BulkheadOptions { MaxConcurrency = max, MaxQueue = queue });

    [Fact]
    public void Slots_up_to_the_limit_are_granted_immediately()
    {
        var bulkhead = Make(3, 2);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(bulkhead.AcquireAsync().IsCompletedSuccessfully);
        }

        Assert.Equal(3, bulkhead.InFlight);
        Assert.Equal(0, bulkhead.QueueLength);
    }

    [Fact]
    public void The_next_caller_waits_in_the_queue()
    {
        var bulkhead = Make(1, 2);
        bulkhead.AcquireAsync();
        var waiting = bulkhead.AcquireAsync();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, bulkhead.QueueLength);
    }

    [Fact]
    public void A_full_queue_rejects_with_a_typed_exception()
    {
        var bulkhead = Make(1, 1);
        bulkhead.AcquireAsync();
        bulkhead.AcquireAsync();
        var ex = Assert.Throws<BulkheadRejectedException>(() => bulkhead.AcquireAsync());
        Assert.Equal(1, ex.MaxConcurrency);
        Assert.Equal(1, ex.MaxQueue);
        Assert.Equal(1, bulkhead.QueueLength);
        Assert.Equal(1, bulkhead.InFlight);
    }

    [Fact]
    public void A_queue_of_zero_rejects_as_soon_as_the_slots_are_taken()
    {
        var bulkhead = Make(2, 0);
        bulkhead.AcquireAsync();
        bulkhead.AcquireAsync();
        Assert.Throws<BulkheadRejectedException>(() => bulkhead.AcquireAsync());
    }

    [Fact]
    public async Task Releasing_a_slot_hands_it_to_the_waiter()
    {
        var bulkhead = Make(1, 1);
        var first = await bulkhead.AcquireAsync();
        var waiting = bulkhead.AcquireAsync().AsTask();
        Assert.False(waiting.IsCompleted);
        first.Dispose();
        var second = await waiting;
        Assert.Equal(1, bulkhead.InFlight);
        Assert.Equal(0, bulkhead.QueueLength);
        second.Dispose();
        Assert.Equal(0, bulkhead.InFlight);
    }

    [Fact]
    public async Task Waiters_are_served_in_arrival_order()
    {
        var bulkhead = Make(1, 5);
        var holder = await bulkhead.AcquireAsync();
        var waiters = Enumerable.Range(0, 5).Select(_ => bulkhead.AcquireAsync().AsTask()).ToList();
        Assert.Equal(5, bulkhead.QueueLength);
        var current = holder;
        for (var i = 0; i < 5; i++)
        {
            current.Dispose();
            current = await waiters[i];
            for (var later = i + 1; later < 5; later++)
            {
                Assert.False(waiters[later].IsCompleted, $"waiter {later} overtook waiter {i}");
            }
        }

        current.Dispose();
        Assert.Equal(0, bulkhead.InFlight);
    }

    [Fact]
    public async Task A_new_arrival_cannot_barge_ahead_of_a_waiter()
    {
        var bulkhead = Make(1, 2);
        var holder = await bulkhead.AcquireAsync();
        var waiter = bulkhead.AcquireAsync().AsTask();
        holder.Dispose();
        Assert.Equal(1, bulkhead.InFlight);
        var newcomer = bulkhead.AcquireAsync().AsTask();
        Assert.False(newcomer.IsCompleted);
        Assert.Equal(1, bulkhead.QueueLength);
        (await waiter).Dispose();
        (await newcomer).Dispose();
        Assert.Equal(0, bulkhead.InFlight);
    }

    [Fact]
    public async Task A_cancelled_waiter_leaves_the_queue_without_leaking_a_slot()
    {
        var bulkhead = Make(1, 2);
        var holder = await bulkhead.AcquireAsync();
        using var cts = new CancellationTokenSource();
        var cancelled = bulkhead.AcquireAsync(cts.Token).AsTask();
        var survivor = bulkhead.AcquireAsync().AsTask();
        Assert.Equal(2, bulkhead.QueueLength);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, bulkhead.QueueLength);
        holder.Dispose();
        var lease = await survivor;
        Assert.Equal(1, bulkhead.InFlight);
        lease.Dispose();
        Assert.Equal(0, bulkhead.InFlight);
    }

    [Fact]
    public async Task Cancelling_the_head_of_the_queue_keeps_the_order_of_the_rest()
    {
        var bulkhead = Make(1, 3);
        var holder = await bulkhead.AcquireAsync();
        using var cts = new CancellationTokenSource();
        var head = bulkhead.AcquireAsync(cts.Token).AsTask();
        var second = bulkhead.AcquireAsync().AsTask();
        var third = bulkhead.AcquireAsync().AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => head);
        holder.Dispose();
        var lease = await second;
        Assert.False(third.IsCompleted);
        lease.Dispose();
        (await third).Dispose();
        Assert.Equal(0, bulkhead.InFlight);
    }

    [Fact]
    public async Task A_cancel_after_the_slot_was_granted_does_not_leak_it()
    {
        var bulkhead = Make(1, 1);
        var holder = await bulkhead.AcquireAsync();
        using var cts = new CancellationTokenSource();
        var waiting = bulkhead.AcquireAsync(cts.Token).AsTask();
        holder.Dispose();
        cts.Cancel();
        var lease = await waiting;
        Assert.Equal(1, bulkhead.InFlight);
        lease.Dispose();
        Assert.Equal(0, bulkhead.InFlight);
        Assert.Equal(0, bulkhead.QueueLength);
    }

    [Fact]
    public void An_already_cancelled_token_never_queues()
    {
        var bulkhead = Make(1, 1);
        bulkhead.AcquireAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => bulkhead.AcquireAsync(cts.Token));
        Assert.Equal(0, bulkhead.QueueLength);
    }

    [Fact]
    public async Task Disposing_a_lease_twice_releases_only_once()
    {
        var bulkhead = Make(2, 0);
        var a = await bulkhead.AcquireAsync();
        var b = await bulkhead.AcquireAsync();
        a.Dispose();
        a.Dispose();
        Assert.Equal(1, bulkhead.InFlight);
        Assert.True(bulkhead.AcquireAsync().IsCompletedSuccessfully);
        Assert.Throws<BulkheadRejectedException>(() => bulkhead.AcquireAsync());
        b.Dispose();
    }

    [Fact]
    public async Task The_observed_peak_never_exceeds_the_limit()
    {
        var bulkhead = Make(3, 10);
        var leases = new List<BulkheadLease>();
        for (var i = 0; i < 3; i++)
        {
            leases.Add(await bulkhead.AcquireAsync());
        }

        var waiting = Enumerable.Range(0, 5).Select(_ => bulkhead.AcquireAsync().AsTask()).ToList();
        foreach (var lease in leases)
        {
            lease.Dispose();
        }

        foreach (var task in waiting)
        {
            (await task).Dispose();
        }

        Assert.Equal(3, bulkhead.MaxObservedInFlight);
        Assert.Equal(0, bulkhead.InFlight);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 5)]
    [InlineData(1, -1)]
    public void Invalid_options_are_rejected(int max, int queue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(max, queue));
    }

    // ---------- handler ----------

    private static (Bulkhead bulkhead, ScriptedHandler inner, HttpClient client) HandlerRig(int max, int queue)
    {
        var bulkhead = Make(max, queue);
        var inner = new ScriptedHandler();
        var client = H.Client(H.Chain(inner, new BulkheadHandler(bulkhead)));
        return (bulkhead, inner, client);
    }

    [Fact]
    public async Task The_handler_releases_the_slot_after_a_response()
    {
        var (bulkhead, _, client) = HandlerRig(1, 0);
        await client.GetAsync("/a");
        await client.GetAsync("/b");
        Assert.Equal(0, bulkhead.InFlight);
    }

    [Fact]
    public async Task The_handler_releases_the_slot_when_the_inner_handler_throws()
    {
        var (bulkhead, inner, client) = HandlerRig(1, 0);
        inner.Throw(() => new HttpRequestException("boom"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/a"));
        Assert.Equal(0, bulkhead.InFlight);
    }

    [Fact]
    public async Task The_handler_releases_the_slot_on_cancellation()
    {
        var (bulkhead, inner, client) = HandlerRig(1, 0);
        inner.Hang();
        using var cts = new CancellationTokenSource();
        var task = client.GetAsync("/a", cts.Token);
        Assert.Equal(1, bulkhead.InFlight);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, bulkhead.InFlight);
    }

    [Fact]
    public async Task The_handler_rejects_without_calling_the_inner_handler()
    {
        var (bulkhead, inner, client) = HandlerRig(1, 0);
        inner.Hang();
        using var cts = new CancellationTokenSource();
        var first = client.GetAsync("/a", cts.Token);
        await Assert.ThrowsAsync<BulkheadRejectedException>(() => client.GetAsync("/b"));
        Assert.Equal(1, inner.CallCount);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(0, bulkhead.InFlight);
    }

    [Fact]
    public void A_null_bulkhead_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new BulkheadHandler(null!));
    }

    [Fact]
    public async Task Concurrent_callers_are_limited_queued_and_rejected_as_configured()
    {
        const int Max = 3;
        const int Queue = 2;
        var bulkhead = Make(Max, Queue);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new SemaphoreSlim(0);
        var inside = 0;
        var peak = 0;
        var inner = new FuncHandler(async (_, _) =>
        {
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref peak, now);
            entered.Release();
            await gate.Task;
            Interlocked.Decrement(ref inside);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = H.Client(H.Chain(inner, new BulkheadHandler(bulkhead)));

        var running = Enumerable.Range(0, Max).Select(i => Task.Run(() => client.GetAsync($"/run/{i}"))).ToList();
        for (var i = 0; i < Max; i++)
        {
            await entered.WaitAsync();
        }

        var queued = new List<Task<HttpResponseMessage>>();
        for (var i = 0; i < Queue; i++)
        {
            var index = i;
            queued.Add(Task.Run(() => client.GetAsync($"/queued/{index}")));
            await H.WaitUntil(() => bulkhead.QueueLength == index + 1);
        }

        var rejected = 0;
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            await Assert.ThrowsAsync<BulkheadRejectedException>(() => Task.Run(() => client.GetAsync($"/rejected/{index}")));
            rejected++;
        }

        gate.SetResult();
        var all = await Task.WhenAll(running.Concat(queued));
        Assert.All(all, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(4, rejected);
        Assert.Equal(Max, peak);
        Assert.Equal(Max + Queue, inner.Calls);
        Assert.Equal(0, bulkhead.InFlight);
        Assert.Equal(0, bulkhead.QueueLength);
    }

    [Fact]
    public async Task Queued_requests_reach_the_upstream_in_arrival_order()
    {
        const int Queue = 4;
        var bulkhead = Make(1, Queue);
        var gates = Enumerable.Range(0, Queue + 1).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var order = new List<string>();
        var step = 0;
        var inner = new FuncHandler(async (request, _) =>
        {
            var mine = Interlocked.Increment(ref step) - 1;
            lock (order)
            {
                order.Add(request.RequestUri!.AbsolutePath);
            }

            await gates[mine].Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = H.Client(H.Chain(inner, new BulkheadHandler(bulkhead)));

        var tasks = new List<Task<HttpResponseMessage>> { Task.Run(() => client.GetAsync("/0")) };
        await H.WaitUntil(() => inner.Calls == 1);
        for (var i = 1; i <= Queue; i++)
        {
            var path = $"/{i}";
            tasks.Add(Task.Run(() => client.GetAsync(path)));
            await H.WaitUntil(() => bulkhead.QueueLength == i);
        }

        for (var i = 0; i <= Queue; i++)
        {
            gates[i].SetResult();
            if (i < Queue)
            {
                await H.WaitUntil(() => inner.Calls == i + 2);
            }
        }

        await Task.WhenAll(tasks);
        Assert.Equal(new[] { "/0", "/1", "/2", "/3", "/4" }, order);
    }

    internal static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
            {
                return;
            }
        }
    }
}

/// <summary>An inner handler defined by a delegate, for tests that need real concurrency.</summary>
public sealed class FuncHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _func;
    private int _calls;

    public FuncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> func)
    {
        _func = func;
    }

    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return _func(request, cancellationToken);
    }
}
