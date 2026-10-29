using PayPal360.Contracts;

namespace PayPal360;

public sealed class DemoCatalog : IProductCatalog
{
    private static readonly Product[] Products = [new("p1", "PayPal 360 demo product", 49m, "PLN"), new("p2", "Creative consultation", 120m, "PLN")];
    public IReadOnlyList<Product> List() => [.. Products];
    public Product? Find(string id) => Products.FirstOrDefault(p => p.Id == id);
}

public record FollowUpTask(string Id, string ProposalId, string OrderId, string Description, string Status);
public record ConnectorVerification(string ConnectorId, string Status, string Detail, DateTimeOffset CheckedAt, string TenantId = "local");
public record ProviderEvent(string EventId, string ProviderOrderId, string Status, DateTimeOffset ReceivedAt, string TenantId = "local", string? ProviderCaptureId = null);
public record ActionRequest(
    string Id,
    string Action,
    string ResourceId,
    string Status,
    int PolicyVersion,
    string TenantId,
    string RequestedBy,
    string Reason,
    DateTimeOffset CreatedAt,
    string? DecidedBy = null,
    string? DecisionReason = null,
    string? Result = null,
    decimal? Amount = null,
    string? ProviderReference = null,
    string? IdempotencyKey = null,
    bool ReconciliationRequired = false,
    string? ReconciliationReason = null);

public sealed class DemoStore(CommerceDatabase database)
{
    public Order AddOrder(Product p, string externalId, string tenantId = "local") => database.Update(state =>
    {
        var order = new Order(Guid.NewGuid().ToString("N"), p.Id, p.Name, p.Price, p.Currency, externalId, "CREATED", tenantId);
        state.Orders.Add(order);
        CommerceDatabase.Record(state, order.Id, "checkout.created", "buyer", "PayPal sandbox order created from server-side product price.", "PayPal sandbox", externalId, null, tenantId);
        return order;
    });

    public IReadOnlyList<Order> ListOrders(string tenantId = "local", IReadOnlyList<string>? resourceScope = null) => database.Read(state => (IReadOnlyList<Order>)state.Orders.Where(order => string.Equals(order.TenantId ?? "local", tenantId, StringComparison.Ordinal) && (resourceScope is null || resourceScope.Count == 0 || resourceScope.Contains(order.Id, StringComparer.Ordinal))).AsEnumerable().Reverse().ToArray());
    public IReadOnlyList<Proposal> ListProposals(string tenantId = "local", IReadOnlyList<string>? resourceScope = null) => database.Read(state => (IReadOnlyList<Proposal>)state.Proposals.Where(proposal => string.Equals(proposal.TenantId ?? "local", tenantId, StringComparison.Ordinal) && (resourceScope is null || resourceScope.Count == 0 || resourceScope.Contains(proposal.OrderId, StringComparer.Ordinal))).AsEnumerable().Reverse().ToArray());
    public IReadOnlyList<FollowUpTask> ListTasks(string tenantId = "local", IReadOnlyList<string>? resourceScope = null) => database.Read(state => (IReadOnlyList<FollowUpTask>)state.Tasks.Where(task => state.Orders.Any(order => order.Id == task.OrderId && string.Equals(order.TenantId ?? "local", tenantId, StringComparison.Ordinal) && (resourceScope is null || resourceScope.Count == 0 || resourceScope.Contains(order.Id, StringComparer.Ordinal)))).AsEnumerable().Reverse().ToArray());
    public FollowUpTask? FindTaskByProposal(string proposalId) => database.Read(state => state.Tasks.FirstOrDefault(task => task.ProposalId == proposalId));
    public Order? FindOrder(string id, string tenantId = "local") => database.Read(state => state.Orders.FirstOrDefault(order => order.Id == id && string.Equals(order.TenantId ?? "local", tenantId, StringComparison.Ordinal)));

    public Order MarkPaid(string id, string tenantId = "local", string? providerCaptureId = null) => database.Update(state =>
    {
        var index = state.Orders.FindIndex(order => order.Id == id && string.Equals(order.TenantId ?? "local", tenantId, StringComparison.Ordinal));
        if (index < 0)
            throw new InvalidOperationException("Order no longer exists.");
        if (state.Orders[index].Status == "PAID")
        {
            if (!string.IsNullOrWhiteSpace(providerCaptureId) && state.Orders[index].ProviderCaptureId != providerCaptureId)
            {
                state.Orders[index] = state.Orders[index] with
                {
                    ProviderCaptureId = providerCaptureId
                };
            }
            return state.Orders[index];
        }
        state.Orders[index] = state.Orders[index] with
        {
            Status = "PAID",
            ProviderCaptureId = providerCaptureId ?? state.Orders[index].ProviderCaptureId
        };
        CommerceDatabase.Record(state, id, "capture.completed", "system", "PayPal confirmed capture.", "PayPal sandbox", state.Orders[index].ProviderOrderId, null, state.Orders[index].TenantId);
        return state.Orders[index];
    });

    public Order? ApplyProviderEvent(ProviderEvent providerEvent) => database.Update(state =>
    {
        var existingEvent = state.ProviderEvents.FirstOrDefault(item => item.EventId == providerEvent.EventId);
        if (existingEvent is not null)
            return state.Orders.FirstOrDefault(order => order.ProviderOrderId == existingEvent.ProviderOrderId && order.TenantId == existingEvent.TenantId);
        var index = state.Orders.FindIndex(order => order.ProviderOrderId == providerEvent.ProviderOrderId && string.Equals(order.TenantId ?? "local", providerEvent.TenantId, StringComparison.Ordinal));
        if (index < 0)
            throw new InvalidOperationException("Provider event does not reference a known local order.");
        state.ProviderEvents.Add(providerEvent);
        var order = state.Orders[index];
        if (providerEvent.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) && order.Status != "PAID")
        {
            order = order with
            {
                Status = "PAID",
                ProviderCaptureId = providerEvent.ProviderCaptureId ?? order.ProviderCaptureId
            };
            state.Orders[index] = order;
            CommerceDatabase.Record(state, order.Id, "capture.completed", "provider:webhook", "Provider event confirmed capture.", "PayPal sandbox", providerEvent.EventId, null, order.TenantId);
        }
        CommerceDatabase.Record(state, order.Id, "provider.event_applied", "system", "Provider event was applied idempotently.", "PayPal sandbox", providerEvent.EventId, null, order.TenantId);
        return order;
    });

    public Order BeginCapture(string id, string actor = "buyer", string tenantId = "local") => database.Update(state =>
    {
        var index = state.Orders.FindIndex(order => order.Id == id && string.Equals(order.TenantId ?? "local", tenantId, StringComparison.Ordinal));
        if (index < 0)
            throw new InvalidOperationException("Order no longer exists.");
        var order = state.Orders[index];
        if (order.Status == "PAID")
            return order;
        if (order.Status != "CAPTURE_PENDING")
        {
            order = order with
            {
                Status = "CAPTURE_PENDING"
            };
            state.Orders[index] = order;
            CommerceDatabase.Record(state, id, "capture.requested", actor, "Capture intent persisted before the provider call.", "PayPal sandbox", order.ProviderOrderId, null, order.TenantId);
        }
        return order;
    });

    public FollowUpTask CreateManualTask(string orderId, string description, string actor, string tenantId = "local") => database.Update(state =>
    {
        var order = state.Orders.FirstOrDefault(item => item.Id == orderId && item.Status == "PAID" && string.Equals(item.TenantId ?? "local", tenantId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Only completed payments can receive a follow-up task.");
        var task = new FollowUpTask(Guid.NewGuid().ToString("N"), "manual:" + Guid.NewGuid().ToString("N"), orderId, description, "OPEN");
        state.Tasks.Add(task);
        CommerceDatabase.Record(state, orderId, "task.created", actor, "Internal follow-up created manually under the human-only policy.", "local-task", task.Id, "follow-up-v" + PolicyManager.Current(state, order.TenantId).Version, order.TenantId);
        return task;
    });

    public Proposal? AddProposal(string id, string reason, string requestedBy, string agentActor) => database.Update<Proposal?>(state =>
    {
        var paidOrder = state.Orders.FirstOrDefault(order => order.Id == id && order.Status == "PAID");
        if (paidOrder is null)
            throw new InvalidOperationException("Only completed payments can be reviewed.");
        var policy = PolicyManager.Current(state, paidOrder.TenantId);
        var policyVersion = "follow-up-v" + policy.Version;
        if (policy.Mode is not ("automatic" or "approval_required"))
        {
            CommerceDatabase.Record(state, id, "policy.denied", agentActor, "Agent follow-up denied by policy mode " + policy.Mode + ".", "gatekeeper", PolicyManager.FollowUpAction, policyVersion, paidOrder.TenantId);
            return null;
        }
        var proposal = new Proposal(Guid.NewGuid().ToString("N"), id, reason, policy.Mode == "automatic" ? "TASK_CREATED" : "PENDING", policy.Version, paidOrder.TenantId);
        state.Proposals.Add(proposal);
        CommerceDatabase.Record(state, id, "review.requested", requestedBy, "Requested an AI review.", "local-auth", proposal.Id, null, paidOrder.TenantId);
        CommerceDatabase.Record(state, id, "proposal.created", agentActor, "AI proposed an internal follow-up: " + reason, "openai", proposal.Id, null, paidOrder.TenantId);
        CommerceDatabase.Record(state, id, "policy.evaluated", "gatekeeper", "Follow-up policy decision: " + policy.Mode + ".", "gatekeeper", proposal.Id, policyVersion, paidOrder.TenantId);
        if (policy.Mode == "automatic")
        {
            var task = new FollowUpTask(Guid.NewGuid().ToString("N"), proposal.Id, id, reason, "OPEN");
            state.Tasks.Add(task);
            CommerceDatabase.Record(state, id, "task.created", "system", "Internal follow-up created automatically under the configured policy.", "local-task", task.Id, policyVersion, paidOrder.TenantId);
        }
        return proposal;
    });

    public Proposal? Decide(string id, string decision, string actor, string rationale, string tenantId = "local") => database.Update(state =>
    {
        var index = state.Proposals.FindIndex(proposal => proposal.Id == id && string.Equals(proposal.TenantId ?? "local", tenantId, StringComparison.Ordinal));
        if (index < 0 || state.Proposals[index].Status != "PENDING" || !state.Orders.Any(order => order.Id == state.Proposals[index].OrderId && order.Status == "PAID" && string.Equals(order.TenantId ?? "local", tenantId, StringComparison.Ordinal)))
            return null;
        if (decision is not ("approve" or "reject"))
            throw new ArgumentException("Decision must be approve or reject.", nameof(decision));

        var proposal = state.Proposals[index];
        var policy = PolicyManager.Current(state, proposal.TenantId);
        var policyVersion = "follow-up-v" + policy.Version;
        if (policy.Mode != "approval_required" || proposal.PolicyVersion != policy.Version)
        {
            CommerceDatabase.Record(state, proposal.OrderId, "approval.stale", actor, "The policy changed after this proposal; a new review is required.", "gatekeeper", proposal.Id, policyVersion, proposal.TenantId);
            return null;
        }
        FollowUpTask? task = null;
        if (decision == "approve")
        {
            task = new FollowUpTask(Guid.NewGuid().ToString("N"), proposal.Id, proposal.OrderId, proposal.Reason, "OPEN");
            state.Tasks.Add(task);
        }
        state.Proposals[index] = proposal with
        {
            Status = decision == "approve" ? "TASK_CREATED" : "REJECTED"
        };
        CommerceDatabase.Record(state, proposal.OrderId, "approval." + decision, actor, "Decision: " + decision + ". Operator reason: " + rationale, "operator", proposal.Id, policyVersion, proposal.TenantId);
        if (task is not null)
            CommerceDatabase.Record(state, proposal.OrderId, "task.created", "system", "Internal follow-up created after approval; no external communication sent.", "local-task", task.Id, policyVersion, proposal.TenantId);
        return state.Proposals[index];
    });
}

public sealed class AuditLog(CommerceDatabase database)
{
    public void Write(string orderId, string type, string actor, string reason, string source, string? reference, string? policyVersion = null, string tenantId = "local") =>
        database.Update(state =>
        {
            CommerceDatabase.Record(state, orderId, type, actor, reason, source, reference, policyVersion, tenantId);
            return true;
        });

    public IReadOnlyList<AuditEvent> Read(string tenantId = "local") =>
        database.Read(state => (IReadOnlyList<AuditEvent>)state.Audit.Where(entry => string.Equals(entry.TenantId ?? "local", tenantId, StringComparison.Ordinal)).AsEnumerable().Reverse().Take(200).ToArray());

    public IReadOnlyList<AuditEvent> ExportAll() =>
        database.Read(state => (IReadOnlyList<AuditEvent>)state.Audit.ToArray());

    public AuditVerification Verify() => database.Read(state => CommerceDatabase.VerifyAudit(state.Audit));
}
