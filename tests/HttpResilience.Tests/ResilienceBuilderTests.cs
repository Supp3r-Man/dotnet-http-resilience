using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.Tests;

public class ResilienceBuilderTests
{
    private static ResilienceBuilder Full(TimeProvider? time = null) =>
        new ResilienceBuilder(time)
            .WithAttemptTimeout(TimeSpan.FromSeconds(2))
            .WithCircuitBreaker(new CircuitBreakerOptions { WindowSize = 4, MinimumCalls = 4 })
            .WithRetry(o => o.RandomSource = () => 0)
            .WithBulkhead(new BulkheadOptions { MaxConcurrency = 2, MaxQueue = 1 })
            .WithOverallDeadline(TimeSpan.FromSeconds(10));

    [Fact]
    public void Stages_are_ordered_outermost_first_regardless_of_call_order()
    {
        Assert.Equal(
            new[] { "overall-deadline", "bulkhead", "retry", "circuit-breaker", "attempt-timeout" },
            Full().StageNames());
    }

    [Fact]
    public void Only_configured_stages_are_included()
    {
        Assert.Empty(new ResilienceBuilder().StageNames());
        Assert.Equal(new[] { "retry" }, new ResilienceBuilder().WithRetry().StageNames());
        Assert.Equal(new[] { "attempt-timeout" }, new ResilienceBuilder().WithAttemptTimeout(TimeSpan.FromSeconds(1)).StageNames());
        Assert.Equal(
            new[] { "overall-deadline", "attempt-timeout" },
            new ResilienceBuilder().WithOverallDeadline(TimeSpan.FromSeconds(1)).StageNames());
        Assert.Equal(new[] { "bulkhead", "circuit-breaker" }, new ResilienceBuilder().WithCircuitBreaker().WithBulkhead().StageNames());
    }

    [Fact]
    public void The_built_chain_has_the_documented_shape()
    {
        var inner = new ScriptedHandler();
        var current = Full().Build(inner);
        var types = new List<Type>();
        while (current is DelegatingHandler d)
        {
            types.Add(d.GetType());
            current = d.InnerHandler!;
        }

        Assert.Equal(
            new[] { typeof(TimeoutHandler), typeof(BulkheadHandler), typeof(RetryHandler), typeof(CircuitBreakerHandler), typeof(TimeoutHandler) },
            types);
        Assert.Same(inner, current);
    }

    [Fact]
    public void An_empty_builder_returns_the_inner_handler_unchanged()
    {
        var inner = new ScriptedHandler();
        Assert.Same(inner, new ResilienceBuilder().Build(inner));
    }

    [Fact]
    public void Every_chain_gets_fresh_handlers_but_shares_the_state_objects()
    {
        var builder = Full();
        var first = builder.CreateHandlers();
        var second = builder.CreateHandlers();
        for (var i = 0; i < first.Count; i++)
        {
            Assert.NotSame(first[i], second[i]);
        }

        Assert.NotNull(builder.Breaker);
        Assert.NotNull(builder.Bulkhead);
    }

    [Fact]
    public async Task Two_chains_share_one_circuit_breaker()
    {
        var builder = new ResilienceBuilder().WithCircuitBreaker(new CircuitBreakerOptions { WindowSize = 2, MinimumCalls = 2 });
        var innerA = new ScriptedHandler().Respond((HttpStatusCode)503, times: 5);
        var innerB = new ScriptedHandler();
        var clientA = H.Client(builder.Build(innerA));
        var clientB = H.Client(builder.Build(innerB));
        await clientA.GetAsync("/x");
        await clientA.GetAsync("/x");
        await Assert.ThrowsAsync<BrokenCircuitException>(() => clientB.GetAsync("/x"));
        Assert.Equal(0, innerB.CallCount);
    }

    [Fact]
    public void The_retry_budget_is_exposed_when_configured()
    {
        var builder = new ResilienceBuilder().WithRetry(budget: new RetryBudgetOptions { RetryRatio = 0.1 });
        Assert.NotNull(builder.Budget);
        Assert.Null(new ResilienceBuilder().WithRetry().Budget);
    }

    [Fact]
    public void Invalid_limits_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilienceBuilder().WithOverallDeadline(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilienceBuilder().WithAttemptTimeout(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilienceBuilder().WithRetry(o => o.MaxRetries = -1));
        Assert.Throws<ArgumentNullException>(() => new ResilienceBuilder().Build(null!));
    }

    [Fact]
    public async Task The_full_stack_retries_and_stamps_a_deadline_with_a_fake_clock()
    {
        var clock = new TestClock();
        var inner = new ScriptedHandler(clock).Respond((HttpStatusCode)503).Respond(HttpStatusCode.OK);
        var builder = new ResilienceBuilder(clock)
            .WithOverallDeadline(TimeSpan.FromSeconds(5))
            .WithRetry(o =>
            {
                o.BaseDelay = TimeSpan.FromMilliseconds(100);
                o.RandomSource = () => 1.0;
            })
            .WithAttemptTimeout(TimeSpan.FromSeconds(1));
        var client = H.Client(builder.Build(inner));
        var response = await clock.Drive(client.GetAsync("/x"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "5000", "4900" }, inner.Requests.Select(r => r.Header(ResilienceKeys.DeadlineHeaderName)));
    }

    [Fact]
    public async Task AddResilience_wires_the_handlers_into_IHttpClientFactory()
    {
        var inner = new ScriptedHandler().Respond((HttpStatusCode)503).Respond(HttpStatusCode.OK);
        var services = new ServiceCollection();
        services.AddHttpClient("api", c =>
            {
                c.BaseAddress = new Uri("http://test.local");
                c.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => inner)
            .AddResilience(b => b
                .WithOverallDeadline(TimeSpan.FromSeconds(5))
                .WithRetry(o =>
                {
                    o.BaseDelay = TimeSpan.FromMilliseconds(1);
                    o.RandomSource = () => 0;
                })
                .WithCircuitBreaker());
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        var response = await client.GetAsync("/x");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.CallCount);
        Assert.NotNull(inner.Requests[0].Header(ResilienceKeys.DeadlineHeaderName));
    }

    [Fact]
    public void AddResilience_validates_its_arguments()
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("x");
        Assert.Throws<ArgumentNullException>(() => builder.AddResilience(null!));
        Assert.Throws<ArgumentNullException>(() => ResilienceHttpClientBuilderExtensions.AddResilience(null!, _ => { }));
    }
}
