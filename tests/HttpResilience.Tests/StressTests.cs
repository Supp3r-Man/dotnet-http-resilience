using System.Collections.Concurrent;
using System.Net;

namespace HttpResilience.Tests;

/// <summary>Thread-safety stress tests. They assert invariants, not timings.</summary>
public class StressTests
{
    [Fact]
    public async Task Bulkhead_never_exceeds_its_limit_and_never_loses_a_permit()
    {
        const int Max = 4;
        const int Workers = 32;
        const int Iterations = 400;
        var bulkhead = new Bulkhead(new BulkheadOptions { MaxConcurrency = Max, MaxQueue = 8 });
        var inside = 0;
        var peak = 0;
        var acquired = 0;
        var cancelled = 0;
        var rejected = 0;

        var workers = Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < Iterations; i++)
            {
                using var cts = new CancellationTokenSource();
                BulkheadLease lease;
                try
                {
                    var pending = bulkhead.AcquireAsync(cts.Token);
                    if ((i + w) % 7 == 0)
                    {
                        cts.Cancel();
                    }

                    lease = await pending;
                }
                catch (BulkheadRejectedException)
                {
                    Interlocked.Increment(ref rejected);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                    continue;
                }

                Interlocked.Increment(ref acquired);
                BulkheadTests.InterlockedMax(ref peak, Interlocked.Increment(ref inside));
                await Task.Yield();
                Interlocked.Decrement(ref inside);
                lease.Dispose();
            }
        })).ToArray();
        await Task.WhenAll(workers);

        Assert.True(peak <= Max, $"peak {peak} exceeded {Max}");
        Assert.True(bulkhead.MaxObservedInFlight <= Max);
        Assert.Equal(0, bulkhead.InFlight);
        Assert.Equal(0, bulkhead.QueueLength);
        Assert.Equal(Workers * Iterations, acquired + cancelled + rejected);
        Assert.True(acquired > 0);

        // No lost permits: all slots can be taken again.
        var leases = new List<BulkheadLease>();
        for (var i = 0; i < Max; i++)
        {
            var attempt = bulkhead.AcquireAsync();
            Assert.True(attempt.IsCompletedSuccessfully);
            leases.Add(await attempt);
        }

        leases.ForEach(l => l.Dispose());
    }

    [Fact]
    public async Task Circuit_breaker_never_issues_more_probes_than_permits_and_never_gets_stuck()
    {
        const int Probes = 3;
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            WindowSize = 20,
            MinimumCalls = 10,
            FailureRateThreshold = 0.5,
            OpenDuration = TimeSpan.FromMilliseconds(1),
            HalfOpenPermits = Probes,
        });
        var probesPerGeneration = new ConcurrentDictionary<long, int>();
        var admitted = 0;
        var rejected = 0;

        var workers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            var random = new Random(w);
            for (var i = 0; i < 20_000; i++)
            {
                if (!breaker.TryAcquire(out var permit, out _))
                {
                    Interlocked.Increment(ref rejected);
                    continue;
                }

                Interlocked.Increment(ref admitted);
                if (permit.IsProbe)
                {
                    probesPerGeneration.AddOrUpdate(permit.Generation, 1, (_, n) => n + 1);
                }

                if (random.NextDouble() < 0.5)
                {
                    breaker.RecordFailure(permit);
                }
                else
                {
                    breaker.RecordSuccess(permit);
                }

                Assert.InRange(breaker.ProbesIssued, 0, Probes);
            }
        })).ToArray();
        await Task.WhenAll(workers);

        Assert.All(probesPerGeneration, kv => Assert.True(kv.Value <= Probes, $"generation {kv.Key} issued {kv.Value} probes"));
        Assert.True(breaker.Generation > 2, "the stress run should have exercised several transitions");
        Assert.True(admitted > 0 && rejected > 0);
        Assert.True(breaker.State != CircuitState.HalfOpen || breaker.ProbesIssued < Probes, "half-open with every permit issued and no outcome pending means a lost permit");
    }

    [Fact]
    public async Task Circuit_breaker_window_counts_stay_consistent_under_contention()
    {
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            WindowSize = 50,
            MinimumCalls = 50,
            FailureRateThreshold = 1.0,
            OpenDuration = TimeSpan.FromHours(1),
        });
        var workers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 5_000; i++)
            {
                if (breaker.TryAcquire(out var permit, out _))
                {
                    breaker.RecordSuccess(permit);
                }

                var calls = breaker.CallsInWindow;
                var failures = breaker.FailuresInWindow;
                Assert.InRange(calls, 0, 50);
                Assert.Equal(0, failures);
            }
        })).ToArray();
        await Task.WhenAll(workers);
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(50, breaker.CallsInWindow);
    }

    [Fact]
    public async Task Full_stack_under_load_respects_the_bulkhead_and_ends_clean()
    {
        const int Max = 5;
        var inside = 0;
        var peak = 0;
        var inner = new FuncHandler(async (request, _) =>
        {
            BulkheadTests.InterlockedMax(ref peak, Interlocked.Increment(ref inside));
            await Task.Yield();
            Interlocked.Decrement(ref inside);
            var odd = request.RequestUri!.AbsolutePath.GetHashCode(StringComparison.Ordinal) % 3 == 0;
            return new HttpResponseMessage(odd ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        });
        var builder = new ResilienceBuilder()
            .WithOverallDeadline(TimeSpan.FromSeconds(30))
            .WithBulkhead(new BulkheadOptions { MaxConcurrency = Max, MaxQueue = 1000 })
            .WithRetry(o =>
            {
                o.BaseDelay = TimeSpan.FromMilliseconds(1);
                o.MaxRetries = 2;
            })
            .WithAttemptTimeout(TimeSpan.FromSeconds(10));
        using var client = new HttpClient(builder.Build(inner)) { BaseAddress = new Uri("http://test.local"), Timeout = Timeout.InfiniteTimeSpan };

        var tasks = Enumerable.Range(0, 600).Select(i => Task.Run(async () =>
        {
            using var response = await client.GetAsync($"/item/{i}");
            return (int)response.StatusCode;
        })).ToArray();
        var statuses = await Task.WhenAll(tasks);

        Assert.True(peak <= Max, $"peak {peak} exceeded {Max}");
        Assert.Equal(0, builder.Bulkhead!.InFlight);
        Assert.Equal(0, builder.Bulkhead.QueueLength);
        Assert.All(statuses, s => Assert.True(s is 200 or 503));
        Assert.Contains(200, statuses);
    }

    [Fact]
    public async Task Retry_budget_total_is_exact_under_concurrent_requests_and_retries()
    {
        var budget = new RetryBudget(new RetryBudgetOptions { RetryRatio = 0.1, MinRetriesPerWindow = 5, Window = TimeSpan.FromHours(1) });
        var granted = 0;
        var requests = 0;
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                budget.RecordRequest();
                Interlocked.Increment(ref requests);
                if (budget.TryAcquireRetry())
                {
                    Interlocked.Increment(ref granted);
                }
            }
        })).ToArray();
        await Task.WhenAll(workers);

        Assert.Equal(4000, requests);
        Assert.True(granted <= 5 + (4000 / 10), $"granted {granted} retries, budget allows at most {5 + 400}");
        Assert.Equal(granted, budget.RetriesInWindow);
    }
}
