namespace HttpResilience.Tests;

public class RetryBudgetTests
{
    private static RetryBudget Make(TestClock clock, double ratio, int floor, int windowSeconds = 10) =>
        new(new RetryBudgetOptions { RetryRatio = ratio, MinRetriesPerWindow = floor, Window = TimeSpan.FromSeconds(windowSeconds) }, clock);

    private static int Drain(RetryBudget budget)
    {
        var granted = 0;
        while (budget.TryAcquireRetry())
        {
            granted++;
            if (granted > 10_000)
            {
                throw new InvalidOperationException("runaway");
            }
        }

        return granted;
    }

    [Fact]
    public void Floor_alone_allows_that_many_retries()
    {
        var budget = Make(new TestClock(), ratio: 0, floor: 2);
        Assert.True(budget.TryAcquireRetry());
        Assert.True(budget.TryAcquireRetry());
        Assert.False(budget.TryAcquireRetry());
    }

    [Fact]
    public void Zero_ratio_and_zero_floor_never_allows_a_retry()
    {
        var budget = Make(new TestClock(), ratio: 0, floor: 0);
        for (var i = 0; i < 50; i++)
        {
            budget.RecordRequest();
        }

        Assert.False(budget.TryAcquireRetry());
    }

    [Theory]
    [InlineData(0, 0.2, 3, 3)]
    [InlineData(4, 0.2, 0, 0)]
    [InlineData(5, 0.2, 0, 1)]
    [InlineData(9, 0.2, 0, 1)]
    [InlineData(10, 0.2, 0, 2)]
    [InlineData(10, 0.2, 1, 3)]
    [InlineData(10, 0.5, 2, 7)]
    [InlineData(100, 0.1, 0, 10)]
    [InlineData(3, 0.1, 0, 0)]
    [InlineData(3, 1.0, 0, 3)]
    [InlineData(7, 0.3, 0, 2)]
    [InlineData(100, 0.29, 0, 29)]
    [InlineData(100, 0.57, 0, 57)]
    public void Allowed_retries_are_floor_plus_ratio_of_requests(int requests, double ratio, int floor, int expected)
    {
        var budget = Make(new TestClock(), ratio, floor);
        for (var i = 0; i < requests; i++)
        {
            budget.RecordRequest();
        }

        Assert.Equal(expected, Drain(budget));
    }

    [Fact]
    public void A_denied_acquire_takes_nothing()
    {
        var budget = Make(new TestClock(), ratio: 0, floor: 1);
        Assert.True(budget.TryAcquireRetry());
        Assert.False(budget.TryAcquireRetry());
        Assert.False(budget.TryAcquireRetry());
        Assert.Equal(1, budget.RetriesInWindow);
    }

    [Fact]
    public void Recording_more_requests_raises_the_allowance()
    {
        var budget = Make(new TestClock(), ratio: 0.5, floor: 0);
        Assert.False(budget.TryAcquireRetry());
        budget.RecordRequest();
        budget.RecordRequest();
        Assert.True(budget.TryAcquireRetry());
        Assert.False(budget.TryAcquireRetry());
        budget.RecordRequest();
        budget.RecordRequest();
        Assert.True(budget.TryAcquireRetry());
    }

    [Fact]
    public void Retries_leave_the_window_exactly_at_the_window_length()
    {
        var clock = new TestClock();
        var budget = Make(clock, ratio: 0, floor: 2);
        Assert.True(budget.TryAcquireRetry());
        Assert.True(budget.TryAcquireRetry());
        clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromMilliseconds(1));
        Assert.False(budget.TryAcquireRetry());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(budget.TryAcquireRetry());
    }

    [Fact]
    public void Window_slides_instead_of_resetting()
    {
        var clock = new TestClock();
        var budget = Make(clock, ratio: 0, floor: 2);
        Assert.True(budget.TryAcquireRetry());
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(budget.TryAcquireRetry());
        clock.Advance(TimeSpan.FromSeconds(4) - TimeSpan.FromMilliseconds(1));
        Assert.False(budget.TryAcquireRetry());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(budget.TryAcquireRetry());
        Assert.False(budget.TryAcquireRetry());
        Assert.Equal(2, budget.RetriesInWindow);
    }

    [Fact]
    public void Requests_age_out_of_the_window()
    {
        var clock = new TestClock();
        var budget = Make(clock, ratio: 0.2, floor: 0);
        for (var i = 0; i < 10; i++)
        {
            budget.RecordRequest();
        }

        Assert.Equal(10, budget.RequestsInWindow);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(10, budget.RequestsInWindow);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, budget.RequestsInWindow);
        Assert.False(budget.TryAcquireRetry());
    }

    [Fact]
    public void A_retry_storm_is_bounded_by_the_budget()
    {
        var budget = Make(new TestClock(), ratio: 0.1, floor: 5);
        var retries = 0;
        for (var request = 0; request < 1000; request++)
        {
            budget.RecordRequest();
            while (budget.TryAcquireRetry())
            {
                retries++;
            }
        }

        Assert.Equal(5 + 100, retries);
    }

    [Fact]
    public void Concurrent_acquires_never_exceed_the_allowance()
    {
        var budget = Make(new TestClock(), ratio: 0.2, floor: 5);
        for (var i = 0; i < 100; i++)
        {
            budget.RecordRequest();
        }

        var granted = 0;
        Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            if (budget.TryAcquireRetry())
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(25, granted);
        Assert.Equal(25, budget.RetriesInWindow);
    }

    [Theory]
    [InlineData(-0.1, 1, 10)]
    [InlineData(double.NaN, 1, 10)]
    [InlineData(0.2, -1, 10)]
    [InlineData(0.2, 1, 0)]
    public void Invalid_options_are_rejected(double ratio, int floor, int windowSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(new TestClock(), ratio, floor, windowSeconds));
    }

    [Fact]
    public void Defaults_are_usable()
    {
        var budget = new RetryBudget();
        Assert.True(budget.TryAcquireRetry());
    }
}
