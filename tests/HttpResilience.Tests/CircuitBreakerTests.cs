namespace HttpResilience.Tests;

public class CircuitBreakerTests
{
    private static CircuitBreaker Make(
        TestClock clock,
        int window = 10,
        double threshold = 0.5,
        int minCalls = 10,
        int openSeconds = 30,
        int probes = 3,
        Action<CircuitStateChange>? onChange = null) =>
        new(
            new CircuitBreakerOptions
            {
                WindowSize = window,
                FailureRateThreshold = threshold,
                MinimumCalls = minCalls,
                OpenDuration = TimeSpan.FromSeconds(openSeconds),
                HalfOpenPermits = probes,
                OnStateChange = onChange,
            },
            clock);

    private static void Outcome(CircuitBreaker breaker, bool success)
    {
        Assert.True(breaker.TryAcquire(out var permit, out _));
        if (success)
        {
            breaker.RecordSuccess(permit);
        }
        else
        {
            breaker.RecordFailure(permit);
        }
    }

    /// <summary>S = success, F = failure.</summary>
    private static void Run(CircuitBreaker breaker, string pattern)
    {
        foreach (var c in pattern)
        {
            Outcome(breaker, c == 'S');
        }
    }

    private static void Trip(CircuitBreaker breaker, int failures = 10)
    {
        Run(breaker, new string('F', failures));
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    private static CircuitPermit Acquire(CircuitBreaker breaker)
    {
        Assert.True(breaker.TryAcquire(out var permit, out _));
        return permit;
    }

    private static void ToHalfOpen(CircuitBreaker breaker, TestClock clock, int openSeconds = 30)
    {
        Trip(breaker);
        clock.Advance(TimeSpan.FromSeconds(openSeconds));
    }

    // ---------- closed ----------

    [Fact]
    public void Starts_closed_in_generation_zero()
    {
        var breaker = Make(new TestClock());
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.Generation);
        Assert.True(breaker.TryAcquire(out var permit, out var retryAfter));
        Assert.False(permit.IsProbe);
        Assert.Equal(TimeSpan.Zero, retryAfter);
    }

    [Fact]
    public void Does_not_open_below_the_minimum_number_of_calls()
    {
        var breaker = Make(new TestClock());
        Run(breaker, new string('F', 9));
        Assert.Equal(CircuitState.Closed, breaker.State);
        Run(breaker, "F");
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public void Opens_exactly_at_the_failure_rate_threshold()
    {
        var breaker = Make(new TestClock());
        Run(breaker, "SSSSSFFFF");
        Assert.Equal(CircuitState.Closed, breaker.State);
        Run(breaker, "F");
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public void Stays_closed_just_below_the_threshold()
    {
        var breaker = Make(new TestClock());
        Run(breaker, "SSSSSSFFFF");
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(4, breaker.FailuresInWindow);
        Assert.Equal(10, breaker.CallsInWindow);
    }

    [Theory]
    [InlineData(0.3, 3, true)]
    [InlineData(0.3, 2, false)]
    [InlineData(0.9, 9, true)]
    [InlineData(0.9, 8, false)]
    [InlineData(1.0, 10, true)]
    [InlineData(1.0, 9, false)]
    [InlineData(0.1, 1, true)]
    public void Threshold_boundaries(double threshold, int failuresInTen, bool shouldOpen)
    {
        var breaker = Make(new TestClock(), threshold: threshold);
        Run(breaker, new string('S', 10 - failuresInTen) + new string('F', failuresInTen));
        Assert.Equal(shouldOpen ? CircuitState.Open : CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void Minimum_calls_may_be_smaller_than_the_window()
    {
        var breaker = Make(new TestClock(), window: 20, minCalls: 5);
        Run(breaker, "FFFF");
        Assert.Equal(CircuitState.Closed, breaker.State);
        Run(breaker, "F");
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public void A_window_of_one_opens_on_the_first_failure()
    {
        var breaker = Make(new TestClock(), window: 1, minCalls: 1);
        Run(breaker, "S");
        Assert.Equal(CircuitState.Closed, breaker.State);
        Run(breaker, "F");
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public void Old_outcomes_slide_out_of_the_window()
    {
        var breaker = Make(new TestClock(), window: 4, minCalls: 4);
        Run(breaker, "FSSS");
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(1, breaker.FailuresInWindow);
        Run(breaker, "S");
        Assert.Equal(0, breaker.FailuresInWindow);
        Assert.Equal(4, breaker.CallsInWindow);
        Run(breaker, "F");
        Assert.Equal(CircuitState.Closed, breaker.State);
        Run(breaker, "F");
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public void Opening_clears_the_window_and_bumps_the_generation()
    {
        var breaker = Make(new TestClock());
        Trip(breaker);
        Assert.Equal(0, breaker.CallsInWindow);
        Assert.Equal(0, breaker.FailuresInWindow);
        Assert.Equal(1, breaker.Generation);
    }

    // ---------- open ----------

    [Fact]
    public void An_open_breaker_rejects_and_reports_the_time_left()
    {
        var clock = new TestClock();
        var breaker = Make(clock);
        Trip(breaker);
        Assert.False(breaker.TryAcquire(out _, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(30), retryAfter);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(breaker.TryAcquire(out _, out retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(20), retryAfter);
    }

    [Fact]
    public void The_open_duration_is_honoured_to_the_millisecond()
    {
        var clock = new TestClock();
        var breaker = Make(clock);
        Trip(breaker);
        clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1));
        Assert.False(breaker.TryAcquire(out _, out var retryAfter));
        Assert.Equal(TimeSpan.FromMilliseconds(1), retryAfter);
        Assert.Equal(CircuitState.Open, breaker.State);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(breaker.TryAcquire(out var permit, out _));
        Assert.True(permit.IsProbe);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
    }

    [Fact]
    public void The_state_stays_open_until_a_call_arrives()
    {
        var clock = new TestClock();
        var breaker = Make(clock);
        Trip(breaker);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    // ---------- half-open ----------

    [Fact]
    public void Half_open_admits_exactly_the_configured_number_of_probes()
    {
        var clock = new TestClock();
        var breaker = Make(clock);
        ToHalfOpen(breaker, clock);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(breaker.TryAcquire(out var permit, out _));
            Assert.True(permit.IsProbe);
        }

        Assert.Equal(3, breaker.ProbesIssued);
        Assert.False(breaker.TryAcquire(out _, out var retryAfter));
        Assert.Equal(TimeSpan.Zero, retryAfter);
        Assert.Equal(3, breaker.ProbesIssued);
    }

    [Fact]
    public void All_probes_succeeding_closes_the_breaker_with_a_fresh_window()
    {
        var clock = new TestClock();
        var breaker = Make(clock);
        ToHalfOpen(breaker, clock);
        var probes = new[] { Acquire(breaker), Acquire(breaker), Acquire(breaker) };
        breaker.RecordSuccess(probes[0]);
        breaker.RecordSuccess(probes[1]);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        breaker.RecordSuccess(probes[2]);
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(3, breaker.Generation);
        Assert.Equal(0, breaker.CallsInWindow);
        Run(breaker, new string('F', 9));
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void One_failed_probe_reopens_immediately_and_restarts_the_open_timer()
    {
        var clock = new TestClock();
        var breaker = Make(clock);
        ToHalfOpen(breaker, clock);
        var first = Acquire(breaker);
        var second = Acquire(breaker);
        breaker.RecordSuccess(first);
        breaker.RecordFailure(second);
        Assert.Equal(CircuitState.Open, breaker.State);
        clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1));
        Assert.False(breaker.TryAcquire(out _, out _));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(breaker.TryAcquire(out _, out _));
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
    }

    [Fact]
    public void A_released_probe_permit_can_be_used_again()
    {
        var clock = new TestClock();
        var breaker = Make(clock);
        ToHalfOpen(breaker, clock);
        var probes = new[] { Acquire(breaker), Acquire(breaker), Acquire(breaker) };
        Assert.False(breaker.TryAcquire(out _, out _));
        breaker.Release(probes[1]);
        Assert.Equal(2, breaker.ProbesIssued);
        Assert.True(breaker.TryAcquire(out var again, out _));
        Assert.True(again.IsProbe);
        Assert.Equal(3, breaker.ProbesIssued);
    }

    [Fact]
    public void Releasing_a_permit_in_the_closed_state_changes_nothing()
    {
        var breaker = Make(new TestClock());
        var permit = Acquire(breaker);
        breaker.Release(permit);
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.CallsInWindow);
        Assert.Equal(0, breaker.ProbesIssued);
    }

    // ---------- generations ----------

    [Fact]
    public void A_late_success_from_before_the_trip_does_not_count_as_a_probe()
    {
        var clock = new TestClock();
        var breaker = Make(clock);
        var stale = Acquire(breaker);
        ToHalfOpen(breaker, clock);
        var probe1 = Acquire(breaker);
        var probe2 = Acquire(breaker);
        var probe3 = Acquire(breaker);
        breaker.RecordSuccess(stale);
        breaker.RecordSuccess(probe1);
        breaker.RecordSuccess(probe2);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        breaker.RecordSuccess(probe3);
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void A_late_failure_from_before_the_trip_does_not_reopen_a_half_open_breaker()
    {
        var clock = new TestClock();
        var breaker = Make(clock);
        var stale = Acquire(breaker);
        ToHalfOpen(breaker, clock);
        var probe = Acquire(breaker);
        breaker.RecordFailure(stale);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        Assert.Equal(1, breaker.ProbesIssued);
        breaker.RecordSuccess(probe);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
    }

    [Fact]
    public void A_stale_release_does_not_free_a_current_probe_permit()
    {
        var clock = new TestClock();
        var breaker = Make(clock);
        var stale = Acquire(breaker);
        ToHalfOpen(breaker, clock);
        Acquire(breaker);
        Acquire(breaker);
        Acquire(breaker);
        breaker.Release(stale);
        Assert.Equal(3, breaker.ProbesIssued);
        Assert.False(breaker.TryAcquire(out _, out _));
    }

    [Fact]
    public void A_stale_probe_permit_from_an_earlier_half_open_phase_does_not_free_a_current_probe()
    {
        var clock = new TestClock();
        var breaker = Make(clock);
        ToHalfOpen(breaker, clock);
        var oldProbe1 = Acquire(breaker);
        var oldProbe2 = Acquire(breaker);
        breaker.RecordFailure(oldProbe1);
        Assert.Equal(CircuitState.Open, breaker.State);
        clock.Advance(TimeSpan.FromSeconds(30));
        var current = Acquire(breaker);
        Assert.True(current.IsProbe);
        Assert.Equal(1, breaker.ProbesIssued);
        breaker.Release(oldProbe2);
        Assert.Equal(1, breaker.ProbesIssued);
        breaker.RecordSuccess(oldProbe2);
        breaker.RecordFailure(oldProbe2);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        Assert.Equal(1, breaker.ProbesIssued);
    }

    [Fact]
    public void A_late_outcome_after_a_full_cycle_is_ignored_by_the_new_closed_window()
    {
        var clock = new TestClock();
        var breaker = Make(clock);
        var stale = Acquire(breaker);
        ToHalfOpen(breaker, clock);
        foreach (var probe in new[] { Acquire(breaker), Acquire(breaker), Acquire(breaker) })
        {
            breaker.RecordSuccess(probe);
        }

        Assert.Equal(CircuitState.Closed, breaker.State);
        breaker.RecordFailure(stale);
        Assert.Equal(0, breaker.CallsInWindow);
        Assert.Equal(0, breaker.FailuresInWindow);
    }

    [Fact]
    public void A_late_outcome_while_open_is_ignored()
    {
        var breaker = Make(new TestClock());
        var stale = Acquire(breaker);
        Trip(breaker);
        breaker.RecordSuccess(stale);
        breaker.RecordFailure(stale);
        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.Equal(1, breaker.Generation);
    }

    [Fact]
    public void A_current_generation_permit_is_counted()
    {
        var breaker = Make(new TestClock(), window: 2, minCalls: 2);
        var first = Acquire(breaker);
        var second = Acquire(breaker);
        breaker.RecordFailure(first);
        Assert.Equal(1, breaker.FailuresInWindow);
        breaker.RecordFailure(second);
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    // ---------- callbacks ----------

    [Fact]
    public void State_changes_are_reported_in_order_with_generations()
    {
        var clock = new TestClock();
        var changes = new List<CircuitStateChange>();
        var breaker = Make(clock, onChange: changes.Add);
        ToHalfOpen(breaker, clock);
        foreach (var _ in Enumerable.Range(0, 3))
        {
            Outcome(breaker, success: true);
        }

        Assert.Equal(
            new[]
            {
                new CircuitStateChange(CircuitState.Closed, CircuitState.Open, 1),
                new CircuitStateChange(CircuitState.Open, CircuitState.HalfOpen, 2),
                new CircuitStateChange(CircuitState.HalfOpen, CircuitState.Closed, 3),
            },
            changes);
    }

    [Fact]
    public void A_failed_probe_reports_half_open_to_open()
    {
        var clock = new TestClock();
        var changes = new List<CircuitStateChange>();
        var breaker = Make(clock, onChange: changes.Add);
        ToHalfOpen(breaker, clock);
        Outcome(breaker, success: false);
        Assert.Equal(new CircuitStateChange(CircuitState.HalfOpen, CircuitState.Open, 3), changes[^1]);
    }

    [Fact]
    public void No_callback_fires_when_nothing_changes()
    {
        var changes = new List<CircuitStateChange>();
        var breaker = Make(new TestClock(), onChange: changes.Add);
        Run(breaker, "SSSSFFFFS");
        Assert.Empty(changes);
    }

    [Fact]
    public void A_throwing_callback_does_not_break_the_breaker()
    {
        var breaker = Make(new TestClock(), onChange: _ => throw new InvalidOperationException("observer bug"));
        Trip(breaker);
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public void Callbacks_run_outside_the_breaker_lock()
    {
        CircuitBreaker? breaker = null;
        var reached = false;
        breaker = Make(new TestClock(), onChange: _ => reached = Task.Run(() => breaker!.State).Wait(TimeSpan.FromSeconds(5)));
        Trip(breaker);
        Assert.True(reached);
    }

    // ---------- options ----------

    [Theory]
    [InlineData(0, 0.5, 1, 30, 1)]
    [InlineData(10, 0.0, 5, 30, 1)]
    [InlineData(10, 1.1, 5, 30, 1)]
    [InlineData(10, double.NaN, 5, 30, 1)]
    [InlineData(10, 0.5, 0, 30, 1)]
    [InlineData(10, 0.5, 11, 30, 1)]
    [InlineData(10, 0.5, 5, 0, 1)]
    [InlineData(10, 0.5, 5, 30, 0)]
    public void Invalid_options_are_rejected(int window, double threshold, int minCalls, int openSeconds, int probes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CircuitBreaker(new CircuitBreakerOptions
            {
                WindowSize = window,
                FailureRateThreshold = threshold,
                MinimumCalls = minCalls,
                OpenDuration = TimeSpan.FromSeconds(openSeconds),
                HalfOpenPermits = probes,
            }));
    }

    [Fact]
    public void Defaults_are_valid()
    {
        var breaker = new CircuitBreaker();
        Assert.Equal(CircuitState.Closed, breaker.State);
    }
}
