using PayPal360.Contracts.Exceptions;
using PayPal360.Contracts.Providers;

namespace PayPal360;

public sealed record ActionOperationResult(string Status, ActionRequest Request, string Message);

public sealed class CommerceActionService(
    CommerceDatabase database,
    PolicyManager policies,
    DemoStore store,
    IPaymentProvider paymentProvider,
    IRefundProvider? refundProvider = null)
{
    public IReadOnlyList<ActionRequest> List(string tenantId = "local") => database.Read(state => state.ActionRequests
        .Where(request => string.Equals(request.TenantId ?? "local", tenantId, StringComparison.Ordinal))
        .OrderByDescending(request => request.CreatedAt)
        .Take(200)
        .ToArray());

    public async Task<ActionOperationResult> RequestAsync(
        string action,
        string resourceId,
        string actor,
        string reason,
        string tenantId,
        bool human,
        CancellationToken ct,
        decimal? amount = null)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
            throw new ArgumentException("A resource ID is required.");

        var policy = policies.Get(action, tenantId);
        if (policy.Mode is not ("automatic" or "approval_required" or "human_only"))
            return CreateDenied(action, resourceId, actor, reason, tenantId, policy, "The action is blocked by policy.");
        if (policy.Mode == "human_only" && !human)
            return CreateDenied(action, resourceId, actor, reason, tenantId, policy, "The action requires a human operator.");

        var initialStatus = policy.Mode == "approval_required" ? "PENDING_APPROVAL" : "EXECUTING";
        var request = database.Update(state =>
        {
            var created = new ActionRequest(
                Guid.NewGuid().ToString("N"),
                action,
                resourceId,
                initialStatus,
                policy.Version,
                tenantId,
                actor,
                reason,
                DateTimeOffset.UtcNow,
                Amount: amount);
            state.ActionRequests.Add(created);
            CommerceDatabase.Record(state, resourceId, policy.Mode == "approval_required" ? "action.pending_approval" : "action.started", actor,
                reason, "action-gate", created.Id, action + "-v" + policy.Version, tenantId);
            return created;
        });

        if (initialStatus == "PENDING_APPROVAL")
            return new ActionOperationResult("PENDING_APPROVAL", request, "The action is waiting for human approval.");

        return await ExecuteAsync(request, actor, ct);
    }

    public async Task<ActionOperationResult> DecideAsync(
        string requestId,
        string decision,
        string actor,
        string rationale,
        string tenantId,
        CancellationToken ct)
    {
        var request = database.Read(state => state.ActionRequests.FirstOrDefault(item =>
            item.Id == requestId && string.Equals(item.TenantId ?? "local", tenantId, StringComparison.Ordinal)));
        if (request is null || request.Status != "PENDING_APPROVAL")
            throw new InvalidOperationException("Action request is missing or is no longer awaiting approval.");
        if (decision is not ("approve" or "reject"))
            throw new ArgumentException("Decision must be approve or reject.");

        var policy = policies.Get(request.Action, tenantId);
        if (policy.Mode != "approval_required" || policy.Version != request.PolicyVersion)
        {
            var stale = UpdateRequest(request, "STALE", actor, "Policy changed after this action request; a new request is required.", null);
            return new ActionOperationResult("STALE", stale, "The policy changed after this request was created.");
        }

        if (decision == "reject")
        {
            var rejected = UpdateRequest(request, "REJECTED", actor, rationale, "The action request was rejected by a human operator.");
            return new ActionOperationResult("REJECTED", rejected, "The action request was rejected.");
        }

        var executing = UpdateRequest(request, "EXECUTING", actor, rationale, "The action request was approved by a human operator.");
        return await ExecuteAsync(executing, actor, ct);
    }

    private async Task<ActionOperationResult> ExecuteAsync(ActionRequest request, string actor, CancellationToken ct)
    {
        if (request.Action == "capture_payment")
            return await CaptureAsync(request, actor, ct);
        if (request.Action == CommerceProviderActions.RefundPayment)
            return await RefundAsync(request, actor, ct);

        return UpdateResult(request, "UNAVAILABLE", actor, "No provider handler is registered for " + request.Action + ".");
    }

    private async Task<ActionOperationResult> CaptureAsync(ActionRequest request, string actor, CancellationToken ct)
    {
        var order = store.FindOrder(request.ResourceId, request.TenantId);
        if (order is null)
            return UpdateResult(request, "FAILED", actor, "The target order was not found in the tenant.");
        if (order.Status == "PAID")
            return UpdateResult(request, "COMPLETED", actor, "The target order was already captured.");

        order = store.BeginCapture(order.Id, actor, request.TenantId);
        try
        {
            var result = await paymentProvider.CaptureOrder(order.ProviderOrderId, ct);
            if (!result.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var authoritative = await paymentProvider.GetOrder(order.ProviderOrderId, ct);
                    if (authoritative.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
                    {
                        store.MarkPaid(order.Id, request.TenantId, authoritative.CaptureId ?? authoritative.Id);
                        return UpdateResult(request, "COMPLETED", actor, "Provider lookup confirmed the payment capture.", authoritative.CaptureId ?? authoritative.Id);
                    }
                    result = authoritative;
                }
                catch (NotSupportedException)
                {
                    // The provider has no authoritative lookup; retain the
                    // pending state and let the webhook/reconciliation path resolve it.
                }

                return UpdateResult(request, "PENDING_PROVIDER", actor, "Provider returned capture status " + result.Status + ".");
            }

            store.MarkPaid(order.Id, request.TenantId, result.CaptureId ?? result.Id);
            return UpdateResult(request, "COMPLETED", actor, "Payment capture completed.", result.CaptureId ?? result.Id);
        }
        catch (ProviderException)
        {
            try
            {
                var authoritative = await paymentProvider.GetOrder(order.ProviderOrderId, ct);
                if (authoritative.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
                {
                    store.MarkPaid(order.Id, request.TenantId, authoritative.CaptureId ?? authoritative.Id);
                    return UpdateResult(request, "COMPLETED", actor, "Provider lookup resolved a capture after an uncertain response.", authoritative.CaptureId ?? authoritative.Id);
                }
            }
            catch (NotSupportedException)
            {
                // Without lookup support the durable webhook/reconciliation
                // path remains the source of truth.
            }
            catch (ProviderException)
            {
                // Keep the action explicitly uncertain; do not claim failure.
            }

            return UpdateResult(request, "UNKNOWN", actor, "Provider capture outcome is uncertain; reconcile it before retrying.");
        }
    }

    private async Task<ActionOperationResult> RefundAsync(ActionRequest request, string actor, CancellationToken ct)
    {
        var order = store.FindOrder(request.ResourceId, request.TenantId);
        if (order is null)
            return UpdateResult(request, "FAILED", actor, "The target order was not found in the tenant.");
        if (order.Status != "PAID")
            return UpdateResult(request, "FAILED", actor, "Only a paid order can be refunded.");
        if (refundProvider is null)
            return UpdateResult(request, "UNAVAILABLE", actor, "No refund provider is registered.");
        if (string.IsNullOrWhiteSpace(order.ProviderCaptureId))
            return UpdateResult(request, "UNAVAILABLE", actor, "The PayPal capture reference is not available; reconcile the capture first.");

        var refundAmount = request.Amount ?? order.Price;
        if (refundAmount <= 0m)
            return UpdateResult(request, "FAILED", actor, "The refund amount must be greater than zero.");
        var refunded = database.Read(state => state.ActionRequests
            .Where(item => item.Action == CommerceProviderActions.RefundPayment &&
                item.ResourceId == order.Id &&
                string.Equals(item.TenantId ?? "local", order.TenantId, StringComparison.Ordinal) &&
                item.Status == "COMPLETED")
            .Sum(item => item.Amount ?? order.Price));
        var remaining = order.Price - refunded;
        if (refundAmount > remaining)
            return UpdateResult(request, "FAILED", actor, "The requested refund exceeds the remaining refundable amount.");

        var idempotencyKey = "refund-" + request.Id;
        var providerRequest = new ProviderActionRequest(
            request.Action,
            request.TenantId,
            order.Id,
            idempotencyKey,
            actor,
            false,
            order.ProviderCaptureId,
            request.Amount,
            order.Currency);
        try
        {
            var result = await refundProvider.RefundAsync(providerRequest, ct);
            var completed = result.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase);
            return UpdateResult(request,
                completed ? "COMPLETED" : "PENDING_PROVIDER",
                actor,
                result.Message,
                result.ProviderReference,
                result.IdempotencyKey,
                result.RequiresReconciliation);
        }
        catch (ProviderException e)
        {
            return UpdateResult(request, "UNKNOWN", actor,
                e.Message + " Reconcile the refund before retrying.",
                null,
                idempotencyKey,
                true);
        }
    }

    public async Task<ActionOperationResult> ReconcileAsync(string requestId, string actor, string rationale, string tenantId, CancellationToken ct)
    {
        var request = database.Read(state => state.ActionRequests.FirstOrDefault(item =>
            item.Id == requestId && string.Equals(item.TenantId ?? "local", tenantId, StringComparison.Ordinal)));
        if (request is null || request.Action != CommerceProviderActions.RefundPayment)
            throw new InvalidOperationException("Refund action request was not found.");
        if (request.Status is not ("UNKNOWN" or "PENDING_PROVIDER"))
            throw new InvalidOperationException("Only an uncertain refund can be reconciled.");
        if (refundProvider is null)
            return UpdateResult(request, "UNAVAILABLE", actor, "No refund provider is registered.", reconciliationReason: rationale);
        if (string.IsNullOrWhiteSpace(request.ProviderReference))
            throw new InvalidOperationException("The refund has no provider reference to reconcile.");

        var idempotencyKey = request.IdempotencyKey ?? "refund-" + request.Id;
        try
        {
            var result = await refundProvider.ReconcileRefundAsync(
                new ProviderReconciliationRequest(request.Action, tenantId, request.ResourceId, idempotencyKey, request.ProviderReference), ct);
            return UpdateResult(request,
                result.IsAuthoritative && result.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)
                    ? "COMPLETED"
                    : "PENDING_PROVIDER",
                actor,
                result.Message,
                result.ProviderReference ?? request.ProviderReference,
                idempotencyKey,
                !(result.IsAuthoritative && result.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)),
                rationale);
        }
        catch (ProviderException e)
        {
            return UpdateResult(request, "UNKNOWN", actor, e.Message + " Refund remains unresolved.", request.ProviderReference, idempotencyKey, true, rationale);
        }
    }

    private ActionOperationResult CreateDenied(string action, string resourceId, string actor, string reason, string tenantId, PolicyRule policy, string message) => database.Update(state =>
    {
        var request = new ActionRequest(Guid.NewGuid().ToString("N"), action, resourceId, "DENIED", policy.Version, tenantId, actor, reason, DateTimeOffset.UtcNow, Result: message);
        state.ActionRequests.Add(request);
        CommerceDatabase.Record(state, resourceId, "policy.denied", actor, message + " Reason: " + reason, "gatekeeper", request.Id, action + "-v" + policy.Version, tenantId);
        return new ActionOperationResult("DENIED", request, message);
    });

    private ActionRequest UpdateRequest(ActionRequest request, string status, string actor, string rationale, string? result) => database.Update(state =>
    {
        var index = state.ActionRequests.FindIndex(item => item.Id == request.Id);
        if (index < 0)
            throw new InvalidDataException("The action request is missing.");
        var updated = state.ActionRequests[index] with
        {
            Status = status,
            DecidedBy = status is "REJECTED" or "STALE" or "EXECUTING" ? actor : state.ActionRequests[index].DecidedBy,
            DecisionReason = status is "REJECTED" or "STALE" or "EXECUTING" ? rationale : state.ActionRequests[index].DecisionReason,
            Result = result ?? state.ActionRequests[index].Result
        };
        state.ActionRequests[index] = updated;
        CommerceDatabase.Record(state, updated.ResourceId, status == "EXECUTING" ? "approval.approve" : "action." + status.ToLowerInvariant(), actor,
            rationale, "action-gate", updated.Id, updated.Action + "-v" + updated.PolicyVersion, updated.TenantId);
        return updated;
    });

    private ActionOperationResult UpdateResult(
        ActionRequest request,
        string status,
        string actor,
        string message,
        string? providerReference = null,
        string? idempotencyKey = null,
        bool reconciliationRequired = false,
        string? reconciliationReason = null) => database.Update(state =>
    {
        var index = state.ActionRequests.FindIndex(item => item.Id == request.Id);
        if (index < 0)
            throw new InvalidDataException("The action request is missing.");
        var updated = state.ActionRequests[index] with
        {
            Status = status,
            Result = message,
            ProviderReference = providerReference ?? state.ActionRequests[index].ProviderReference,
            IdempotencyKey = idempotencyKey ?? state.ActionRequests[index].IdempotencyKey,
            ReconciliationRequired = reconciliationRequired,
            ReconciliationReason = reconciliationReason ?? state.ActionRequests[index].ReconciliationReason
        };
        state.ActionRequests[index] = updated;
        var auditReason = string.IsNullOrWhiteSpace(reconciliationReason) ? message : message + " Reconciliation reason: " + reconciliationReason;
        CommerceDatabase.Record(state, updated.ResourceId, "action." + status.ToLowerInvariant(), actor, auditReason,
            "action-executor", updated.Id, updated.Action + "-v" + updated.PolicyVersion, updated.TenantId);
        return new ActionOperationResult(status, updated, message);
    });
}
