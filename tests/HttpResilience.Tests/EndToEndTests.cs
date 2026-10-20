using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace HttpResilience.Tests;

/// <summary>End-to-end tests against a real Kestrel server on a random free port. Real sockets, small real timeouts.</summary>
public class EndToEndTests
{
    private static HttpClient ClientFor(TestUpstream upstream, ResilienceBuilder builder) =>
        new(builder.Build(new SocketsHttpHandler()), disposeHandler: true)
        {
            BaseAddress = upstream.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private static Task Status(HttpContext ctx, int code)
    {
        ctx.Response.StatusCode = code;
        return Task.CompletedTask;
    }

    private static async Task HangUntilAborted(HttpContext ctx)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Retry_recovers_from_a_flaky_upstream()
    {
        await using var upstream = await TestUpstream.StartAsync((ctx, n) => Status(ctx, n <= 2 ? 503 : 200));
        using var client = ClientFor(upstream, new ResilienceBuilder().WithRetry(o => o.BaseDelay = TimeSpan.FromMilliseconds(1)));
        var response = await client.GetAsync("/item");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, upstream.Hits);
    }

    [Fact]
    public async Task Post_without_a_key_is_sent_once_and_with_a_key_is_retried_with_the_same_body()
    {
        var seen = new ConcurrentQueue<(string? Key, string Body)>();
        await using var upstream = await TestUpstream.StartAsync(async (ctx, _) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            seen.Enqueue((ctx.Request.Headers["Idempotency-Key"].FirstOrDefault(), await reader.ReadToEndAsync()));
            ctx.Response.StatusCode = 503;
        });
        using var client = ClientFor(upstream, new ResilienceBuilder().WithRetry(o => o.BaseDelay = TimeSpan.FromMilliseconds(1)));

        var plain = await client.PostAsync("/orders", new StringContent("{\"n\":1}"));
        Assert.Equal(503, (int)plain.StatusCode);
        Assert.Equal(1, upstream.Hits);

        using var keyed = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = new StringContent("{\"n\":2}") };
        keyed.Headers.Add("Idempotency-Key", "order-42");
        var response = await client.SendAsync(keyed);
        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal(1 + 4, upstream.Hits);

        var keyedAttempts = seen.Skip(1).ToList();
        Assert.Equal(4, keyedAttempts.Count);
        Assert.All(keyedAttempts, a =>
        {
            Assert.Equal("order-42", a.Key);
            Assert.Equal("{\"n\":2}", a.Body);
        });
    }

    [Fact]
    public async Task A_large_body_is_resent_intact()
    {
        var hashes = new ConcurrentQueue<string>();
        await using var upstream = await TestUpstream.StartAsync(async (ctx, n) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            hashes.Enqueue(Convert.ToHexString(SHA256.HashData(ms.ToArray())));
            ctx.Response.StatusCode = n == 1 ? 503 : 200;
        });
        using var client = ClientFor(upstream, new ResilienceBuilder().WithRetry(o => o.BaseDelay = TimeSpan.FromMilliseconds(1)));
        var payload = new byte[200_000];
        new Random(5).NextBytes(payload);
        var response = await client.PutAsync("/blob", new ByteArrayContent(payload));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = Convert.ToHexString(SHA256.HashData(payload));
        Assert.Equal(new[] { expected, expected }, hashes.ToArray());
    }

    [Fact]
    public async Task Retry_after_from_a_real_response_is_honoured_and_capped()
    {
        await using var upstream = await TestUpstream.StartAsync((ctx, n) =>
        {
            if (n == 1)
            {
                ctx.Response.Headers["Retry-After"] = "1";
            }

            return Status(ctx, n == 1 ? 503 : 200);
        });
        using var client = ClientFor(upstream, new ResilienceBuilder().WithRetry(o =>
        {
            o.BaseDelay = TimeSpan.FromMilliseconds(1);
            o.RandomSource = () => 0;
            o.MaxRetryAfter = TimeSpan.FromMilliseconds(120);
        }));
        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync("/item");
        sw.Stop();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange(sw.ElapsedMilliseconds, 100, 900);
    }

    [Fact]
    public async Task The_deadline_header_reaches_the_upstream_and_shrinks_across_retries()
    {
        var values = new ConcurrentQueue<long>();
        await using var upstream = await TestUpstream.StartAsync((ctx, n) =>
        {
            values.Enqueue(long.Parse(ctx.Request.Headers["X-Request-Deadline-Ms"].Single()!));
            return Status(ctx, n <= 2 ? 503 : 200);
        });
        using var client = ClientFor(upstream, new ResilienceBuilder()
            .WithOverallDeadline(TimeSpan.FromSeconds(10))
            .WithRetry(o =>
            {
                o.BaseDelay = TimeSpan.FromMilliseconds(30);
                o.RandomSource = () => 1.0;
            })
            .WithAttemptTimeout(TimeSpan.FromSeconds(5)));
        var response = await client.GetAsync("/item");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = values.ToList();
        Assert.Equal(3, list.Count);
        Assert.InRange(list[0], 9_000, 10_000);
        Assert.True(list[0] > list[1] && list[1] > list[2], $"not strictly decreasing: {string.Join(", ", list)}");
    }

    [Fact]
    public async Task A_hanging_attempt_times_out_and_the_retry_succeeds()
    {
        await using var upstream = await TestUpstream.StartAsync(async (ctx, n) =>
        {
            if (n == 1)
            {
                await HangUntilAborted(ctx);
                return;
            }

            ctx.Response.StatusCode = 200;
        });
        using var client = ClientFor(upstream, new ResilienceBuilder()
            .WithRetry(o =>
            {
                o.BaseDelay = TimeSpan.FromMilliseconds(1);
                o.RandomSource = () => 0;
            })
            .WithAttemptTimeout(TimeSpan.FromMilliseconds(150)));
        var response = await client.GetAsync("/slow");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, upstream.Hits);
    }

    [Fact]
    public async Task When_everything_hangs_the_overall_deadline_ends_the_request_with_a_typed_timeout()
    {
        await using var upstream = await TestUpstream.StartAsync((ctx, _) => HangUntilAborted(ctx));
        using var client = ClientFor(upstream, new ResilienceBuilder()
            .WithOverallDeadline(TimeSpan.FromMilliseconds(400))
            .WithRetry(o =>
            {
                o.BaseDelay = TimeSpan.FromMilliseconds(1);
                o.RandomSource = () => 0;
            })
            .WithAttemptTimeout(TimeSpan.FromMilliseconds(150)));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => client.GetAsync("/slow"));
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried_and_not_a_timeout()
    {
        await using var upstream = await TestUpstream.StartAsync((ctx, _) => HangUntilAborted(ctx));
        using var client = ClientFor(upstream, new ResilienceBuilder()
            .WithOverallDeadline(TimeSpan.FromSeconds(30))
            .WithRetry(o => o.BaseDelay = TimeSpan.FromMilliseconds(1))
            .WithAttemptTimeout(TimeSpan.FromSeconds(30)));
        using var cts = new CancellationTokenSource();
        var task = client.GetAsync("/slow", cts.Token);
        await H.WaitUntil(() => upstream.Hits == 1);
        cts.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.IsNotType<TimeoutRejectedException>(ex);
        Assert.Equal(1, upstream.Hits);
    }

    [Fact]
    public async Task The_breaker_opens_and_stops_traffic_to_a_failing_upstream()
    {
        var changes = new ConcurrentQueue<CircuitStateChange>();
        await using var upstream = await TestUpstream.StartAsync((ctx, _) => Status(ctx, 503));
        var builder = new ResilienceBuilder()
            .WithRetry(o =>
            {
                o.MaxRetries = 1;
                o.BaseDelay = TimeSpan.FromMilliseconds(1);
            })
            .WithCircuitBreaker(new CircuitBreakerOptions
            {
                WindowSize = 4,
                MinimumCalls = 4,
                OpenDuration = TimeSpan.FromMinutes(5),
                OnStateChange = changes.Enqueue,
            });
        using var client = ClientFor(upstream, builder);
        Assert.Equal(503, (int)(await client.GetAsync("/x")).StatusCode);
        Assert.Equal(503, (int)(await client.GetAsync("/x")).StatusCode);
        Assert.Equal(4, upstream.Hits);
        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.GetAsync("/x"));
        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.GetAsync("/x"));
        Assert.Equal(4, upstream.Hits);
        Assert.Equal(CircuitState.Open, builder.Breaker!.State);
        Assert.Equal(CircuitState.Open, Assert.Single(changes).To);
    }

    [Fact]
    public async Task The_bulkhead_limits_queues_and_rejects_against_a_real_server()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await TestUpstream.StartAsync(async (ctx, _) =>
        {
            await gate.Task;
            ctx.Response.StatusCode = 200;
        });
        var builder = new ResilienceBuilder().WithBulkhead(new BulkheadOptions { MaxConcurrency = 2, MaxQueue = 1 });
        using var client = ClientFor(upstream, builder);

        var first = client.GetAsync("/a");
        var second = client.GetAsync("/b");
        await H.WaitUntil(() => upstream.Hits == 2);
        var queued = client.GetAsync("/c");
        await H.WaitUntil(() => builder.Bulkhead!.QueueLength == 1);
        await Assert.ThrowsAsync<BulkheadRejectedException>(() => client.GetAsync("/d"));
        Assert.Equal(2, upstream.Hits);

        gate.SetResult();
        var responses = await Task.WhenAll(first, second, queued);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(3, upstream.Hits);
        Assert.Equal(0, builder.Bulkhead!.InFlight);
    }

    [Fact]
    public async Task A_retry_budget_bounds_amplification_against_a_dead_upstream()
    {
        await using var upstream = await TestUpstream.StartAsync((ctx, _) => Status(ctx, 503));
        var builder = new ResilienceBuilder().WithRetry(
            o =>
            {
                o.MaxRetries = 3;
                o.BaseDelay = TimeSpan.FromMilliseconds(1);
                o.RandomSource = () => 0;
            },
            budget: new RetryBudgetOptions { RetryRatio = 0.1, MinRetriesPerWindow = 2, Window = TimeSpan.FromMinutes(5) });
        using var client = ClientFor(upstream, builder);
        for (var i = 0; i < 50; i++)
        {
            (await client.GetAsync("/x")).Dispose();
        }

        // 50 first attempts plus at most 2 + floor(0.1 * 50) = 7 retries. Without a budget it would be 200 hits.
        Assert.InRange(upstream.Hits, 51, 57);
    }
}
