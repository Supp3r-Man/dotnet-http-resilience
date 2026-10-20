namespace HttpResilience;

/// <summary>
/// Limits concurrent requests through a shared <see cref="Bulkhead"/>. The slot is held until the handler chain
/// returns (response headers), not until the body is read. Rejection throws <see cref="BulkheadRejectedException"/>.
/// </summary>
public sealed class BulkheadHandler : DelegatingHandler
{
    private readonly Bulkhead _bulkhead;

    public BulkheadHandler(Bulkhead bulkhead)
    {
        _bulkhead = bulkhead ?? throw new ArgumentNullException(nameof(bulkhead));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await _bulkhead.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
