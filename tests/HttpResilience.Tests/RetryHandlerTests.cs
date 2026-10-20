using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace HttpResilience.Tests;

public class RetryHandlerTests
{
    private static HttpStatusCode C(int code) => (HttpStatusCode)code;

    private sealed class Rig
    {
        public Rig(Action<RetryOptions>? configure = null, double jitter = 1.0)
        {
            Clock = new TestClock();
            Inner = new ScriptedHandler(Clock);
            Options = new RetryOptions
            {
                BaseDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(10),
                RandomSource = () => jitter,
                OnRetry = Events.Add,
            };
            configure?.Invoke(Options);
            Client = H.Client(new RetryHandler(Options, Clock) { InnerHandler = Inner });
        }

        public TestClock Clock { get; }

        public ScriptedHandler Inner { get; }

        public RetryOptions Options { get; }

        public List<RetryEvent> Events { get; } = new();

        public HttpClient Client { get; }

        public Task<HttpResponseMessage> Send(HttpRequestMessage request) => Clock.Drive(Client.SendAsync(request));

        public Task<HttpResponseMessage> Get(string path = "/x") => Clock.Drive(Client.GetAsync(path));
    }

    // ---------- what gets retried ----------

    [Fact]
    public async Task Success_is_returned_without_retry()
    {
        var rig = new Rig();
        var response = await rig.Get();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, rig.Inner.CallCount);
        Assert.Empty(rig.Events);
    }

    [Fact]
    public async Task Get_503_then_200_is_retried_once()
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503)).Respond(C(200));
        var response = await rig.Get();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, rig.Inner.CallCount);
        Assert.Equal(TimeSpan.FromMilliseconds(100), Assert.Single(rig.Events).Delay);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(599)]
    public async Task Retryable_statuses_are_retried(int status)
    {
        var rig = new Rig();
        rig.Inner.Respond(C(status)).Respond(C(200));
        var response = await rig.Get();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, rig.Inner.CallCount);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(304)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(499)]
    public async Task Other_statuses_are_returned_immediately(int status)
    {
        var rig = new Rig();
        rig.Inner.Respond(C(status), times: 3);
        var response = await rig.Get();
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(1, rig.Inner.CallCount);
    }

    [Fact]
    public async Task Exhausted_retries_return_the_last_response_not_an_exception()
    {
        var rig = new Rig(o => o.MaxRetries = 2);
        rig.Inner.Respond(C(503), times: 10);
        var response = await rig.Get();
        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal(3, rig.Inner.CallCount);
        Assert.Equal(2, rig.Events.Count);
    }

    [Fact]
    public async Task Zero_max_retries_never_retries()
    {
        var rig = new Rig(o => o.MaxRetries = 0);
        rig.Inner.Respond(C(503), times: 3);
        var response = await rig.Get();
        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal(1, rig.Inner.CallCount);
    }

    [Fact]
    public async Task Failed_attempt_responses_are_disposed_before_the_retry()
    {
        var rig = new Rig();
        var first = new HttpResponseMessage(C(503)) { Content = new TrackingContent() };
        rig.Inner.Run((_, _) => Task.FromResult(first)).Respond(C(200));
        await rig.Get();
        Assert.True(((TrackingContent)first.Content).Disposed);
    }

    private sealed class TrackingContent : StringContent
    {
        public TrackingContent() : base("x")
        {
        }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task Response_points_back_to_the_callers_request()
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503)).Respond(C(200));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/x");
        var response = await rig.Send(request);
        Assert.Same(request, response.RequestMessage);
    }

    // ---------- idempotency ----------

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task Idempotent_methods_are_retried(string method)
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503)).Respond(C(200));
        var response = await rig.Send(new HttpRequestMessage(new HttpMethod(method), "/x"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, rig.Inner.CallCount);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("TRACE")]
    [InlineData("CONNECT")]
    public async Task Other_methods_are_not_retried(string method)
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503)).Respond(C(200));
        var response = await rig.Send(new HttpRequestMessage(new HttpMethod(method), "/x"));
        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal(1, rig.Inner.CallCount);
        Assert.Empty(rig.Events);
    }

    [Fact]
    public async Task Post_without_a_key_is_not_retried_on_a_transport_error_either()
    {
        var rig = new Rig();
        rig.Inner.Throw(() => new HttpRequestException("boom")).Respond(C(200));
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Send(new HttpRequestMessage(HttpMethod.Post, "/x")));
        Assert.Equal(1, rig.Inner.CallCount);
    }

    [Fact]
    public async Task Post_with_an_idempotency_key_is_retried()
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503)).Respond(C(200));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/x") { Content = new StringContent("{}") };
        request.Headers.Add("Idempotency-Key", "abc-123");
        var response = await rig.Send(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, rig.Inner.CallCount);
        Assert.All(rig.Inner.Requests, r => Assert.Equal("abc-123", r.Header("Idempotency-Key")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_with_a_blank_idempotency_key_is_not_retried(string key)
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503)).Respond(C(200));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/x");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        var response = await rig.Send(request);
        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal(1, rig.Inner.CallCount);
    }

    [Fact]
    public async Task Idempotency_key_header_name_is_case_insensitive()
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503)).Respond(C(200));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/x");
        request.Headers.Add("idempotency-key", "k");
        var response = await rig.Send(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Retryable_request_rule_is_exposed()
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, "http://x");
        using var post = new HttpRequestMessage(HttpMethod.Post, "http://x");
        Assert.True(RetryHandler.IsRetryableRequest(get));
        Assert.False(RetryHandler.IsRetryableRequest(post));
        post.Headers.Add("Idempotency-Key", "1");
        Assert.True(RetryHandler.IsRetryableRequest(post));
    }

    // ---------- exceptions ----------

    [Fact]
    public async Task Transport_exception_is_retried()
    {
        var rig = new Rig();
        rig.Inner.Throw(() => new HttpRequestException("reset")).Respond(C(200));
        var response = await rig.Get();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("HttpRequestException", Assert.Single(rig.Events).Reason);
    }

    [Fact]
    public async Task Exhausted_transport_exceptions_rethrow_the_original()
    {
        var rig = new Rig(o => o.MaxRetries = 2);
        var error = new HttpRequestException("reset");
        rig.Inner.Throw(() => error, times: 5);
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => rig.Get());
        Assert.Same(error, thrown);
        Assert.Equal(3, rig.Inner.CallCount);
    }

    [Fact]
    public async Task Attempt_timeout_exception_is_retried()
    {
        var rig = new Rig();
        rig.Inner.Throw(() => new TimeoutRejectedException(TimeoutKind.Attempt, TimeSpan.FromSeconds(1))).Respond(C(200));
        var response = await rig.Get();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, rig.Inner.CallCount);
    }

    [Fact]
    public async Task Overall_timeout_exception_is_not_retried()
    {
        var rig = new Rig();
        rig.Inner.Throw(() => new TimeoutRejectedException(TimeoutKind.Overall, TimeSpan.FromSeconds(1))).Respond(C(200));
        var thrown = await Assert.ThrowsAsync<TimeoutRejectedException>(() => rig.Get());
        Assert.Equal(TimeoutKind.Overall, thrown.Kind);
        Assert.Equal(1, rig.Inner.CallCount);
    }

    [Fact]
    public async Task Cancellation_that_did_not_come_from_the_caller_is_treated_as_a_timeout_and_retried()
    {
        var rig = new Rig();
        rig.Inner.Throw(() => new TaskCanceledException("transport timeout")).Respond(C(200));
        var response = await rig.Get();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, rig.Inner.CallCount);
    }

    [Theory]
    [InlineData(typeof(BrokenCircuitException))]
    [InlineData(typeof(BulkheadRejectedException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    public async Task Other_exceptions_are_not_retried(Type type)
    {
        var rig = new Rig();
        Exception Make() => type == typeof(BrokenCircuitException) ? new BrokenCircuitException("open", TimeSpan.Zero)
            : type == typeof(BulkheadRejectedException) ? new BulkheadRejectedException(1, 1)
            : (Exception)Activator.CreateInstance(type)!;
        rig.Inner.Throw(Make).Respond(C(200));
        var thrown = await Assert.ThrowsAsync(type, () => rig.Get());
        Assert.IsType(type, thrown);
        Assert.Equal(1, rig.Inner.CallCount);
    }

    // ---------- cancellation ----------

    [Fact]
    public async Task Caller_cancellation_while_in_flight_is_not_retried()
    {
        var rig = new Rig();
        rig.Inner.Hang().Respond(C(200));
        using var cts = new CancellationTokenSource();
        var task = rig.Client.GetAsync("/x", cts.Token);
        Assert.Equal(1, rig.Inner.CallCount);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, rig.Inner.CallCount);
        Assert.Empty(rig.Events);
    }

    [Fact]
    public async Task Caller_cancellation_surfacing_as_an_inner_exception_is_not_retried()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.Inner.Run((_, ct) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }).Respond(C(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Clock.Drive(rig.Client.GetAsync("/x", cts.Token)));
        Assert.Equal(1, rig.Inner.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_during_backoff_stops_the_retry()
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503)).Respond(C(200));
        using var cts = new CancellationTokenSource();
        var task = rig.Client.GetAsync("/x", cts.Token);
        Assert.Equal(1, rig.Inner.CallCount);
        Assert.Equal(1, rig.Clock.LiveTimerCount);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, rig.Inner.CallCount);
        Assert.Equal(0, rig.Clock.LiveTimerCount);
    }

    [Fact]
    public async Task Cancelled_before_the_first_attempt_is_not_a_timeout()
    {
        var rig = new Rig();
        rig.Inner.Run((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Client.GetAsync("/x", cts.Token));
        Assert.IsNotType<TimeoutRejectedException>(ex);
    }

    // ---------- backoff and jitter ----------

    [Fact]
    public async Task Delays_double_with_each_retry()
    {
        var rig = new Rig(o => o.MaxRetries = 4);
        rig.Inner.Respond(C(503), times: 10);
        await rig.Get();
        Assert.Equal(
            new[] { 100, 200, 400, 800 }.Select(ms => TimeSpan.FromMilliseconds(ms)),
            rig.Events.Select(e => e.Delay));
        Assert.Equal(new[] { 1, 2, 3, 4 }, rig.Events.Select(e => e.RetryNumber));
    }

    [Fact]
    public async Task Full_jitter_scales_the_delay_between_zero_and_the_ceiling()
    {
        var rig = new Rig(o => o.MaxRetries = 3, jitter: 0.5);
        rig.Inner.Respond(C(503), times: 10);
        await rig.Get();
        Assert.Equal(
            new[] { 50, 100, 200 }.Select(ms => TimeSpan.FromMilliseconds(ms)),
            rig.Events.Select(e => e.Delay));
    }

    [Fact]
    public async Task Max_delay_caps_the_exponential_growth()
    {
        var rig = new Rig(o =>
        {
            o.MaxRetries = 5;
            o.MaxDelay = TimeSpan.FromMilliseconds(250);
        });
        rig.Inner.Respond(C(503), times: 10);
        await rig.Get();
        Assert.Equal(
            new[] { 100, 200, 250, 250, 250 }.Select(ms => TimeSpan.FromMilliseconds(ms)),
            rig.Events.Select(e => e.Delay));
    }

    [Fact]
    public async Task A_random_of_zero_retries_immediately_without_a_timer()
    {
        var rig = new Rig(jitter: 0);
        rig.Inner.Respond(C(503)).Respond(C(200));
        var task = rig.Client.GetAsync("/x");
        Assert.True(task.IsCompleted);
        Assert.Equal(TimeSpan.Zero, Assert.Single(rig.Events).Delay);
        Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(100.0)]
    [InlineData(double.PositiveInfinity)]
    public async Task A_random_above_one_is_clamped_to_the_ceiling(double value)
    {
        var rig = new Rig(jitter: value);
        rig.Inner.Respond(C(503)).Respond(C(200));
        await rig.Get();
        Assert.Equal(TimeSpan.FromMilliseconds(100), Assert.Single(rig.Events).Delay);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public async Task A_random_below_zero_or_nan_is_treated_as_zero(double value)
    {
        var rig = new Rig(jitter: value);
        rig.Inner.Respond(C(503)).Respond(C(200));
        await rig.Get();
        Assert.Equal(TimeSpan.Zero, Assert.Single(rig.Events).Delay);
    }

    [Fact]
    public async Task Seeded_jitter_always_stays_within_zero_and_the_ceiling()
    {
        var rig = new Rig(o =>
        {
            o.MaxRetries = 4;
            o.MaxDelay = TimeSpan.FromMilliseconds(500);
            o.RandomSource = H.Seeded(7);
        });
        for (var i = 0; i < 40; i++)
        {
            rig.Inner.Respond(C(503), times: 5);
            await rig.Get();
        }

        Assert.Equal(160, rig.Events.Count);
        foreach (var e in rig.Events)
        {
            var ceiling = RetryHandler.BackoffCeiling(rig.Options, e.RetryNumber - 1);
            Assert.InRange(e.Delay, TimeSpan.Zero, ceiling);
        }

        var first = rig.Events.Where(e => e.RetryNumber == 1).Select(e => e.Delay).ToList();
        Assert.Contains(first, d => d < TimeSpan.FromMilliseconds(25));
        Assert.Contains(first, d => d > TimeSpan.FromMilliseconds(75));
    }

    private static async Task<List<TimeSpan>> DelaysFor(int seed)
    {
        var rig = new Rig(o =>
        {
            o.MaxRetries = 3;
            o.RandomSource = H.Seeded(seed);
        });
        for (var i = 0; i < 5; i++)
        {
            rig.Inner.Respond(C(503), times: 4);
            await rig.Get();
        }

        return rig.Events.Select(e => e.Delay).ToList();
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_delays()
    {
        Assert.Equal(await DelaysFor(42), await DelaysFor(42));
    }

    [Fact]
    public async Task Different_seeds_give_different_delays()
    {
        Assert.NotEqual(await DelaysFor(42), await DelaysFor(43));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 200)]
    [InlineData(2, 400)]
    [InlineData(3, 800)]
    [InlineData(6, 6400)]
    [InlineData(7, 10000)]
    [InlineData(20, 10000)]
    [InlineData(1000, 10000)]
    public void Backoff_ceiling_is_exponential_and_capped(int index, int expectedMs)
    {
        var options = new RetryOptions { BaseDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromSeconds(10) };
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), RetryHandler.BackoffCeiling(options, index));
    }

    // ---------- Retry-After ----------

    private static Action<HttpResponseMessage> RetryAfter(string value) =>
        r => r.Headers.TryAddWithoutValidation("Retry-After", value);

    [Fact]
    public async Task Retry_after_seconds_lengthens_the_delay()
    {
        var rig = new Rig(jitter: 0);
        rig.Inner.Respond(C(503), RetryAfter("2")).Respond(C(200));
        await rig.Get();
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(rig.Events).Delay);
        Assert.Equal(TimeSpan.FromSeconds(2), rig.Inner.Requests[1].At - rig.Inner.Requests[0].At);
    }

    [Fact]
    public async Task Retry_after_on_429_is_honoured_too()
    {
        var rig = new Rig(jitter: 0);
        rig.Inner.Respond(C(429), RetryAfter("3")).Respond(C(200));
        await rig.Get();
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(rig.Events).Delay);
    }

    [Fact]
    public async Task Retry_after_is_capped_by_the_configured_maximum()
    {
        var rig = new Rig(o => o.MaxRetryAfter = TimeSpan.FromSeconds(5), jitter: 0);
        rig.Inner.Respond(C(503), RetryAfter("9999")).Respond(C(200));
        await rig.Get();
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(rig.Events).Delay);
    }

    [Fact]
    public async Task Retry_after_date_is_measured_against_the_injected_clock()
    {
        var rig = new Rig(jitter: 0);
        var when = rig.Clock.GetUtcNow().AddSeconds(3);
        rig.Inner.Respond(C(503), r => r.Headers.RetryAfter = new RetryConditionHeaderValue(when)).Respond(C(200));
        await rig.Get();
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(rig.Events).Delay);
    }

    [Fact]
    public async Task Retry_after_date_in_the_past_falls_back_to_the_backoff()
    {
        var rig = new Rig(jitter: 1.0);
        var past = rig.Clock.GetUtcNow().AddMinutes(-5);
        rig.Inner.Respond(C(503), r => r.Headers.RetryAfter = new RetryConditionHeaderValue(past)).Respond(C(200));
        await rig.Get();
        Assert.Equal(TimeSpan.FromMilliseconds(100), Assert.Single(rig.Events).Delay);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("-5")]
    [InlineData("")]
    [InlineData("1.5")]
    public async Task Invalid_retry_after_falls_back_to_the_backoff(string value)
    {
        var rig = new Rig(jitter: 1.0);
        rig.Inner.Respond(C(503), RetryAfter(value)).Respond(C(200));
        await rig.Get();
        Assert.Equal(TimeSpan.FromMilliseconds(100), Assert.Single(rig.Events).Delay);
    }

    [Fact]
    public async Task A_short_retry_after_never_shortens_the_backoff()
    {
        var rig = new Rig(o => o.BaseDelay = TimeSpan.FromSeconds(4), jitter: 1.0);
        rig.Inner.Respond(C(503), RetryAfter("1")).Respond(C(200));
        await rig.Get();
        Assert.Equal(TimeSpan.FromSeconds(4), Assert.Single(rig.Events).Delay);
    }

    [Fact]
    public async Task Retry_after_can_be_ignored_by_configuration()
    {
        var rig = new Rig(o => o.HonourRetryAfter = false, jitter: 0);
        rig.Inner.Respond(C(503), RetryAfter("10")).Respond(C(200));
        await rig.Get();
        Assert.Equal(TimeSpan.Zero, Assert.Single(rig.Events).Delay);
    }

    // ---------- retry budget ----------

    private static RetryBudget Budget(TestClock clock, double ratio, int floor) =>
        new(new RetryBudgetOptions { RetryRatio = ratio, MinRetriesPerWindow = floor }, clock);

    [Fact]
    public async Task An_exhausted_budget_stops_retries_across_requests()
    {
        var rig = new Rig();
        rig.Options.MaxRetries = 5;
        rig.Options.Budget = Budget(rig.Clock, ratio: 0, floor: 2);
        rig.Inner.Respond(C(503), times: 50);
        var first = await rig.Get();
        Assert.Equal(503, (int)first.StatusCode);
        Assert.Equal(3, rig.Inner.CallCount);
        var second = await rig.Get();
        Assert.Equal(503, (int)second.StatusCode);
        Assert.Equal(4, rig.Inner.CallCount);
    }

    [Fact]
    public async Task Budget_denial_rethrows_a_transport_exception()
    {
        var rig = new Rig();
        rig.Options.Budget = Budget(rig.Clock, ratio: 0, floor: 0);
        var error = new HttpRequestException("reset");
        rig.Inner.Throw(() => error, times: 3);
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => rig.Get());
        Assert.Same(error, thrown);
        Assert.Equal(1, rig.Inner.CallCount);
    }

    [Fact]
    public async Task Each_logical_request_is_recorded_once_regardless_of_attempts()
    {
        var rig = new Rig();
        rig.Options.Budget = Budget(rig.Clock, ratio: 1.0, floor: 0);
        rig.Inner.Respond(C(503), times: 2).Respond(C(200));
        await rig.Get();
        Assert.Equal(1, rig.Options.Budget.RequestsInWindow);
        Assert.Equal(1, rig.Options.Budget.RetriesInWindow);
    }

    [Fact]
    public async Task Budget_is_not_charged_when_the_request_is_not_retryable()
    {
        var rig = new Rig();
        rig.Options.Budget = Budget(rig.Clock, ratio: 0, floor: 5);
        rig.Inner.Respond(C(503));
        await rig.Send(new HttpRequestMessage(HttpMethod.Post, "/x"));
        Assert.Equal(0, rig.Options.Budget.RetriesInWindow);
        Assert.Equal(1, rig.Options.Budget.RequestsInWindow);
    }

    [Fact]
    public async Task Budget_is_not_charged_after_max_retries_is_reached()
    {
        var rig = new Rig(o => o.MaxRetries = 1);
        rig.Options.Budget = Budget(rig.Clock, ratio: 0, floor: 10);
        rig.Inner.Respond(C(503), times: 5);
        await rig.Get();
        Assert.Equal(1, rig.Options.Budget.RetriesInWindow);
    }

    [Fact]
    public async Task Budget_replenishes_as_the_window_slides()
    {
        var rig = new Rig();
        rig.Options.MaxRetries = 1;
        rig.Options.Budget = Budget(rig.Clock, ratio: 0, floor: 1);
        rig.Inner.Respond(C(503), times: 50);
        await rig.Get();
        await rig.Get();
        Assert.Equal(3, rig.Inner.CallCount);
        rig.Clock.Advance(TimeSpan.FromSeconds(11));
        await rig.Get();
        Assert.Equal(5, rig.Inner.CallCount);
    }

    // ---------- bodies and request copying ----------

    [Fact]
    public async Task The_request_body_is_resent_on_every_attempt()
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503), times: 2).Respond(C(200));
        using var request = new HttpRequestMessage(HttpMethod.Put, "/x")
        {
            Content = new StringContent("hello body", Encoding.UTF8, "text/plain"),
        };
        var response = await rig.Send(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, rig.Inner.CallCount);
        Assert.All(rig.Inner.Requests, r =>
        {
            Assert.Equal("hello body", Encoding.UTF8.GetString(r.Body!));
            Assert.Equal("text/plain; charset=utf-8", r.Header("Content-Type"));
        });
    }

    [Fact]
    public async Task Binary_bodies_survive_unchanged()
    {
        var rig = new Rig();
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        rig.Inner.Respond(C(503)).Respond(C(200));
        using var request = new HttpRequestMessage(HttpMethod.Put, "/x") { Content = new ByteArrayContent(bytes) };
        await rig.Send(request);
        Assert.All(rig.Inner.Requests, r => Assert.Equal(bytes, r.Body));
    }

    [Fact]
    public async Task A_body_larger_than_the_limit_is_sent_once_and_not_retried()
    {
        var rig = new Rig(o => o.MaxBufferedContentBytes = 4);
        rig.Inner.Respond(C(503)).Respond(C(200));
        using var request = new HttpRequestMessage(HttpMethod.Put, "/x") { Content = new ByteArrayContent(new byte[10]) };
        var response = await rig.Send(request);
        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal(1, rig.Inner.CallCount);
        Assert.Equal(10, rig.Inner.Requests[0].Body!.Length);
    }

    [Fact]
    public async Task A_body_exactly_at_the_limit_is_still_buffered()
    {
        var rig = new Rig(o => o.MaxBufferedContentBytes = 10);
        rig.Inner.Respond(C(503)).Respond(C(200));
        using var request = new HttpRequestMessage(HttpMethod.Put, "/x") { Content = new ByteArrayContent(new byte[10]) };
        var response = await rig.Send(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, rig.Inner.CallCount);
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public NonSeekableStream(byte[] bytes) : base(bytes)
        {
        }

        public override bool CanSeek => false;
    }

    [Fact]
    public async Task A_body_of_unknown_length_is_sent_once_and_not_retried()
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503)).Respond(C(200));
        using var request = new HttpRequestMessage(HttpMethod.Put, "/x")
        {
            Content = new StreamContent(new NonSeekableStream(new byte[] { 1, 2, 3 })),
        };
        var response = await rig.Send(request);
        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal(1, rig.Inner.CallCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, rig.Inner.Requests[0].Body);
    }

    [Fact]
    public async Task Headers_and_options_are_copied_to_every_attempt()
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503)).Respond(C(200));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/x?q=1");
        request.Headers.Add("X-Custom", "v1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Options.Set(new HttpRequestOptionsKey<string>("my.key"), "my-value");
        await rig.Send(request);
        Assert.All(rig.Inner.Requests, r =>
        {
            Assert.Equal("v1", r.Header("X-Custom"));
            Assert.Equal("application/json", r.Header("Accept"));
            Assert.Equal("http://test.local/x?q=1", r.Uri!.ToString());
            Assert.Equal("my-value", r.Options["my.key"]);
        });
    }

    [Fact]
    public async Task Bodiless_requests_stay_bodiless()
    {
        var rig = new Rig();
        rig.Inner.Respond(C(503)).Respond(C(200));
        await rig.Get();
        Assert.All(rig.Inner.Requests, r => Assert.Null(r.Body));
    }

    // ---------- deadline interaction ----------

    private static (TestClock clock, ScriptedHandler inner, HttpClient client, List<RetryEvent> events) DeadlineChain(TimeSpan overall)
    {
        var clock = new TestClock();
        var inner = new ScriptedHandler(clock);
        var events = new List<RetryEvent>();
        var retry = new RetryHandler(
            new RetryOptions { BaseDelay = TimeSpan.FromMilliseconds(100), RandomSource = () => 1.0, OnRetry = events.Add },
            clock);
        var chain = H.Chain(
            inner,
            new TimeoutHandler(new TimeoutOptions { OverallTimeout = overall, PropagateDeadline = false }, clock),
            retry,
            new TimeoutHandler(new TimeoutOptions(), clock));
        return (clock, inner, H.Client(chain), events);
    }

    [Fact]
    public async Task The_deadline_header_shrinks_across_retries()
    {
        var (clock, inner, client, _) = DeadlineChain(TimeSpan.FromSeconds(1));
        inner.Respond(C(503), times: 2).Respond(C(200));
        await clock.Drive(client.GetAsync("/x"));
        Assert.Equal(new[] { "1000", "900", "700" }, inner.Requests.Select(r => r.Header(ResilienceKeys.DeadlineHeaderName)));
    }

    [Fact]
    public async Task No_retry_when_the_remaining_deadline_is_shorter_than_the_next_delay()
    {
        var (clock, inner, client, events) = DeadlineChain(TimeSpan.FromMilliseconds(250));
        inner.Respond(C(503), times: 5);
        var response = await clock.Drive(client.GetAsync("/x"));
        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal(2, inner.CallCount);
        Assert.Single(events);
    }

    [Fact]
    public async Task No_retry_when_the_remaining_deadline_equals_the_next_delay()
    {
        var (clock, inner, client, events) = DeadlineChain(TimeSpan.FromMilliseconds(300));
        inner.Respond(C(503), times: 5);
        await clock.Drive(client.GetAsync("/x"));
        Assert.Equal(2, inner.CallCount);
        Assert.Single(events);
    }

    [Fact]
    public async Task A_retry_happens_when_the_remaining_deadline_just_exceeds_the_next_delay()
    {
        var (clock, inner, client, events) = DeadlineChain(TimeSpan.FromMilliseconds(301));
        inner.Respond(C(503), times: 2).Respond(C(200));
        var response = await clock.Drive(client.GetAsync("/x"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.CallCount);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task The_overall_deadline_firing_in_flight_is_a_timeout_and_is_not_retried()
    {
        var (clock, inner, client, events) = DeadlineChain(TimeSpan.FromMilliseconds(100));
        inner.Hang().Respond(C(200));
        var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() => clock.Drive(client.GetAsync("/x")));
        Assert.Equal(TimeoutKind.Overall, ex.Kind);
        Assert.Equal(1, inner.CallCount);
        Assert.Empty(events);
    }

    [Fact]
    public async Task An_attempt_timeout_is_retried_and_the_second_attempt_can_succeed()
    {
        var clock = new TestClock();
        var inner = new ScriptedHandler(clock);
        inner.Hang().Respond(C(200));
        var chain = H.Chain(
            inner,
            new RetryHandler(new RetryOptions { RandomSource = () => 0.0 }, clock),
            new TimeoutHandler(new TimeoutOptions { AttemptTimeout = TimeSpan.FromMilliseconds(100) }, clock));
        var response = await clock.Drive(H.Client(chain).GetAsync("/x"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.CallCount);
    }

    // ---------- options ----------

    [Fact]
    public void Invalid_options_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryHandler(new RetryOptions { MaxRetries = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryHandler(new RetryOptions { BaseDelay = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryHandler(new RetryOptions { MaxDelay = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryHandler(new RetryOptions { MaxRetryAfter = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryHandler(new RetryOptions { MaxBufferedContentBytes = -1 }));
    }

    [Fact]
    public async Task Default_options_work_with_the_system_clock_and_random()
    {
        using var handler = new RetryHandler { InnerHandler = new ScriptedHandler() };
        using var client = H.Client(handler);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/x")).StatusCode);
    }
}
