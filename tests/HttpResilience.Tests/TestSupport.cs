using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace HttpResilience.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> that delegates to a <see cref="FakeTimeProvider"/> and tracks which timers are live,
/// so a test can say "advance to the next thing that would have happened" without ever sleeping.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<TrackedTimer> _timers = new();

    public TestClock(DateTimeOffset? start = null)
    {
        Fake = new FakeTimeProvider(start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    public FakeTimeProvider Fake { get; }

    public override long TimestampFrequency => Fake.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => Fake.LocalTimeZone;

    public int LiveTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Live);
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow();

    public override long GetTimestamp() => Fake.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TrackedTimer(this, callback, state, dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan by) => Fake.Advance(by);

    /// <summary>Time until the earliest live timer fires, or null if there is none.</summary>
    public TimeSpan? NextDue()
    {
        lock (_gate)
        {
            var live = _timers.Where(t => t.Live).Select(t => t.Due).ToList();
            if (live.Count == 0)
            {
                return null;
            }

            var next = live.Min() - Fake.GetUtcNow();
            return next < TimeSpan.Zero ? TimeSpan.Zero : next;
        }
    }

    /// <summary>
    /// Runs virtual time forward, always to the earliest live timer, until the task completes. Rethrows its exception.
    /// A real-time guard turns a hang into a failure.
    /// </summary>
    public async Task Drive(Task task)
    {
        var guard = Stopwatch.StartNew();
        while (true)
        {
            for (var i = 0; i < 30 && !task.IsCompleted; i++)
            {
                await Task.Yield();
            }

            if (task.IsCompleted)
            {
                break;
            }

            var next = NextDue();
            if (next is { } due)
            {
                Fake.Advance(due);
            }
            else
            {
                await Task.Yield();
            }

            if (guard.Elapsed > TimeSpan.FromSeconds(20))
            {
                throw new TimeoutException("Drive: the task did not complete.");
            }
        }

        await task;
    }

    public async Task<T> Drive<T>(Task<T> task)
    {
        await Drive((Task)task);
        return await task;
    }

    private sealed class TrackedTimer : ITimer
    {
        private readonly TestClock _clock;
        private readonly ITimer _inner;
        private TimeSpan _period;

        public TrackedTimer(TestClock clock, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _clock = clock;
            _period = period;
            Due = clock.Fake.GetUtcNow() + dueTime;
            Live = dueTime != Timeout.InfiniteTimeSpan;
            _inner = clock.Fake.CreateTimer(
                _ =>
                {
                    if (_period == Timeout.InfiniteTimeSpan)
                    {
                        Live = false;
                    }
                    else
                    {
                        Due = _clock.Fake.GetUtcNow() + _period;
                    }

                    callback(state);
                },
                null,
                dueTime,
                period);
        }

        public DateTimeOffset Due { get; private set; }

        public bool Live { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = _clock.Fake.GetUtcNow() + dueTime;
            Live = dueTime != Timeout.InfiniteTimeSpan;
            return _inner.Change(dueTime, period);
        }

        public void Dispose()
        {
            Live = false;
            _inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    IReadOnlyDictionary<string, string> Headers,
    byte[]? Body,
    DateTimeOffset At,
    IReadOnlyDictionary<string, object?> Options)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>A fake inner handler: plays back a script of responses, exceptions or custom steps, and records every request.</summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();
    private readonly List<RecordedRequest> _requests = new();
    private readonly TimeProvider _time;

    public ScriptedHandler(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _requests.Count;
            }
        }
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public ScriptedHandler Respond(HttpStatusCode code, Action<HttpResponseMessage>? configure = null, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            Run((_, _) =>
            {
                var response = new HttpResponseMessage(code);
                configure?.Invoke(response);
                return Task.FromResult(response);
            });
        }

        return this;
    }

    public ScriptedHandler Throw(Func<Exception> factory, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            Run((_, _) => Task.FromException<HttpResponseMessage>(factory()));
        }

        return this;
    }

    public ScriptedHandler Hang()
    {
        return Run(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
    }

    public ScriptedHandler Run(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step)
    {
        lock (_gate)
        {
            _script.Enqueue(step);
        }

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
        {
            headers[h.Key] = string.Join(",", h.Value);
        }

        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
            {
                headers[h.Key] = string.Join(",", h.Value);
            }
        }

        var options = new Dictionary<string, object?>(request.Options);
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? step = null;
        lock (_gate)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri, headers, body, _time.GetUtcNow(), options));
            if (_script.Count > 0)
            {
                step = _script.Dequeue();
            }
        }

        if (step is null)
        {
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        return await step(request, cancellationToken);
    }
}

public static class H
{
    public static HttpClient Client(HttpMessageHandler handler) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("http://test.local"), Timeout = Timeout.InfiniteTimeSpan };

    public static HttpMessageHandler Chain(HttpMessageHandler inner, params DelegatingHandler[] outerToInner)
    {
        var current = inner;
        for (var i = outerToInner.Length - 1; i >= 0; i--)
        {
            outerToInner[i].InnerHandler = current;
            current = outerToInner[i];
        }

        return current;
    }

    public static Func<double> Fixed(double value) => () => value;

    public static Func<double> Seeded(int seed)
    {
        var random = new Random(seed);
        return random.NextDouble;
    }

    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Yield();
        }
    }
}

/// <summary>An in-process Kestrel server on a random free port. The handler receives the 1-based hit number.</summary>
public sealed class TestUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _hits;

    private TestUpstream(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public int Hits => Volatile.Read(ref _hits);

    public static async Task<TestUpstream> StartAsync(Func<HttpContext, int, Task> handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        TestUpstream? self = null;
        app.Run(async ctx =>
        {
            var n = Interlocked.Increment(ref self!._hits);
            await handler(ctx, n);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new TestUpstream(app, new Uri(address));
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
