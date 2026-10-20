using System.Net;

namespace HttpResilience.Tests;

public class CircuitBreakerHandlerTests
{
    private sealed class Rig
    {
        public Rig(int window = 4, int minCalls = 4, Func<HttpResponseMessage, bool>? isFailure = null, int probes = 3)
        {
            Clock = new TestClock();
            Inner = new ScriptedHandler(Clock);
            Changes = new List<CircuitStateChange>();
            Breaker = new CircuitBreaker(
                new CircuitBreakerOptions
                {
                    WindowSize = window,
                    MinimumCalls = minCalls,
                    FailureRateThreshold = 0.5,
                    OpenDuration = TimeSpan.FromSeconds(30),
                    HalfOpenPermits = probes,
                    OnStateChange = Changes.Add,
                },
                Clock);
            Client = H.Client(H.Chain(Inner, new CircuitBreakerHandler(Breaker, isFailure)));
        }

        public TestClock Clock { get; }

        public ScriptedHandler Inner { get; }

        public CircuitBreaker Breaker { get; }

        public List<CircuitStateChange> Changes { get; }

        public HttpClient Client { get; }
    }

    private static HttpStatusCode C(int code) => (HttpStatusCode)code;

    [Fact]
    public async Task A_success_is_recorded_and_returned()
    {
        var rig = new Rig();
        var response = await rig.Client.GetAsync("/x");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, rig.Breaker.CallsInWindow);
        Assert.Equal(0, rig.Breaker.FailuresInWindow);
    }

    [Theory]
    [InlineData(500, 1)]
    [InlineData(502, 1)]
    [InlineData(503, 1)]
    [InlineData(504, 1)]
    [InlineData(408, 1)]
    [InlineData(200, 0)]
    [InlineData(301, 0)]
    [InlineData(400, 0)]
    [InlineData(404, 0)]
    [InlineData(429, 0)]
    public async Task Only_server_errors_and_408_count_as_failures(int status, int expectedFailures)
    {
        var rig = new Rig();
        rig.Inner.Respond(C(status));
        await rig.Client.GetAsync("/x");
        Assert.Equal(1, rig.Breaker.CallsInWindow);
        Assert.Equal(expectedFailures, rig.Breaker.FailuresInWindow);
    }

    [Fact]
    public async Task An_open_circuit_fast_fails_without_calling_the_inner_handler()
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503), times: 4);
        for (var i = 0; i < 4; i++)
        {
            await rig.Client.GetAsync("/x");
        }

        Assert.Equal(CircuitState.Open, rig.Breaker.State);
        var ex = await Assert.ThrowsAsync<BrokenCircuitException>(() => rig.Client.GetAsync("/x"));
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        Assert.Equal(4, rig.Inner.CallCount);
    }

    [Fact]
    public async Task The_circuit_recovers_through_half_open_probes()
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503), times: 4);
        for (var i = 0; i < 4; i++)
        {
            await rig.Client.GetAsync("/x");
        }

        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await rig.Client.GetAsync("/x")).StatusCode);
        }

        Assert.Equal(CircuitState.Closed, rig.Breaker.State);
        Assert.Equal(
            new[] { CircuitState.Open, CircuitState.HalfOpen, CircuitState.Closed },
            rig.Changes.Select(c => c.To));
    }

    [Fact]
    public async Task A_transport_exception_counts_as_a_failure_and_is_rethrown()
    {
        var rig = new Rig();
        var error = new HttpRequestException("reset");
        rig.Inner.Throw(() => error);
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => rig.Client.GetAsync("/x"));
        Assert.Same(error, thrown);
        Assert.Equal(1, rig.Breaker.FailuresInWindow);
    }

    [Fact]
    public async Task An_attempt_timeout_counts_as_a_failure()
    {
        var rig = new Rig();
        rig.Inner.Throw(() => new TimeoutRejectedException(TimeoutKind.Attempt, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => rig.Client.GetAsync("/x"));
        Assert.Equal(1, rig.Breaker.FailuresInWindow);
    }

    [Fact]
    public async Task An_overall_deadline_timeout_is_not_the_dependencys_fault_and_is_not_counted()
    {
        var rig = new Rig();
        rig.Inner.Throw(() => new TimeoutRejectedException(TimeoutKind.Overall, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => rig.Client.GetAsync("/x"));
        Assert.Equal(0, rig.Breaker.CallsInWindow);
        Assert.Equal(0, rig.Breaker.FailuresInWindow);
    }

    [Fact]
    public async Task A_cancellation_not_caused_by_the_caller_counts_as_a_failure()
    {
        var rig = new Rig();
        rig.Inner.Throw(() => new TaskCanceledException("transport timeout"));
        await Assert.ThrowsAsync<TaskCanceledException>(() => rig.Client.GetAsync("/x"));
        Assert.Equal(1, rig.Breaker.FailuresInWindow);
    }

    [Fact]
    public async Task Caller_cancellation_is_neither_a_success_nor_a_failure()
    {
        var rig = new Rig();
        rig.Inner.Hang();
        using var cts = new CancellationTokenSource();
        var task = rig.Client.GetAsync("/x", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, rig.Breaker.CallsInWindow);
        Assert.Equal(0, rig.Breaker.FailuresInWindow);
    }

    [Fact]
    public async Task Caller_cancellation_returns_a_half_open_probe_permit()
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503), times: 4);
        for (var i = 0; i < 4; i++)
        {
            await rig.Client.GetAsync("/x");
        }

        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Inner.Hang();
        using var cts = new CancellationTokenSource();
        var task = rig.Client.GetAsync("/x", cts.Token);
        Assert.Equal(1, rig.Breaker.ProbesIssued);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, rig.Breaker.ProbesIssued);
        Assert.Equal(CircuitState.HalfOpen, rig.Breaker.State);
    }

    [Fact]
    public async Task An_unrelated_exception_releases_the_permit_without_counting()
    {
        var rig = new Rig();
        rig.Inner.Throw(() => new InvalidOperationException("bug"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Client.GetAsync("/x"));
        Assert.Equal(0, rig.Breaker.CallsInWindow);
    }

    [Fact]
    public async Task A_custom_failure_predicate_is_used()
    {
        var rig = new Rig(isFailure: r => r.StatusCode == C(418));
        rig.Inner.Respond(C(418)).Respond(C(503));
        await rig.Client.GetAsync("/x");
        await rig.Client.GetAsync("/x");
        Assert.Equal(2, rig.Breaker.CallsInWindow);
        Assert.Equal(1, rig.Breaker.FailuresInWindow);
    }

    [Fact]
    public async Task A_slow_request_that_started_before_the_trip_cannot_close_the_breaker_later()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Inner.Run((_, _) => gate.Task).Respond(C(503), times: 4).Respond(C(200), times: 3);
        var slow = rig.Client.GetAsync("/slow");
        Assert.False(slow.IsCompleted);
        for (var i = 0; i < 4; i++)
        {
            await rig.Client.GetAsync("/fail");
        }

        Assert.Equal(CircuitState.Open, rig.Breaker.State);
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        await rig.Client.GetAsync("/probe1");
        Assert.Equal(CircuitState.HalfOpen, rig.Breaker.State);

        gate.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        await slow;
        await rig.Client.GetAsync("/probe2");
        Assert.Equal(CircuitState.HalfOpen, rig.Breaker.State);
        await rig.Client.GetAsync("/probe3");
        Assert.Equal(CircuitState.Closed, rig.Breaker.State);
    }

    [Fact]
    public void A_null_breaker_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new CircuitBreakerHandler(null!));
    }

    [Fact]
    public void The_default_failure_predicate_is_exposed()
    {
        using var ok = new HttpResponseMessage(HttpStatusCode.OK);
        using var bad = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.False(CircuitBreakerHandler.DefaultIsFailure(ok));
        Assert.True(CircuitBreakerHandler.DefaultIsFailure(bad));
    }
}
