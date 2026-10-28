using System.Security.Cryptography;
using System.Text;
using PayPal360.Contracts;
using PayPal360.Contracts.Exceptions;
using PayPal360.Contracts.Providers;

namespace PayPal360;

// An unfinished attempt is conservatively not retried after five hours within the six-hour
// create-order idempotency window, because the provider outcome may be unknown.
public record CheckoutAttempt(
    Guid RequestId,
    Product Product,
    DateTimeOffset CreatedAt,
    string? OrderId,
    string TenantId = "local",
    string? ProviderOrderId = null,
    string ProviderStatus = "UNKNOWN",
    string State = "PENDING",
    string? LastError = null,
    DateTimeOffset? UpdatedAt = null,
    string? AccessTokenHash = null);

public sealed class CheckoutCoordinator(IPaymentProvider provider, CommerceDatabase database)
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromHours(5);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<(Order Order, bool Created)> CreateAsync(Product product, Guid requestId, CancellationToken ct, string tenantId = "local", string? accessToken = null)
    {
        await gate.WaitAsync(ct);
        try
        {
            var attempt = database.Update(state =>
            {
                var existing = state.CheckoutAttempts.FirstOrDefault(item => item.RequestId == requestId);
                if (existing is not null)
                {
                    if (existing.Product != product || !string.Equals(existing.TenantId ?? "local", tenantId, StringComparison.Ordinal))
                        throw new InvalidOperationException("The checkout request ID is associated with different product details.");
                    if (existing.AccessTokenHash is not null && !AccessTokenMatches(existing.AccessTokenHash, accessToken))
                        throw new UnauthorizedAccessException("The checkout request is not authorized by its checkout session.");
                    if (existing.AccessTokenHash is null && !string.IsNullOrWhiteSpace(accessToken))
                    {
                        existing = existing with
                        {
                            AccessTokenHash = HashAccessToken(accessToken)
                        };
                        state.CheckoutAttempts[state.CheckoutAttempts.FindIndex(item => item.RequestId == requestId)] = existing;
                    }
                    if (existing.OrderId is null && DateTimeOffset.UtcNow - existing.CreatedAt >= RetryWindow)
                        throw new InvalidOperationException("The checkout outcome is uncertain and its retry window has expired; reconcile it before trying again.");
                    return existing;
                }
                var created = new CheckoutAttempt(requestId, product, DateTimeOffset.UtcNow, null, tenantId, null, "UNKNOWN", "PENDING_CREATE", null, DateTimeOffset.UtcNow,
                    string.IsNullOrWhiteSpace(accessToken) ? null : HashAccessToken(accessToken));
                state.CheckoutAttempts.Add(created);
                CommerceDatabase.Record(state, "-", "checkout.requested", "buyer", "Sandbox checkout requested.", "PayPal sandbox", requestId.ToString("D"), null, tenantId);
                return created;
            });

            if (attempt.OrderId is not null)
            {
                var saved = database.Read(state => state.Orders.FirstOrDefault(order => order.Id == attempt.OrderId));
                return (saved ?? throw new InvalidDataException("The saved checkout order is missing."), false);
            }

            ProviderResult result;
            try
            {
                // A provider order reference lets a restart reconcile by lookup. If the
                // response was lost before it was persisted, retrying the same provider
                // idempotency key is the safe recovery path.
                result = attempt.ProviderOrderId is not null
                    ? await provider.GetOrder(attempt.ProviderOrderId, ct)
                    : await provider.CreateOrder(attempt.Product, requestId.ToString("D"), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                database.Update(state =>
                {
                    var index = state.CheckoutAttempts.FindIndex(item => item.RequestId == requestId);
                    if (index >= 0)
                        state.CheckoutAttempts[index] = state.CheckoutAttempts[index] with
                        {
                            State = "UNKNOWN",
                            LastError = e.Message,
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                    return true;
                });
                throw;
            }
            return database.Update(state =>
            {
                var index = state.CheckoutAttempts.FindIndex(item => item.RequestId == requestId);
                if (index < 0)
                    throw new InvalidDataException("The checkout attempt is missing.");
                var current = state.CheckoutAttempts[index];
                if (current.OrderId is not null)
                    return (state.Orders.Single(order => order.Id == current.OrderId), false);
                var orderStatus = result.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) ? "PAID" : "CREATED";
                var order = new Order(Guid.NewGuid().ToString("N"), product.Id, product.Name, product.Price, product.Currency, result.Id, orderStatus, current.TenantId);
                state.Orders.Add(order);
                state.CheckoutAttempts[index] = current with
                {
                    OrderId = order.Id,
                    ProviderOrderId = result.Id,
                    ProviderStatus = result.Status,
                    State = "RESOLVED",
                    LastError = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                CommerceDatabase.Record(state, order.Id, "checkout.created", "buyer", "PayPal sandbox order created from the recorded product price.", "PayPal sandbox", result.Id, null, current.TenantId);
                CommerceDatabase.Record(state, order.Id, "checkout.reconciled", "system", "Provider outcome was resolved and linked to the local checkout attempt.", "PayPal sandbox", result.Id, null, current.TenantId);
                if (orderStatus == "PAID")
                    CommerceDatabase.Record(state, order.Id, "capture.completed", "system", "Provider lookup confirmed a completed capture.", "PayPal sandbox", result.Id, null, current.TenantId);
                return (order, true);
            });
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<(Order? Order, string Status)> ReconcileAsync(Guid requestId, CancellationToken ct, string tenantId = "local", string? accessToken = null)
    {
        var attempt = database.Read(state => state.CheckoutAttempts.FirstOrDefault(item => item.RequestId == requestId));
        if (attempt is null || !string.Equals(attempt.TenantId ?? "local", tenantId, StringComparison.Ordinal))
            return (null, "NOT_FOUND");
        if (attempt.AccessTokenHash is not null && !AccessTokenMatches(attempt.AccessTokenHash, accessToken))
            return (null, "FORBIDDEN");
        if (attempt.OrderId is not null)
            return (database.Read(state => state.Orders.FirstOrDefault(order => order.Id == attempt.OrderId)), "RESOLVED");

        try
        {
            // This uses PayPal's idempotency key when no provider reference was saved,
            // then immediately asks the provider for authoritative status.
            var created = attempt.ProviderOrderId is null
                ? await provider.CreateOrder(attempt.Product, requestId.ToString("D"), ct)
                : await provider.GetOrder(attempt.ProviderOrderId, ct);
            var authoritative = await provider.GetOrder(created.Id, ct);
            var result = database.Update(state =>
            {
                var index = state.CheckoutAttempts.FindIndex(item => item.RequestId == requestId);
                if (index < 0)
                    throw new InvalidDataException("The checkout attempt is missing.");
                var current = state.CheckoutAttempts[index];
                if (current.OrderId is not null)
                    return state.Orders.Single(order => order.Id == current.OrderId);
                var status = authoritative.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) ? "PAID" : "CREATED";
                var order = new Order(Guid.NewGuid().ToString("N"), current.Product.Id, current.Product.Name, current.Product.Price, current.Product.Currency, authoritative.Id, status, current.TenantId);
                state.Orders.Add(order);
                state.CheckoutAttempts[index] = current with
                {
                    OrderId = order.Id,
                    ProviderOrderId = authoritative.Id,
                    ProviderStatus = authoritative.Status,
                    State = "RESOLVED",
                    LastError = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                CommerceDatabase.Record(state, order.Id, "checkout.reconciled", "system", "Checkout was reconciled against the provider after an uncertain outcome.", "PayPal sandbox", authoritative.Id, null, current.TenantId);
                if (status == "PAID")
                    CommerceDatabase.Record(state, order.Id, "capture.completed", "system", "Provider reconciliation confirmed a completed capture.", "PayPal sandbox", authoritative.Id, null, current.TenantId);
                return order;
            });
            return (result, "RESOLVED");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            database.Update(state =>
            {
                var index = state.CheckoutAttempts.FindIndex(item => item.RequestId == requestId);
                if (index >= 0)
                    state.CheckoutAttempts[index] = state.CheckoutAttempts[index] with
                    {
                        State = "UNKNOWN",
                        LastError = e.Message,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                return true;
            });
            return (null, "UNKNOWN");
        }
    }

    public bool CanAccessRequest(Guid requestId, string? accessToken, string tenantId = "local") => database.Read(state =>
    {
        var attempt = state.CheckoutAttempts.FirstOrDefault(item => item.RequestId == requestId && string.Equals(item.TenantId ?? "local", tenantId, StringComparison.Ordinal));
        return attempt is not null && AccessTokenMatches(attempt.AccessTokenHash, accessToken);
    });

    public bool CanAccessOrder(string orderId, string? accessToken, string tenantId = "local") => database.Read(state =>
    {
        var order = state.Orders.FirstOrDefault(item => item.Id == orderId && string.Equals(item.TenantId ?? "local", tenantId, StringComparison.Ordinal));
        if (order is null)
            return false;
        var attempt = state.CheckoutAttempts.FirstOrDefault(item => item.OrderId == order.Id && string.Equals(item.TenantId ?? "local", tenantId, StringComparison.Ordinal));
        return attempt is not null && AccessTokenMatches(attempt.AccessTokenHash, accessToken);
    });

    public static string CreateAccessToken() => "pp360_checkout_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static bool AccessTokenMatches(string? expectedHash, string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(expectedHash) || string.IsNullOrWhiteSpace(accessToken))
            return false;
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(accessToken));
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), actualHash);
    }

    private static string HashAccessToken(string accessToken) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken))).ToLowerInvariant();
}


