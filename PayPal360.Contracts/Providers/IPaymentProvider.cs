namespace PayPal360.Contracts.Providers;

public interface IPaymentProvider
{
    Task<ProviderResult> CreateOrder(Product product, string requestId, CancellationToken ct);
    Task<ProviderResult> CaptureOrder(string providerId, CancellationToken ct);

    // Providers that support recovery should return the authoritative current
    // order state. The default keeps existing local/test adapters source-compatible.
    Task<ProviderResult> GetOrder(string providerId, CancellationToken ct) =>
        throw new NotSupportedException("This payment provider does not support order reconciliation.");
}

