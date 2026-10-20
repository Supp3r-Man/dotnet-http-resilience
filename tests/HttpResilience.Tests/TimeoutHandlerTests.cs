using System.Net;

namespace HttpResilience.Tests;

public class TimeoutHandlerTests
{
    private const string Header = ResilienceKeys.DeadlineHeaderName;

    private static (TestClock clock, ScriptedHandler inner, HttpMessageHandler chain) Make(params TimeoutOptions[] outerToInner)
    {
        var clock = new TestClock();
        var inner = new ScriptedHandler(clock);
        var chain = H.Chain(inner, outerToInner.Select(o => (DelegatingHandler)new TimeoutHandler(o, clock)).ToArray());
        return (clock, inner, chain);
    }

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public async Task Without_any_limit_the_request_passes_through_untouched()
    {
        var (_, inner, chain) = Make(new TimeoutOptions());
        var response = await H.Client(chain).GetAsync("/x");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(inner.Requests[0].Header(Header));
    }

    [Fact]
    public async Task Attempt_timeout_throws_a_typed_exception_with_the_cancellation_as_inner()
    {
        var (clock, inner, chain) = Make(new TimeoutOptions { AttemptTimeout = Ms(200) });
        inner.Hang();
        var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() => clock.Drive(H.Client(chain).GetAsync("/x")));
        Assert.Equal(TimeoutKind.Attempt, ex.Kind);
        Assert.Equal(Ms(200), ex.Timeout);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
        Assert.IsAssignableFrom<TimeoutException>(ex);
    }

    [Fact]
    public async Task Attempt_timeout_fires_exactly_at_the_limit()
    {
        var (clock, inner, chain) = Make(new TimeoutOptions { AttemptTimeout = Ms(200) });
        inner.Hang();
        var task = H.Client(chain).GetAsync("/x");
        clock.Advance(Ms(199));
        Assert.False(task.IsCompleted);
        clock.Advance(Ms(1));
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => task);
    }

    [Fact]
    public async Task A_response_before_the_limit_wins_and_the_timer_is_released()
    {
        var (clock, _, chain) = Make(new TimeoutOptions { AttemptTimeout = Ms(200) });
        var response = await H.Client(chain).GetAsync("/x");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, clock.LiveTimerCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout()
    {
        var (_, inner, chain) = Make(new TimeoutOptions { AttemptTimeout = TimeSpan.FromSeconds(10) });
        inner.Hang();
        using var cts = new CancellationTokenSource();
        var task = H.Client(chain).GetAsync("/x", cts.Token);
        cts.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.IsNotType<TimeoutRejectedException>(ex);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout_for_the_overall_deadline_either()
    {
        var (_, inner, chain) = Make(new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(10) });
        inner.Hang();
        using var cts = new CancellationTokenSource();
        var task = H.Client(chain).GetAsync("/x", cts.Token);
        cts.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.IsNotType<TimeoutRejectedException>(ex);
    }

    [Fact]
    public async Task When_the_caller_cancels_and_the_timeout_fires_together_the_caller_wins()
    {
        var (clock, inner, chain) = Make(new TimeoutOptions { AttemptTimeout = Ms(200) });
        using var cts = new CancellationTokenSource();
        inner.Run((_, ct) =>
        {
            cts.Cancel();
            clock.Advance(Ms(200));
            throw new OperationCanceledException(ct);
        });
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => H.Client(chain).GetAsync("/x", cts.Token));
        Assert.IsNotType<TimeoutRejectedException>(ex);
    }

    [Fact]
    public async Task A_cancellation_raised_by_the_inner_handler_itself_is_not_a_timeout()
    {
        var (clock, inner, chain) = Make(new TimeoutOptions { AttemptTimeout = TimeSpan.FromSeconds(10) });
        inner.Throw(() => new TaskCanceledException("unrelated"));
        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => clock.Drive(H.Client(chain).GetAsync("/x")));
        Assert.IsNotType<TimeoutRejectedException>(ex);
    }

    [Fact]
    public async Task The_deadline_header_carries_the_remaining_milliseconds()
    {
        var (_, inner, chain) = Make(new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(5) });
        await H.Client(chain).GetAsync("/x");
        Assert.Equal("5000", inner.Requests[0].Header(Header));
    }

    [Fact]
    public async Task The_remaining_time_shrinks_as_the_clock_advances()
    {
        var (clock, inner, chain) = Make(new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(5) });
        using var invoker = new HttpMessageInvoker(chain);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.local/x");
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        clock.Advance(Ms(1500));
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        Assert.Equal(new[] { "5000", "3500" }, inner.Requests.Select(r => r.Header(Header)));
    }

    [Fact]
    public async Task The_remaining_time_is_rounded_up_to_whole_milliseconds()
    {
        var (clock, inner, chain) = Make(new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(1) });
        using var invoker = new HttpMessageInvoker(chain);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.local/x");
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        clock.Advance(TimeSpan.FromTicks(4000));
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        clock.Advance(Ms(10));
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        Assert.Equal(new[] { "1000", "1000", "990" }, inner.Requests.Select(r => r.Header(Header)));
    }

    [Fact]
    public async Task A_stale_header_on_the_request_is_overwritten()
    {
        var (_, inner, chain) = Make(new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(5) });
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.local/x");
        request.Headers.TryAddWithoutValidation(Header, "999999");
        using var invoker = new HttpMessageInvoker(chain);
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        Assert.Equal("5000", inner.Requests[0].Header(Header));
    }

    [Fact]
    public async Task An_expired_deadline_fails_before_the_inner_handler_is_called()
    {
        var (clock, inner, chain) = Make(new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(1) });
        using var invoker = new HttpMessageInvoker(chain);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.local/x");
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        clock.Advance(TimeSpan.FromSeconds(1));
        var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() => invoker.SendAsync(request, CancellationToken.None));
        Assert.Equal(TimeoutKind.Overall, ex.Kind);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task One_millisecond_before_the_deadline_the_call_is_still_made()
    {
        var (clock, inner, chain) = Make(new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(1) });
        using var invoker = new HttpMessageInvoker(chain);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.local/x");
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        clock.Advance(Ms(999));
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        Assert.Equal(2, inner.CallCount);
        Assert.Equal("1", inner.Requests[1].Header(Header));
    }

    [Fact]
    public async Task The_overall_deadline_firing_in_flight_is_an_overall_timeout()
    {
        var (clock, inner, chain) = Make(new TimeoutOptions { OverallTimeout = Ms(300) });
        inner.Hang();
        var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() => clock.Drive(H.Client(chain).GetAsync("/x")));
        Assert.Equal(TimeoutKind.Overall, ex.Kind);
        Assert.Equal(Ms(300), ex.Timeout);
    }

    [Fact]
    public async Task The_shorter_of_attempt_and_remaining_time_applies_and_is_labelled_overall()
    {
        var (clock, inner, chain) = Make(new TimeoutOptions { AttemptTimeout = TimeSpan.FromSeconds(5), OverallTimeout = TimeSpan.FromSeconds(2) });
        inner.Hang();
        var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() => clock.Drive(H.Client(chain).GetAsync("/x")));
        Assert.Equal(TimeoutKind.Overall, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(2), ex.Timeout);
    }

    [Fact]
    public async Task The_shorter_attempt_timeout_applies_and_is_labelled_attempt()
    {
        var (clock, inner, chain) = Make(new TimeoutOptions { AttemptTimeout = TimeSpan.FromSeconds(1), OverallTimeout = TimeSpan.FromSeconds(5) });
        inner.Hang();
        var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() => clock.Drive(H.Client(chain).GetAsync("/x")));
        Assert.Equal(TimeoutKind.Attempt, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(1), ex.Timeout);
    }

    [Fact]
    public async Task When_attempt_and_overall_are_equal_the_attempt_label_wins()
    {
        var (clock, inner, chain) = Make(new TimeoutOptions { AttemptTimeout = TimeSpan.FromSeconds(1), OverallTimeout = TimeSpan.FromSeconds(1) });
        inner.Hang();
        var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() => clock.Drive(H.Client(chain).GetAsync("/x")));
        Assert.Equal(TimeoutKind.Attempt, ex.Kind);
    }

    [Fact]
    public async Task The_earliest_deadline_wins_when_an_outer_one_is_shorter()
    {
        var (_, inner, chain) = Make(
            new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(2), PropagateDeadline = false },
            new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(10) });
        await H.Client(chain).GetAsync("/x");
        Assert.Equal("2000", inner.Requests[0].Header(Header));
    }

    [Fact]
    public async Task The_earliest_deadline_wins_when_an_inner_one_is_shorter()
    {
        var (_, inner, chain) = Make(
            new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(10), PropagateDeadline = false },
            new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(2) });
        await H.Client(chain).GetAsync("/x");
        Assert.Equal("2000", inner.Requests[0].Header(Header));
    }

    [Fact]
    public async Task Propagation_can_be_switched_off()
    {
        var (_, inner, chain) = Make(new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(5), PropagateDeadline = false });
        await H.Client(chain).GetAsync("/x");
        Assert.Null(inner.Requests[0].Header(Header));
    }

    [Fact]
    public async Task The_header_name_is_configurable()
    {
        var (_, inner, chain) = Make(new TimeoutOptions { OverallTimeout = TimeSpan.FromSeconds(5), HeaderName = "X-Budget" });
        await H.Client(chain).GetAsync("/x");
        Assert.Equal("5000", inner.Requests[0].Header("X-Budget"));
        Assert.Null(inner.Requests[0].Header(Header));
    }

    [Fact]
    public async Task An_attempt_timeout_alone_does_not_stamp_a_header()
    {
        var (_, inner, chain) = Make(new TimeoutOptions { AttemptTimeout = TimeSpan.FromSeconds(5) });
        await H.Client(chain).GetAsync("/x");
        Assert.Null(inner.Requests[0].Header(Header));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(null, 0)]
    [InlineData(null, -5)]
    public void Non_positive_limits_are_rejected(int? attemptMs, int? overallMs)
    {
        var options = new TimeoutOptions
        {
            AttemptTimeout = attemptMs is { } a ? Ms(a) : null,
            OverallTimeout = overallMs is { } o ? Ms(o) : null,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeoutHandler(options));
    }

    [Fact]
    public void Null_options_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new TimeoutHandler(null!));
    }
}
