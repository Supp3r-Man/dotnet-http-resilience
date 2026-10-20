using System.Net;

namespace HttpResilience;

/// <summary>
/// Guards a dependency with a shared <see cref="CircuitBreaker"/>. Fast-fails with <see cref="BrokenCircuitException"/>.
/// Failures: 5xx and 408 responses, <see cref="HttpRequestException"/>, attempt timeouts and non-caller cancellation.
/// Caller cancellation is neither success nor failure: the permit is returned.
/// The breaker object (not the handler) holds the state, so it survives IHttpClientFactory handler rotation.
/// </summary>
public sealed class CircuitBreakerHandler : DelegatingHandler
{
    private readonly CircuitBreaker _breaker;
    private readonly Func<HttpResponseMessage, bool> _isFailure;

    public CircuitBreakerHandler(CircuitBreaker breaker, Func<HttpResponseMessage, bool>? isFailureResponse = null)
    {
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _isFailure = isFailureResponse ?? DefaultIsFailure;
    }

    public static bool DefaultIsFailure(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        return code >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_breaker.TryAcquire(out var permit, out var retryAfter))
        {
            throw new BrokenCircuitException(
                $"The circuit is {_breaker.State}; call rejected. Retry after {retryAfter.TotalMilliseconds:0} ms.",
                retryAfter);
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _breaker.Release(permit);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                       or TimeoutRejectedException { Kind: TimeoutKind.Attempt })
        {
            _breaker.RecordFailure(permit);
            throw;
        }
        catch
        {
            _breaker.Release(permit);
            throw;
        }

        if (_isFailure(response))
        {
            _breaker.RecordFailure(permit);
        }
        else
        {
            _breaker.RecordSuccess(permit);
        }

        return response;
    }
}
