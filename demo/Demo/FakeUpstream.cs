using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Demo;

/// <summary>
/// An in-process Kestrel server that misbehaves on a fixed schedule. Every scenario sees the same behaviour
/// per unit of wall-clock time, but the exact interleaving differs from run to run.
/// <list type="bullet">
/// <item>Time is cut into 1000 ms cycles. The first 600 ms are healthy (10-20 ms responses).</item>
/// <item>The last 400 ms are degraded: 45% 503, 15% 429 with Retry-After: 1, 20% slow (500 ms) 200, 20% healthy.</item>
/// <item>Always: more than 8 requests in flight means the request is shed with 503 (overload).</item>
/// </list>
/// The per-hit random draw is a pure function of (seed, hit number).
/// </summary>
public sealed class FakeUpstream : IAsyncDisposable
{
    public const int Capacity = 8;
    public const int CycleMs = 1000;
    public const int HealthyMs = 600;
    public const int SlowMs = 500;

    private readonly WebApplication _app;
    private readonly int _seed;
    private long _startTicks;
    private int _hits;
    private int _inFlight;
    private int _shed;

    private FakeUpstream(WebApplication app, int seed, Uri baseAddress)
    {
        _app = app;
        _seed = seed;
        BaseAddress = baseAddress;
        Reset();
    }

    public Uri BaseAddress { get; }

    public int Hits => Volatile.Read(ref _hits);

    public int Shed => Volatile.Read(ref _shed);

    /// <summary>Zero the counters and restart the flapping schedule.</summary>
    public void Reset()
    {
        Volatile.Write(ref _hits, 0);
        Volatile.Write(ref _shed, 0);
        Volatile.Write(ref _startTicks, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    public static async Task<FakeUpstream> StartAsync(int firstPort, int seed)
    {
        for (var port = firstPort; port <= 5129; port++)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            FakeUpstream? self = null;
            app.MapGet("/item", ctx => self!.HandleAsync(ctx));
            try
            {
                await app.StartAsync();
            }
            catch (IOException)
            {
                await app.DisposeAsync();
                continue;
            }

            self = new FakeUpstream(app, seed, new Uri($"http://127.0.0.1:{port}"));
            return self;
        }

        throw new InvalidOperationException("No free port in 5121-5129.");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var hit = Interlocked.Increment(ref _hits);
        var inFlight = Interlocked.Increment(ref _inFlight);
        try
        {
            if (inFlight > Capacity)
            {
                Interlocked.Increment(ref _shed);
                ctx.Response.StatusCode = 503;
                return;
            }

            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _startTicks)).TotalMilliseconds;
            var degraded = elapsedMs % CycleMs >= HealthyMs;
            var u = Draw(_seed, hit);
            if (degraded)
            {
                if (u < 0.45)
                {
                    ctx.Response.StatusCode = 503;
                    return;
                }

                if (u < 0.60)
                {
                    ctx.Response.StatusCode = 429;
                    ctx.Response.Headers["Retry-After"] = "1";
                    return;
                }

                if (u < 0.80)
                {
                    await Task.Delay(SlowMs, ctx.RequestAborted);
                    ctx.Response.StatusCode = 200;
                    return;
                }
            }

            await Task.Delay(10 + (int)(u * 10), ctx.RequestAborted);
            ctx.Response.StatusCode = 200;
        }
        catch (OperationCanceledException)
        {
            // The client gave up (timeout or cancellation); nothing to answer.
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>SplitMix64 of (seed, hit) mapped to [0,1).</summary>
    internal static double Draw(int seed, int hit)
    {
        var z = unchecked(((ulong)(uint)seed << 32) + (ulong)(uint)hit + 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }
}
