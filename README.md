# dotnet-http-resilience

HTTP client resilience written by hand as `DelegatingHandler`s in C# on .NET 10, to understand what Polly and
Microsoft.Extensions.Http.Resilience do under the hood. It has a retry handler (with a retry budget, full jitter and
`Retry-After`), a circuit breaker, a timeout handler with a propagated deadline, a bulkhead, and a small builder that
composes them in a documented order. A console demo runs the policies against an in-process Kestrel upstream that
flaps, goes slow and sheds load.

This is a learning and showcase project. It does not use Polly or Microsoft.Extensions.Http.Resilience and it does
not claim feature or behaviour parity with either.

CI ran green on the first run on GitHub Actions (ubuntu-latest, 2026-10-20). Tested locally only on Windows 11 with .NET SDK 10.0.402.

## Usage

Hand-built chain around any `HttpMessageHandler`:

```csharp
using HttpResilience;

var resilience = new ResilienceBuilder()
    .WithOverallDeadline(TimeSpan.FromSeconds(5))                  // whole request, incl. queueing and backoff
    .WithBulkhead(new BulkheadOptions { MaxConcurrency = 20, MaxQueue = 20 })
    .WithRetry(o => { o.MaxRetries = 3; o.BaseDelay = TimeSpan.FromMilliseconds(100); },
               budget: new RetryBudgetOptions { RetryRatio = 0.1, MinRetriesPerWindow = 5 })
    .WithCircuitBreaker(new CircuitBreakerOptions { OnStateChange = c => Console.WriteLine($"{c.From} -> {c.To}") })
    .WithAttemptTimeout(TimeSpan.FromSeconds(1));

var client = new HttpClient(resilience.Build(new SocketsHttpHandler()))
{
    Timeout = Timeout.InfiniteTimeSpan,   // let the handlers own timeouts, see "Known limits"
};
```

With `IHttpClientFactory` (breaker, bulkhead and budget are created once and shared by every handler chain, so they
survive the factory's handler rotation):

```csharp
services.AddHttpClient("orders", c => { c.BaseAddress = new Uri("https://orders.internal"); c.Timeout = Timeout.InfiniteTimeSpan; })
        .AddResilience(b => b
            .WithOverallDeadline(TimeSpan.FromSeconds(5))
            .WithRetry(budget: new RetryBudgetOptions())
            .WithCircuitBreaker()
            .WithAttemptTimeout(TimeSpan.FromSeconds(1)));
```

Handlers can also be used on their own (`new RetryHandler(options, timeProvider) { InnerHandler = ... }`), and
`CircuitBreaker`, `Bulkhead` and `RetryBudget` are plain classes with no HTTP dependency.

## Composition order

`ResilienceBuilder` always builds the chain in this order, outermost first, whatever order you call the `With*` methods:

| # | Stage | Why it sits here |
|---|-------|------------------|
| 1 | overall deadline (`TimeoutHandler`) | Creates the deadline, so queue waits and backoff delays count against it. |
| 2 | `BulkheadHandler` | Bounds logical requests in flight. A retry reuses the slot, so retries cannot multiply concurrency. |
| 3 | `RetryHandler` | Every attempt goes through the breaker. |
| 4 | `CircuitBreakerHandler` | Judges each attempt. An open circuit fast-fails with `BrokenCircuitException`, which is not retried. |
| 5 | attempt timeout (`TimeoutHandler`) | Bounds each attempt and stamps the remaining deadline on it. |

Trade-off of this order: a request waiting in the bulkhead queue is not yet counted by the breaker, and the slot is
held during backoff sleeps. Putting the bulkhead inside the retry handler would free the slot while sleeping but lets
retries add concurrency.

## Design notes

### RetryHandler
- Retries only idempotent methods (GET, HEAD, PUT, DELETE, OPTIONS) or POST with a non-empty `Idempotency-Key`
  header. PATCH and everything else is sent once.
- Retries on 408, 429, any 5xx (a simplification: 501 and 505 are not really transient), `HttpRequestException`,
  per-attempt timeouts (`TimeoutRejectedException` with `Kind == Attempt`) and cancellations that did not come from
  the caller. Never retries caller cancellation, `BrokenCircuitException`, `BulkheadRejectedException` or an overall
  deadline timeout.
- Backoff: full jitter, `delay = random(0, min(MaxDelay, BaseDelay * 2^retryIndex))`. The random source is injected
  (`RetryOptions.RandomSource`), so tests are seeded. Out-of-range values are clamped to [0,1].
- `Retry-After`: integer seconds or an IMF-fixdate. Negative, garbage and empty values are ignored; a date in the
  past is zero; huge values are capped by `MaxRetryAfter`. The delay is `max(jittered backoff, capped Retry-After)`.
- Retry budget (`RetryBudget`): in a sliding window (on `TimeProvider`), retries may be at most
  `MinRetriesPerWindow + floor(RetryRatio * requests)`. First attempts are never limited; a denied retry returns
  the last response or rethrows the last exception. The window keeps one timestamp per event, so memory is
  proportional to traffic in one window.
- Deadline aware: no retry if the remaining overall deadline is not longer than the next delay.
- Request bodies are buffered (up to `MaxBufferedContentBytes`, default 1 MiB) and each attempt gets a cloned request
  with the same headers and options. Larger bodies or bodies of unknown length are sent once, without retries.
  Failed responses are disposed before the retry.

### CircuitBreakerHandler / CircuitBreaker
- Count-based sliding window of the last `WindowSize` outcomes. Opens when `failures / calls >= FailureRateThreshold`
  and at least `MinimumCalls` outcomes are in the window.
- Open for `OpenDuration`, then half-open on the next call. Half-open admits `HalfOpenPermits` probes; all must
  succeed to close, one failure re-opens (and restarts the open timer). Extra calls are rejected.
- Generation counter: every transition bumps it, permits carry it, and outcomes or releases with an older
  generation are ignored. A slow request that started before the breaker tripped cannot count as a probe later.
- Failures: 5xx, 408, `HttpRequestException`, attempt timeouts and cancellations not caused by the caller. Caller
  cancellation and unrelated exceptions return the permit without recording anything. Overall-deadline timeouts are
  not counted against the dependency. Override with a predicate for responses.
- State-change callback runs outside the lock; exceptions from it are swallowed. State is only re-evaluated when a
  call arrives, so `State` can read `Open` after the open duration has passed.

### TimeoutHandler
- Per-attempt timeout and/or an overall deadline, both on `TimeProvider`. The deadline lives in
  `HttpRequestMessage.Options`, so retries share it, and the earliest deadline wins if several are set.
- The remaining time is written to `X-Request-Deadline-Ms` (whole milliseconds, rounded up) on every attempt. If the
  deadline has already passed, the call fails before the inner handler runs.
- A timeout throws `TimeoutRejectedException` (a `TimeoutException`) with `Kind` Attempt or Overall. Caller
  cancellation surfaces as `OperationCanceledException` and wins if both happen at once.

### BulkheadHandler / Bulkhead
- Max concurrency plus a bounded FIFO wait queue. A released slot is handed directly to the head of the queue (no
  barging). When the queue is full: `BulkheadRejectedException`. Cancelled waiters leave the queue; a cancel that
  races with a grant still returns the lease to the caller, so no permit leaks. Leases are idempotent to dispose.

## Demo

```
dotnet run --project demo/Demo -- --requests 600 --rate 200 --runs 5
```

Options: `--requests N` (default 600), `--rate N` requests per second, open loop (default 200), `--seed N` (default
42), `--runs N` (default 1), `--port N` (default 5121; tries up to 5129).

The fake upstream (Kestrel on 127.0.0.1:5121) works on a 1000 ms cycle: 600 ms healthy (10-20 ms responses), then
400 ms degraded (45% 503, 15% 429 with `Retry-After: 1`, 20% slow 500 ms responses, 20% healthy). Independently,
more than 8 requests in flight are shed with 503. The per-hit random draw is a pure function of (seed, hit number),
but which request gets which hit number depends on timing. Client: open loop, so every scenario sees the same
arrival schedule; "success" is a 2xx after all policies; amplification is upstream hits divided by client requests.

Scenarios: `no policies` (plain HttpClient); `retry only` (3 retries, base 20 ms, cap 200 ms, `Retry-After` capped
at 100 ms); `retry + budget` (same, budget 10% + 5 per 5 s); `full stack` (overall deadline 1500 ms, bulkhead 12+12,
retry + budget, breaker window 50 / min 20 / 50% / open 200 ms / 3 probes, attempt timeout 250 ms).

Real output of the command above (Windows 11, Debug build, one machine, 2026-10-20):

```
Fake upstream on http://127.0.0.1:5121/ (capacity 8 in flight, 1000 ms cycle: 600 ms healthy, 400 ms degraded)
requests=600 arrival-rate=200/s (open loop) seed=42 runs=5

Run 1 of 5
| scenario       | client success | upstream hits | amplification | breaker transitions         | p50 ms | p95 ms |
|----------------|---------------:|--------------:|--------------:|-----------------------------|-------:|-------:|
| no policies    |          45.5% |           600 |         1.00x | n/a                         |     16 |    362 |
| retry only     |          60.7% |          1457 |         2.43x | n/a                         |     51 |    193 |
| retry + budget |          57.7% |           665 |         1.11x | n/a                         |     16 |    127 |
| full stack     |          49.0% |           418 |         0.70x | 13 (C>Ox3 O>Hx5 H>Ox2 H>Cx3) |     16 |     66 |

Client-side failures by kind (upstream shed = requests the upstream rejected for overload):
  no policies    shed  223 | HTTP 503: 302, HTTP 429: 25
  retry only     shed 1014 | HTTP 503: 235, HTTP 429: 1
  retry + budget shed  241 | HTTP 503: 246, HTTP 429: 8
  full stack     shed   21 | circuit open: 247, HTTP 503: 45, HTTP 429: 9, attempt timeout: 5

Run 2 of 5
| scenario       | client success | upstream hits | amplification | breaker transitions         | p50 ms | p95 ms |
|----------------|---------------:|--------------:|--------------:|-----------------------------|-------:|-------:|
| no policies    |          59.2% |           600 |         1.00x | n/a                         |     16 |     44 |
| retry only     |          62.3% |          1449 |         2.42x | n/a                         |     48 |    160 |
| retry + budget |          57.2% |           665 |         1.11x | n/a                         |     17 |     81 |
| full stack     |          50.0% |           422 |         0.70x | 14 (C>Ox3 O>Hx6 H>Ox3 H>Cx2) |     19 |     88 |

Client-side failures by kind (upstream shed = requests the upstream rejected for overload):
  no policies    shed  154 | HTTP 503: 222, HTTP 429: 23
  retry only     shed  996 | HTTP 503: 226
  retry + budget shed  254 | HTTP 503: 247, HTTP 429: 10
  full stack     shed   25 | circuit open: 243, HTTP 503: 45, HTTP 429: 7, attempt timeout: 5

Run 3 of 5
| scenario       | client success | upstream hits | amplification | breaker transitions         | p50 ms | p95 ms |
|----------------|---------------:|--------------:|--------------:|-----------------------------|-------:|-------:|
| no policies    |          58.3% |           600 |         1.00x | n/a                         |     17 |     41 |
| retry only     |          61.7% |          1426 |         2.38x | n/a                         |     46 |    190 |
| retry + budget |          56.0% |           665 |         1.11x | n/a                         |     18 |     75 |
| full stack     |          53.5% |           446 |         0.74x | 13 (C>Ox3 O>Hx5 H>Ox3 H>Cx2) |     17 |     65 |

Client-side failures by kind (upstream shed = requests the upstream rejected for overload):
  no policies    shed  155 | HTTP 503: 228, HTTP 429: 22
  retry only     shed  962 | HTTP 503: 229, HTTP 429: 1
  retry + budget shed  259 | HTTP 503: 253, HTTP 429: 11
  full stack     shed   14 | circuit open: 219, HTTP 503: 44, HTTP 429: 10, attempt timeout: 6

Run 4 of 5
| scenario       | client success | upstream hits | amplification | breaker transitions         | p50 ms | p95 ms |
|----------------|---------------:|--------------:|--------------:|-----------------------------|-------:|-------:|
| no policies    |          59.2% |           600 |         1.00x | n/a                         |     17 |     47 |
| retry only     |          62.2% |          1468 |         2.45x | n/a                         |     54 |    210 |
| retry + budget |          56.7% |           665 |         1.11x | n/a                         |     17 |    125 |
| full stack     |          49.5% |           415 |         0.69x | 13 (C>Ox3 O>Hx5 H>Ox3 H>Cx2) |     16 |    111 |

Client-side failures by kind (upstream shed = requests the upstream rejected for overload):
  no policies    shed  147 | HTTP 503: 220, HTTP 429: 25
  retry only     shed 1003 | HTTP 503: 226, HTTP 429: 1
  retry + budget shed  245 | HTTP 503: 251, HTTP 429: 9
  full stack     shed   19 | circuit open: 241, HTTP 503: 42, bulkhead rejected: 7, HTTP 429: 7, attempt timeout: 6

Run 5 of 5
| scenario       | client success | upstream hits | amplification | breaker transitions         | p50 ms | p95 ms |
|----------------|---------------:|--------------:|--------------:|-----------------------------|-------:|-------:|
| no policies    |          58.5% |           600 |         1.00x | n/a                         |     17 |     41 |
| retry only     |          62.3% |          1469 |         2.45x | n/a                         |     58 |    227 |
| retry + budget |          53.2% |           665 |         1.11x | n/a                         |     18 |    114 |
| full stack     |          49.8% |           428 |         0.71x | 13 (C>Ox3 O>Hx5 H>Ox3 H>Cx2) |     19 |     75 |

Client-side failures by kind (upstream shed = requests the upstream rejected for overload):
  no policies    shed  157 | HTTP 503: 227, HTTP 429: 22
  retry only     shed 1018 | HTTP 503: 226
  retry + budget shed  272 | HTTP 503: 270, HTTP 429: 11
  full stack     shed   23 | circuit open: 236, HTTP 503: 50, HTTP 429: 9, attempt timeout: 6

Variance over 5 runs (same request count and seed; only timing differs)
| scenario       | success % min / mean / max | amplification min / mean / max | p95 ms min / max | breaker transitions per run |
|----------------|----------------------------|--------------------------------|------------------|-----------------------------|
| no policies    |    45.5 /  56.1 /  59.2  |      1.00 /  1.00 /  1.00    |      41 /    362  | n/a                         |
| retry only     |    60.7 /  61.8 /  62.3  |      2.38 /  2.42 /  2.45    |     160 /    227  | n/a                         |
| retry + budget |    53.2 /  56.1 /  57.7  |      1.11 /  1.11 /  1.11    |      75 /    127  | n/a                         |
| full stack     |    49.0 /  50.4 /  53.5  |      0.69 /  0.71 /  0.74    |      65 /    111  | 13, 14, 13, 13, 13          |
```

How to read it, honestly:

- Retry only more than doubles the load on the struggling upstream (about 2.4x hits, about 1000 requests shed for
  overload against about 150 with no policies) for a gain of roughly 5 points of success. That is the retry storm
  the budget exists for.
- With the budget, amplification is 1.11x in every run. That number is not a measurement of variance: the budget
  (5 + 10% of 600 = 65 retries) is simply used up in every run. Success was about the same as no policies.
- The full stack has the lowest amplification (about 0.7x, because the breaker rejects roughly 240 requests locally)
  and a lower p95 than retry only (similar to retry + budget), but the lowest success rate of the policy scenarios. It
  trades availability for protecting the upstream and failing fast. The demo does not show the full stack
  "winning" on success.
- The breaker is not rare here: it opened about 3 times per run (13 or 14 transitions). That is because the
  upstream flaps on a fixed 1 s schedule and a run covers about 3 cycles, which is also why the transition counts
  look almost identical across runs. A less hostile upstream could leave it closed; I did not try that.
- Numbers vary run to run. Run 1 of `no policies` (45.5% success, p95 362 ms) is an outlier against the other four
  (58 to 59%); I did not investigate it (shared machine, Debug build).
- The parameters are hand-chosen. The breaker settings were changed once after looking at output (window 20 / min 10 /
  open 300 ms became 50 / 20 / 200 ms), and an earlier closed-loop version of the load generator was dropped because
  fast-failing scenarios consumed the fixed request count in a different time window. Treat this as an illustration
  of mechanisms, not as a benchmark.

## How it is tested

`dotnet test`: 311 xunit test cases (each theory row counts), all passing on the author's machine. By area:
retry handler 106 (including jitter bounds, seed determinism, `Retry-After` edge cases, budget exhaustion, body
re-send, deadline shrinking across retries, cancellation versus timeout), circuit breaker state machine 44 and handler
24 (exact threshold, minimum calls, half-open probe accounting, stale generations), timeout handler 28, bulkhead 23
(FIFO order, no barging, rejection, cancellation), `Retry-After` parser 32, retry budget 27, builder and
`IHttpClientFactory` 11, Kestrel end-to-end 11, thread-safety stress 5.

- Unit tests are deterministic: everything time-based takes a `TimeProvider`; tests use
  `Microsoft.Extensions.TimeProvider.Testing`'s `FakeTimeProvider` (wrapped by `TestClock`, which tracks live timers so
  a test can advance to the next timer without sleeping) and a scripted fake inner handler.
- End-to-end tests start a real Kestrel server on a random free port and use small real timeouts (about 150 ms), so
  those do take real time.
- Concurrency tests are barrier based (semaphores and task gates, no sleeps). Stress tests assert invariants:
  concurrent holders never exceed the bulkhead limit, no permit is lost, a breaker generation never issues more probes
  than permits, and the retry budget never grants more than it allows.

### Mutation check

Tool: first `Stryker.NET` 5.0.0 (installed as a local dotnet tool). It runs on .NET 10, but in "Safe Mode" it removed
every mutant inside every `SendAsync` (one mutation produced CS0165 in the circuit breaker handler), which is the core
logic of four handlers, and after about 22 minutes it had printed no results and three runners were logging "Retrying
the test session". I stopped it at my time limit. There is no Stryker score in this repo.

Instead `scripts/mutation_check.py` applies 38 hand-written single textual replacements, one at a time (each must match
exactly once), runs `dotnet test` per mutant (per-mutant timeout 240 s plus `--blame-hang-timeout 60s`), restores the file
in a `finally` block, and compares `src/` with a snapshot at the end. `python scripts/mutation_check.py` takes roughly 10
to 15 minutes. Before and after the runs `src/` was also compared with a separate backup copy: identical.

First run (before I added any test for it): 29 killed, 5 survived, 4 compile errors. Three kills (K01, K02, K03) came only
from the 240 s timeout because the mutant made some tests hang. The four compile errors were my fault: one typo in a
mutation (missing parenthesis) and three mutants that only produce warnings, which `TreatWarningsAsErrors` turned into
errors; fixed by passing `-p:TreatWarningsAsErrors=false` for mutant runs. Survivors, decided one by one:

| Survivor | Verdict |
|----------|---------|
| B04 epsilon removed from the budget allowance | Test gap: no test had a ratio times request count that is just below an integer in floating point. Added cases 0.29 x 100 and 0.57 x 100. |
| H03 overall-deadline timeout counted by the breaker | Test gap. Added a handler test. |
| T05 caller cancellation reported as timeout when both happen | Test gap. Added a test that cancels the caller and fires the timer in the same inner call. |
| R13 `IsTransient` guard `!callerToken.IsCancellationRequested` | Equivalent: the `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)` clause before it already rethrows every cancellation when the caller token is cancelled, so both versions return the same value whenever the guard is reached (it differs only in a race between the two reads). Kept as defence in depth. |
| P01 `seconds >= max` became `seconds > max` | Equivalent: at `seconds == max` the other branch returns `TimeSpan.FromSeconds(seconds)`, which equals the cap. |

C04 (stale `Release` frees a current probe permit) could not be evaluated in the first run (compile error). Reading the
code, I judged that my existing stale-release test used a closed-era permit, which cannot expose that mutation, and added a
test with a stale probe permit from an earlier half-open phase before the second run. I did not check that C04 would have
survived without the new test.

Final run (311 tests, current code): **36 of 38 killed (94.7%), 2 survived, both equivalent mutants** (R13, P01); 36 of 36
non-equivalent mutants killed. No kill in this run is counted from a timeout. For K01, K02 and K03 the run still hit the
hang timeout because some tests hang under those mutants, but at least one test also failed by assertion, which is what
the table shows. This is 38 hand-picked mutants in the places I considered risky, not a systematic operator set, so the
score says less than a Stryker score would.

| # | Mutation | Result | Caught by (first failing tests) |
|---|----------|--------|---------------------------------|
| R01 | HTTP 500 no longer retried (`>=` became `>`) | killed | Retryable_statuses_are_retried |
| R03 | blank Idempotency-Key accepted as a key | killed | Post_with_a_blank_idempotency_key_is_not_retried |
| R04 | OPTIONS no longer retryable | killed | Idempotent_methods_are_retried |
| R05 | body exactly at the buffer limit not buffered | killed | A_body_exactly_at_the_limit_is_still_buffered |
| R06 | jitter removed (always full ceiling) | killed | Seeded_jitter_always_stays_within_zero_and_the_ceiling |
| R07 | random source not clamped to [0,1] | killed | A_random_above_one_is_clamped_to_the_ceiling |
| R08 | `Retry-After` used only when shorter than backoff (max became min) | killed | Retry_after_on_429_is_honoured_too |
| R09 | retry allowed when remaining deadline equals the delay | killed | No_retry_when_the_remaining_deadline_equals_the_next_delay |
| R10 | one retry too many (`<` became `<=`) | killed | Zero_max_retries_never_retries |
| R11 | retry budget ignored | killed | An_exhausted_budget_stops_retries_across_requests |
| R13 | IsTransient treats caller cancellation as transient | survived (equivalent) | - |
| R14 | overall-deadline timeouts retried too | killed | Overall_timeout_exception_is_not_retried |
| R19 | backoff cap removed | killed | Max_delay_caps_the_exponential_growth |
| P01 | `Retry-After` seconds equal to cap (`>=` became `>`) | survived (equivalent) | - |
| P03 | `Retry-After` HTTP-date not capped | killed | Http_date_far_in_the_future_is_capped |
| B01 | budget off by one | killed | Allowed_retries_are_floor_plus_ratio_of_requests |
| B02 | request events leave the window one tick late | killed | Requests_age_out_of_the_window |
| B04 | epsilon removed from allowance | killed (after new test) | Allowed_retries_are_floor_plus_ratio_of_requests |
| C01 | opens only above the threshold | killed | Opens_exactly_at_the_failure_rate_threshold |
| C02 | minimum calls off by one | killed | All_probes_succeeding_closes_the_breaker_with_a_fresh_window |
| C03 | stale-generation outcomes are counted | killed | A_late_outcome_after_a_full_cycle_is_ignored_by_the_new_closed_window |
| C04 | stale `Release` frees a current probe permit | killed (new test) | A_stale_probe_permit_from_an_earlier_half_open_phase_does_not_free_a_current_probe |
| C05 | first successful probe closes the breaker | killed | All_probes_succeeding_closes_the_breaker_with_a_fresh_window |
| C06 | one probe too many admitted | killed | Half_open_admits_exactly_the_configured_number_of_probes |
| C08 | open timer not restarted on re-open | killed | An_open_breaker_rejects_and_reports_the_time_left |
| C09 | failure count not decremented as old outcomes leave | killed | Old_outcomes_slide_out_of_the_window |
| H01 | 408 not a failure | killed | Only_server_errors_and_408_count_as_failures |
| H02 | caller cancellation counted as failure | killed | Caller_cancellation_is_neither_a_success_nor_a_failure |
| H03 | overall timeout counted against the dependency | killed (after new test) | An_overall_deadline_timeout_is_not_the_dependencys_fault_and_is_not_counted |
| T01 | deadline exactly reached still sends | killed | An_expired_deadline_fails_before_the_inner_handler_is_called |
| T02 | header rounded down instead of up | killed | The_remaining_time_is_rounded_up_to_whole_milliseconds |
| T03 | tie labelled overall instead of attempt | killed | When_attempt_and_overall_are_equal_the_attempt_label_wins |
| T05 | caller cancellation reported as timeout when both happen | killed (after new test) | When_the_caller_cancels_and_the_timeout_fires_together_the_caller_wins |
| T06 | stale deadline header kept | killed | A_stale_header_on_the_request_is_overwritten |
| K01 | queue one longer than configured | killed | Disposing_a_lease_twice_releases_only_once |
| K02 | slot never given back | killed | The_handler_rejects_without_calling_the_inner_handler |
| K03 | LIFO instead of FIFO | killed | Queued_requests_reach_the_upstream_in_arrival_order |
| K05 | already-cancelled token queued | killed | An_already_cancelled_token_never_queues |

## Known limits

- Not Polly. No hedging, no rate limiter, no fallback, no telemetry or logging, no per-host breaker keys. No claim of
  parity with Polly or Microsoft.Extensions.Http.Resilience.
- State is local to one process: a fleet of N instances has N breakers, N budgets and N bulkheads.
- Request bodies are buffered for re-sending (default limit 1 MiB); larger or unknown-length bodies are sent once with no retry.
- Timeouts and the bulkhead cover the time until response headers, not the body download. The slot is released when
  headers arrive.
- `HttpClient.Timeout` and the caller's token are merged before a handler sees them, so a handler cannot tell them
  apart. Set `HttpClient.Timeout` to `Timeout.InfiniteTimeSpan` and let `TimeoutHandler` own timeouts.
- Failure classification is only status code and exception type. 501 and 505 are retried like other 5xx. The `Retry-After`
  parser only accepts IMF-fixdate dates.
- The retry budget keeps one timestamp per event in the window, which is fine for a demo but not for very high request rates.
- The demo upstream is synthetic and in-process; no real network faults (resets, partial reads, TLS, DNS) were tested.
- Verified only on Windows 11 with SDK 10.0.402. Other operating systems and the GitHub Actions workflow have not been run.

## Layout

```
src/HttpResilience/        the library
demo/Demo/                 console demo with an in-process Kestrel upstream
tests/HttpResilience.Tests xunit tests
scripts/mutation_check.py  hand-rolled mutation check
.github/workflows/ci.yml   restore, build -warnaserror, test, run the demo with small settings
```

## Clone and license

```
git clone https://github.com/Supp3r-Man/dotnet-http-resilience.git
```

MIT, see `LICENSE`.
