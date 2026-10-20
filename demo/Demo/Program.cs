using System.Diagnostics;
using System.Text;
using HttpResilience;

namespace Demo;

public sealed record ScenarioResult(
    string Name,
    int Requests,
    int Successes,
    int UpstreamHits,
    int Shed,
    string BreakerTransitions,
    double P50Ms,
    double P95Ms,
    IReadOnlyDictionary<string, int> Failures)
{
    public double SuccessRate => 100.0 * Successes / Requests;

    public double Amplification => (double)UpstreamHits / Requests;
}

public static class Program
{
    private static readonly string[] ScenarioNames =
    {
        "no policies",
        "retry only",
        "retry + budget",
        "full stack",
    };

    public static async Task<int> Main(string[] args)
    {
        var requests = IntArg(args, "--requests", 600);
        var rate = IntArg(args, "--rate", 200);
        var seed = IntArg(args, "--seed", 42);
        var runs = IntArg(args, "--runs", 1);
                var port = IntArg(args, "--port", 5121);

        await using var upstream = await FakeUpstream.StartAsync(port, seed);
        Console.WriteLine($"Fake upstream on {upstream.BaseAddress} (capacity {FakeUpstream.Capacity} in flight, 1000 ms cycle: 600 ms healthy, 400 ms degraded)");
        Console.WriteLine($"requests={requests} arrival-rate={rate}/s (open loop) seed={seed} runs={runs}");

        var all = new List<List<ScenarioResult>>();
        for (var run = 1; run <= runs; run++)
        {
            var results = new List<ScenarioResult>();
            foreach (var name in ScenarioNames)
            {
                results.Add(await RunScenario(name, upstream, requests, rate, seed));
            }

            all.Add(results);
            Console.WriteLine();
            Console.WriteLine($"Run {run} of {runs}");
            PrintTable(results);
        }

        if (runs > 1)
        {
            Console.WriteLine();
            PrintVariance(all);
        }

        return 0;
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) ? value : fallback;
    }

    private static (HttpClient client, Func<string> transitions) BuildClient(string scenario, Uri baseAddress, int seed)
    {
        var transitions = new List<string>();
        string Transitions()
        {
            lock (transitions)
            {
                return transitions.Count == 0
                    ? "0"
                    : $"{transitions.Count} ({string.Join(" ", transitions.GroupBy(t => t).Select(g => $"{g.Key}x{g.Count()}"))})";
            }
        }

        if (scenario == "no policies")
        {
            return (new HttpClient(new SocketsHttpHandler()) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(5) }, () => "n/a");
        }

        var random = new Random(seed);
        Action<RetryOptions> retry = o =>
        {
            o.MaxRetries = 3;
            o.BaseDelay = TimeSpan.FromMilliseconds(20);
            o.MaxDelay = TimeSpan.FromMilliseconds(200);
            o.MaxRetryAfter = TimeSpan.FromMilliseconds(100);
            o.RandomSource = random.NextDouble;
        };
        var budget = new RetryBudgetOptions { RetryRatio = 0.1, MinRetriesPerWindow = 5, Window = TimeSpan.FromSeconds(5) };
        var builder = new ResilienceBuilder();
        switch (scenario)
        {
            case "retry only":
                builder.WithRetry(retry);
                break;
            case "retry + budget":
                builder.WithRetry(retry, budget);
                break;
            default:
                builder
                    .WithOverallDeadline(TimeSpan.FromMilliseconds(1500))
                    .WithBulkhead(new BulkheadOptions { MaxConcurrency = 12, MaxQueue = 12 })
                    .WithRetry(retry, budget)
                    .WithCircuitBreaker(new CircuitBreakerOptions
                    {
                        WindowSize = 50,
                        MinimumCalls = 20,
                        FailureRateThreshold = 0.5,
                        OpenDuration = TimeSpan.FromMilliseconds(200),
                        HalfOpenPermits = 3,
                        OnStateChange = c =>
                        {
                            lock (transitions)
                            {
                                transitions.Add($"{Short(c.From)}>{Short(c.To)}");
                            }
                        },
                    })
                    .WithAttemptTimeout(TimeSpan.FromMilliseconds(250));
                break;
        }

        var client = new HttpClient(builder.Build(new SocketsHttpHandler())) { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };
        return (client, scenario == "full stack" ? Transitions : () => "n/a");
    }

    private static string Short(CircuitState s) => s switch
    {
        CircuitState.Closed => "C",
        CircuitState.Open => "O",
        _ => "H",
    };

    private static async Task<ScenarioResult> RunScenario(string name, FakeUpstream upstream, int requests, int rate, int seed)
    {
        upstream.Reset();
        var (client, transitions) = BuildClient(name, upstream.BaseAddress, seed);
        using var disposable = client;
        var latencies = new List<double>(requests);
        var failures = new Dictionary<string, int>();
        var successes = 0;

        async Task OneRequest()
        {
            {
                var sw = Stopwatch.StartNew();
                string? failure = null;
                try
                {
                    using var response = await client.GetAsync("/item");
                    if (!response.IsSuccessStatusCode)
                    {
                        failure = $"HTTP {(int)response.StatusCode}";
                    }
                }
                catch (BrokenCircuitException)
                {
                    failure = "circuit open";
                }
                catch (BulkheadRejectedException)
                {
                    failure = "bulkhead rejected";
                }
                catch (TimeoutRejectedException e)
                {
                    failure = $"{e.Kind.ToString().ToLowerInvariant()} timeout";
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    failure = e.GetType().Name;
                }

                sw.Stop();
                lock (latencies)
                {
                    latencies.Add(sw.Elapsed.TotalMilliseconds);
                    if (failure is null)
                    {
                        successes++;
                    }
                    else
                    {
                        failures[failure] = failures.GetValueOrDefault(failure) + 1;
                    }
                }
            }
        }

        var tasks = new List<Task>(requests);
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < requests; i++)
        {
            var wait = TimeSpan.FromMilliseconds(i * 1000.0 / rate) - clock.Elapsed;
            if (wait > TimeSpan.FromMilliseconds(1))
            {
                await Task.Delay(wait);
            }

            tasks.Add(Task.Run(OneRequest));
        }

        await Task.WhenAll(tasks);
        latencies.Sort();
        return new ScenarioResult(
            name,
            requests,
            successes,
            upstream.Hits,
            upstream.Shed,
            transitions(),
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            failures);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(p * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static void PrintTable(List<ScenarioResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| scenario       | client success | upstream hits | amplification | breaker transitions         | p50 ms | p95 ms |");
        sb.AppendLine("|----------------|---------------:|--------------:|--------------:|-----------------------------|-------:|-------:|");
        foreach (var r in results)
        {
            sb.AppendLine(
                $"| {r.Name,-14} | {r.SuccessRate,13:0.0}% | {r.UpstreamHits,13} | {r.Amplification,12:0.00}x | {r.BreakerTransitions,-27} | {r.P50Ms,6:0} | {r.P95Ms,6:0} |");
        }

        Console.Write(sb.ToString());
        Console.WriteLine();
        Console.WriteLine("Client-side failures by kind (upstream shed = requests the upstream rejected for overload):");
        foreach (var r in results)
        {
            var kinds = r.Failures.Count == 0
                ? "none"
                : string.Join(", ", r.Failures.OrderByDescending(f => f.Value).Select(f => $"{f.Key}: {f.Value}"));
            Console.WriteLine($"  {r.Name,-14} shed {r.Shed,4} | {kinds}");
        }
    }

    private static void PrintVariance(List<List<ScenarioResult>> all)
    {
        Console.WriteLine($"Variance over {all.Count} runs (same request count and seed; only timing differs)");
        Console.WriteLine("| scenario       | success % min / mean / max | amplification min / mean / max | p95 ms min / max | breaker transitions per run |");
        Console.WriteLine("|----------------|----------------------------|--------------------------------|------------------|-----------------------------|");
        for (var i = 0; i < ScenarioNames.Length; i++)
        {
            var rows = all.Select(run => run[i]).ToList();
            var success = rows.Select(r => r.SuccessRate).ToList();
            var amp = rows.Select(r => r.Amplification).ToList();
            var p95 = rows.Select(r => r.P95Ms).ToList();
            var transitions = ScenarioNames[i] == "full stack"
                ? string.Join(", ", rows.Select(r => r.BreakerTransitions.Split(' ')[0]))
                : "n/a";
            Console.WriteLine(
                $"| {ScenarioNames[i],-14} | {success.Min(),7:0.0} / {success.Average(),5:0.0} / {success.Max(),5:0.0}  | {amp.Min(),9:0.00} / {amp.Average(),5:0.00} / {amp.Max(),5:0.00}    | {p95.Min(),7:0} / {p95.Max(),6:0}  | {transitions,-27} |");
        }
    }
}
