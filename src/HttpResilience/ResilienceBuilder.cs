using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience;

/// <summary>
/// Composes the handlers in a fixed order, outermost first, regardless of the order the With* methods are called:
/// <list type="number">
/// <item>overall deadline (TimeoutHandler, establishes the deadline and bounds everything inside, including queue waits and backoff)</item>
/// <item>bulkhead (bounds logical requests in flight; a retry reuses the slot)</item>
/// <item>retry (each attempt goes through the breaker)</item>
/// <item>circuit breaker (judges each attempt; an open circuit fast-fails and is not retried)</item>
/// <item>attempt timeout and deadline header (TimeoutHandler, stamps the remaining time on every attempt)</item>
/// </list>
/// State objects (budget, breaker, bulkhead) are created once by the builder and shared by every handler chain it
/// produces, so they survive IHttpClientFactory handler rotation.
/// </summary>
public sealed class ResilienceBuilder
{
    private readonly TimeProvider _time;
    private TimeSpan? _overall;
    private TimeSpan? _attempt;
    private Bulkhead? _bulkhead;
    private RetryOptions? _retry;
    private CircuitBreaker? _breaker;
    private Func<HttpResponseMessage, bool>? _breakerFailure;

    public ResilienceBuilder(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    public Bulkhead? Bulkhead => _bulkhead;

    public CircuitBreaker? Breaker => _breaker;

    public RetryBudget? Budget => _retry?.Budget;

    public ResilienceBuilder WithOverallDeadline(TimeSpan deadline)
    {
        _overall = deadline > TimeSpan.Zero ? deadline : throw new ArgumentOutOfRangeException(nameof(deadline));
        return this;
    }

    public ResilienceBuilder WithAttemptTimeout(TimeSpan timeout)
    {
        _attempt = timeout > TimeSpan.Zero ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));
        return this;
    }

    public ResilienceBuilder WithBulkhead(BulkheadOptions? options = null)
    {
        _bulkhead = new Bulkhead(options);
        return this;
    }

    public ResilienceBuilder WithRetry(Action<RetryOptions>? configure = null, RetryBudgetOptions? budget = null)
    {
        var options = new RetryOptions();
        configure?.Invoke(options);
        if (budget is not null)
        {
            options.Budget = new RetryBudget(budget, _time);
        }

        options.Validate();
        _retry = options;
        return this;
    }

    public ResilienceBuilder WithCircuitBreaker(CircuitBreakerOptions? options = null, Func<HttpResponseMessage, bool>? isFailureResponse = null)
    {
        _breaker = new CircuitBreaker(options, _time);
        _breakerFailure = isFailureResponse;
        return this;
    }

    /// <summary>Number of handlers in a chain.</summary>
    public int StageCount => StageNames().Count;

    /// <summary>Handler names, outermost first.</summary>
    public IReadOnlyList<string> StageNames()
    {
        var names = new List<string>();
        if (_overall is not null)
        {
            names.Add("overall-deadline");
        }

        if (_bulkhead is not null)
        {
            names.Add("bulkhead");
        }

        if (_retry is not null)
        {
            names.Add("retry");
        }

        if (_breaker is not null)
        {
            names.Add("circuit-breaker");
        }

        if (_attempt is not null || _overall is not null)
        {
            names.Add("attempt-timeout");
        }

        return names;
    }

    /// <summary>Create a new handler for stage <paramref name="index"/> (0 is outermost). Handlers are single-use per chain.</summary>
    public DelegatingHandler CreateHandler(int index)
    {
        var name = StageNames()[index];
        return name switch
        {
            "overall-deadline" => new TimeoutHandler(new TimeoutOptions { OverallTimeout = _overall, PropagateDeadline = false }, _time),
            "bulkhead" => new BulkheadHandler(_bulkhead!),
            "retry" => new RetryHandler(_retry, _time),
            "circuit-breaker" => new CircuitBreakerHandler(_breaker!, _breakerFailure),
            _ => new TimeoutHandler(new TimeoutOptions { AttemptTimeout = _attempt }, _time),
        };
    }

    public IReadOnlyList<DelegatingHandler> CreateHandlers()
    {
        var handlers = new List<DelegatingHandler>();
        for (var i = 0; i < StageCount; i++)
        {
            handlers.Add(CreateHandler(i));
        }

        return handlers;
    }

    /// <summary>Wrap <paramref name="inner"/> with a fresh chain and return its outermost handler.</summary>
    public HttpMessageHandler Build(HttpMessageHandler inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var handlers = CreateHandlers();
        var current = inner;
        for (var i = handlers.Count - 1; i >= 0; i--)
        {
            handlers[i].InnerHandler = current;
            current = handlers[i];
        }

        return current;
    }
}

public static class ResilienceHttpClientBuilderExtensions
{
    /// <summary>
    /// Adds the configured resilience handlers to a named or typed client. Set <c>HttpClient.Timeout</c> to
    /// <see cref="Timeout.InfiniteTimeSpan"/> on the client so it does not race the handlers.
    /// </summary>
    public static IHttpClientBuilder AddResilience(this IHttpClientBuilder builder, Action<ResilienceBuilder> configure, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var resilience = new ResilienceBuilder(timeProvider);
        configure(resilience);
        for (var i = 0; i < resilience.StageCount; i++)
        {
            var index = i;
            builder.AddHttpMessageHandler(_ => resilience.CreateHandler(index));
        }

        return builder;
    }
}
