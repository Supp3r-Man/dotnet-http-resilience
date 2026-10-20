#!/usr/bin/env python
"""Hand-rolled mutation check: apply one textual mutation at a time, run the tests, expect a failure, restore.

Usage:  python scripts/mutation_check.py [--only ID[,ID...]]
Env:    MUT_TIMEOUT=seconds per mutant (default 240)
        dotnet must be on PATH (and DOTNET_CLI_TELEMETRY_OPTOUT=1 is a good idea).

Rules:
  * every mutation's `old` text must occur exactly once in its file, otherwise it is reported as BAD_PATTERN;
  * the original bytes of every touched file are restored in a finally block, and at the end the script
    compares all of src/ with a snapshot taken before the first mutation;
  * a mutant that hangs (infinite loop) is only killed by the timeout; the whole process tree of the
    `dotnet test` this script started is then killed (and only that tree) and the result is reported as
    KILLED_TIMEOUT so you can tell it apart from a real assertion failure.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "HttpResilience")
TIMEOUT = int(os.environ.get("MUT_TIMEOUT", "240"))

# (id, file, old, new, description)
MUTATIONS = [
    ("R01", "RetryHandler.cs", "return code == 408 || code == 429 || code >= 500;",
     "return code == 408 || code == 429 || code > 500;", "HTTP 500 no longer retried (>= became >)"),
    ("R03", "RetryHandler.cs", "return keys.Any(k => !string.IsNullOrWhiteSpace(k));", "return true;",
     "blank Idempotency-Key accepted as a key"),
    ("R04", "RetryHandler.cs", "            || method == HttpMethod.Delete || method == HttpMethod.Options)",
     "            || method == HttpMethod.Delete)", "OPTIONS no longer treated as retryable"),
    ("R05", "RetryHandler.cs", "if (length is null || length > _options.MaxBufferedContentBytes)",
     "if (length is null || length >= _options.MaxBufferedContentBytes)", "body exactly at the buffer limit no longer buffered"),
    ("R06", "RetryHandler.cs", "var backoff = TimeSpan.FromTicks((long)(ceiling.Ticks * r));", "var backoff = ceiling;",
     "jitter removed (always the full ceiling)"),
    ("R07", "RetryHandler.cs", "r = double.IsNaN(r) ? 0 : Math.Clamp(r, 0, 1);", "r = double.IsNaN(r) ? 0 : r;",
     "random source not clamped to [0,1]"),
    ("R08", "RetryHandler.cs", "&& retryAfter > backoff)", "&& retryAfter < backoff)",
     "Retry-After only used when shorter than the backoff (max became min)"),
    ("R09", "RetryHandler.cs", "if (remaining is null || remaining.Value > delay)",
     "if (remaining is null || remaining.Value >= delay)", "retry allowed when remaining deadline equals the delay"),
    ("R10", "RetryHandler.cs", "if (canRetry && retryIndex < _options.MaxRetries)",
     "if (canRetry && retryIndex <= _options.MaxRetries)", "one retry too many (< became <=)"),
    ("R11", "RetryHandler.cs", "retry = _options.Budget is null || _options.Budget.TryAcquireRetry();", "retry = true;",
     "retry budget ignored"),
    ("R13", "RetryHandler.cs", "OperationCanceledException => !callerToken.IsCancellationRequested,",
     "OperationCanceledException => true,", "IsTransient treats caller cancellation as transient"),
    ("R14", "RetryHandler.cs", "TimeoutRejectedException { Kind: TimeoutKind.Attempt } => true,",
     "TimeoutRejectedException => true,", "overall-deadline timeouts retried too"),
    ("R19", "RetryHandler.cs", "var capped = Math.Min(ms, options.MaxDelay.TotalMilliseconds);", "var capped = ms;",
     "backoff cap removed"),
    ("P01", "RetryAfterParser.cs", "|| seconds >= maxDelay.TotalSeconds)", "|| seconds > maxDelay.TotalSeconds)",
     "Retry-After seconds equal to the cap (>= became >)"),
    ("P03", "RetryAfterParser.cs", "delay = remaining > maxDelay ? maxDelay : remaining;", "delay = remaining;",
     "Retry-After HTTP-date not capped"),
    ("B01", "RetryBudget.cs", "if (_retries.Count + 1 > allowed)", "if (_retries.Count + 1 >= allowed)",
     "budget off by one (one retry fewer than allowed)"),
    ("B02", "RetryBudget.cs", "while (_requests.Count > 0 && _time.GetElapsedTime(_requests.Peek(), now) >= _options.Window)",
     "while (_requests.Count > 0 && _time.GetElapsedTime(_requests.Peek(), now) > _options.Window)",
     "request events leave the window one tick late"),
    ("B04", "RetryBudget.cs", "(long)Math.Floor((_options.RetryRatio * _requests.Count) + 1e-9)",
     "(long)Math.Floor(_options.RetryRatio * _requests.Count)", "floating point epsilon removed from the allowance"),
    ("C01", "CircuitBreaker.cs", "&& (double)_failures / _filled >= _options.FailureRateThreshold)",
     "&& (double)_failures / _filled > _options.FailureRateThreshold)", "opens only above the threshold (>= became >)"),
    ("C02", "CircuitBreaker.cs", "if (_filled >= _options.MinimumCalls", "if (_filled > _options.MinimumCalls",
     "minimum calls off by one"),
    ("C03", "CircuitBreaker.cs",
     "            if (permit.Generation != _generation)\n            {\n                return;\n            }\n\n            var now = _time.GetTimestamp();",
     "            if (false)\n            {\n                return;\n            }\n\n            var now = _time.GetTimestamp();",
     "stale-generation outcomes are counted"),
    ("C04", "CircuitBreaker.cs",
     "            if (permit.Generation != _generation)\n            {\n                return;\n            }\n\n            if (_state == CircuitState.HalfOpen && permit.IsProbe",
     "            if (false)\n            {\n                return;\n            }\n\n            if (_state == CircuitState.HalfOpen && permit.IsProbe",
     "stale-generation Release frees a current probe permit"),
    ("C05", "CircuitBreaker.cs", "if (_probesSucceeded >= _options.HalfOpenPermits)", "if (_probesSucceeded >= 1)",
     "first successful probe closes the breaker"),
    ("C06", "CircuitBreaker.cs", "_state == CircuitState.HalfOpen && _probesIssued < _options.HalfOpenPermits",
     "_state == CircuitState.HalfOpen && _probesIssued <= _options.HalfOpenPermits", "one probe too many admitted"),
    ("C08", "CircuitBreaker.cs", "_openedAt = now;", "/* mutated */", "open timer not restarted on re-open"),
    ("C09", "CircuitBreaker.cs", "                _failures--;\n", "                /* mutated */\n",
     "failure count not decremented when an old failure leaves the window"),
    ("H01", "CircuitBreakerHandler.cs", "return code >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout;",
     "return code >= 500;", "408 not counted as a failure"),
    ("H02", "CircuitBreakerHandler.cs", "        {\n            _breaker.Release(permit);\n            throw;\n        }\n        catch (Exception ex)",
     "        {\n            _breaker.RecordFailure(permit);\n            throw;\n        }\n        catch (Exception ex)",
     "caller cancellation counted as a failure"),
    ("H03", "CircuitBreakerHandler.cs", "or TimeoutRejectedException { Kind: TimeoutKind.Attempt })", "or TimeoutRejectedException)",
     "overall-deadline timeouts counted against the dependency"),
    ("T01", "TimeoutHandler.cs", "if (remaining is { } left && left <= TimeSpan.Zero)", "if (remaining is { } left && left < TimeSpan.Zero)",
     "deadline exactly reached still sends the request"),
    ("T02", "TimeoutHandler.cs", "var ms = (long)Math.Ceiling(toSend.TotalMilliseconds);", "var ms = (long)Math.Floor(toSend.TotalMilliseconds);",
     "deadline header rounded down instead of up"),
    ("T03", "TimeoutHandler.cs", "(effective is null || rem < effective.Value))", "(effective is null || rem <= effective.Value))",
     "tie between attempt timeout and remaining time labelled overall"),
    ("T05", "TimeoutHandler.cs", "when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)",
     "when (timeoutCts.IsCancellationRequested)", "caller cancellation reported as timeout when both happen"),
    ("T06", "TimeoutHandler.cs", "request.Headers.Remove(_options.HeaderName);", "/* mutated */",
     "stale deadline header kept next to the new one"),
    ("K01", "Bulkhead.cs", "if (_queue.Count >= _maxQueue)", "if (_queue.Count > _maxQueue)", "queue one longer than configured"),
    ("K02", "Bulkhead.cs", "                _inFlight--;\n", "                /* mutated */\n", "slot never given back when nobody waits"),
    ("K03", "Bulkhead.cs", "waiter.Node = _queue.AddLast(waiter);", "waiter.Node = _queue.AddFirst(waiter);", "LIFO instead of FIFO"),
    ("K05", "Bulkhead.cs", "        cancellationToken.ThrowIfCancellationRequested();\n        Waiter waiter;",
     "        Waiter waiter;", "already-cancelled token is queued instead of rejected"),
]


def read(path):
    with open(path, encoding="utf-8", newline="") as f:
        return f.read()


def write(path, text):
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def snapshot():
    snap = {}
    for dirpath, _, files in os.walk(SRC):
        if os.sep + "obj" in dirpath or os.sep + "bin" in dirpath:
            continue
        for name in files:
            if name.endswith((".cs", ".csproj")):
                p = os.path.join(dirpath, name)
                snap[p] = read(p)
    return snap


def kill_tree(proc):
    if os.name == "nt":
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True)
    else:
        proc.kill()


def run_tests():
    # TreatWarningsAsErrors=false: several mutants (if (false), a field that is never assigned) only produce warnings.
    # --blame-hang-timeout: a mutant that hangs makes testhost get killed after 60 s instead of waiting for MUT_TIMEOUT.
    cmd = ["dotnet", "test", os.path.join("tests", "HttpResilience.Tests"), "--no-restore", "-nologo", "-v", "minimal",
           "-p:TreatWarningsAsErrors=false", "--blame-hang-timeout", "60s", "--blame-hang-dump-type", "none"]
    flags = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
    proc = subprocess.Popen(cmd, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                            encoding="utf-8", errors="replace", creationflags=flags)
    try:
        out, _ = proc.communicate(timeout=TIMEOUT)
    except subprocess.TimeoutExpired:
        kill_tree(proc)
        try:
            proc.communicate(timeout=30)
        except subprocess.TimeoutExpired:
            pass
        return "KILLED_TIMEOUT", ["(timeout %ds, process tree killed)" % TIMEOUT]
    if proc.returncode == 0:
        return "SURVIVED", []
    failed = []
    for m in re.finditer(r"^\s+Failed (\S+)", out, re.M):
        name = m.group(1).replace("HttpResilience.Tests.", "")
        if name not in failed:
            failed.append(name)
    if failed:
        return "KILLED", failed[:2]
    if re.search(r"hang", out, re.I) and not re.search(r"error CS\d+", out):
        return "KILLED_TIMEOUT", ["(hang detected by --blame-hang-timeout 60s)"]
    if re.search(r"error CS\d+", out):
        return "COMPILE_ERROR", []
    return "KILLED", ["(non-zero exit, no test name parsed)"]


def main():
    only = None
    if "--only" in sys.argv:
        only = set(sys.argv[sys.argv.index("--only") + 1].split(","))
    before = snapshot()
    originals = {}
    rows = []
    try:
        for mid, fname, old, new, desc in MUTATIONS:
            if only and mid not in only:
                continue
            path = os.path.join(SRC, fname)
            if path not in originals:
                originals[path] = read(path)
            text = originals[path]
            if text.count(old) != 1:
                rows.append((mid, desc, "BAD_PATTERN(%d matches)" % text.count(old), []))
                print(mid, "BAD_PATTERN", text.count(old), flush=True)
                continue
            write(path, text.replace(old, new))
            try:
                status, names = run_tests()
            finally:
                write(path, text)
            rows.append((mid, desc, status, names))
            print(f"{mid} {status} {', '.join(names)}  -- {desc}", flush=True)
    finally:
        for path, text in originals.items():
            write(path, text)
    after = snapshot()
    print()
    print("| # | Mutation | Result | Caught by |")
    print("|---|----------|--------|-----------|")
    for mid, desc, status, names in rows:
        print(f"| {mid} | {desc} | {status} | {', '.join(names) or '-'} |")
    killed = sum(1 for r in rows if r[2].startswith("KILLED"))
    timeouts = sum(1 for r in rows if r[2] == "KILLED_TIMEOUT")
    survived = sum(1 for r in rows if r[2] == "SURVIVED")
    other = len(rows) - killed - survived
    print(f"\nkilled {killed}/{len(rows)} (of which by timeout: {timeouts}), survived {survived}, other (compile error / bad pattern): {other}")
    print("source identical to the pre-run snapshot:", before == after)
    return 0 if before == after else 1


if __name__ == "__main__":
    sys.exit(main())
