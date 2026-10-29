namespace PayPal360.Contracts.Providers;

// These are the common boundaries for any future external action handler. No
// implementation is registered by the sandbox until the provider contract,
// authorization policy and source of truth have an owner.
public static class CommerceProviderActions
{
    public const string RefundPayment = "refund_payment";
    public const string FulfillOrder = "fulfill_order";
    public const string PublishProduct = "publish_product";
}

public sealed record ProviderActionRequest(
    string Action,
    string TenantId,
    string ResourceId,
    string IdempotencyKey,
    string RequestedBy,
    bool DryRun = false,
    string? ProviderReference = null,
    decimal? Amount = null,
    string? Currency = null);

public sealed record ProviderActionResult(
    string Status,
    string? ProviderReference,
    string IdempotencyKey,
    bool RequiresReconciliation,
    string Message);

public sealed record ProviderReconciliationRequest(
    string Action,
    string TenantId,
    string ResourceId,
    string IdempotencyKey,
    string? ProviderReference = null);

public sealed record ProviderReconciliationResult(
    string Status,
    string? ProviderReference,
    bool IsAuthoritative,
    string Message);

public interface IRefundProvider
{
    Task<ProviderActionResult> RefundAsync(ProviderActionRequest request, CancellationToken cancellationToken);
    Task<ProviderReconciliationResult> ReconcileRefundAsync(ProviderReconciliationRequest request, CancellationToken cancellationToken);
}

public interface IOrderFulfillmentProvider
{
    Task<ProviderActionResult> FulfillAsync(ProviderActionRequest request, CancellationToken cancellationToken);
    Task<ProviderReconciliationResult> ReconcileFulfillmentAsync(ProviderReconciliationRequest request, CancellationToken cancellationToken);
}

public interface IProductPublicationProvider
{
    Task<ProviderActionResult> PublishAsync(ProviderActionRequest request, CancellationToken cancellationToken);
    Task<ProviderReconciliationResult> ReconcilePublicationAsync(ProviderReconciliationRequest request, CancellationToken cancellationToken);
}
