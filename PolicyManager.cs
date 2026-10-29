namespace PayPal360;

public record PolicyRule(string Action, string Mode, int Version, string TenantId = "local");

public sealed class PolicyManager(CommerceDatabase database)
{
    public const string FollowUpAction = "create_internal_followup";
    public static readonly IReadOnlySet<string> KnownActions = new HashSet<string>(StringComparer.Ordinal)
    {
        FollowUpAction,
        "capture_payment",
        "refund_payment",
        "fulfill_order",
        "publish_product"
    };
    private static readonly HashSet<string> Modes = ["automatic", "approval_required", "human_only", "blocked"];

    public static PolicyRule Current(CommerceState state, string tenantId = "local") => Current(state, FollowUpAction, tenantId);

    public static PolicyRule Current(CommerceState state, string action, string tenantId = "local") =>
        state.Policies.FirstOrDefault(rule => rule.Action == action && string.Equals(rule.TenantId ?? "local", tenantId, StringComparison.Ordinal))
        ?? new PolicyRule(action,
            action == FollowUpAction || action == "refund_payment" ? "approval_required" : "blocked",
            1,
            tenantId);

    public PolicyRule GetFollowUp(string tenantId = "local") => database.Read(state => Current(state, tenantId));

    public IReadOnlyList<PolicyRule> List(string tenantId = "local") => database.Read(state =>
        KnownActions.Select(action => Current(state, action, tenantId)).OrderBy(rule => rule.Action, StringComparer.Ordinal).ToArray());

    public PolicyRule Get(string action, string tenantId = "local")
    {
        ValidateAction(action);
        return database.Read(state => Current(state, action, tenantId));
    }

    public PolicyRule UpdateFollowUp(string mode, string actor, string reason, string tenantId = "local")
        => Update(FollowUpAction, mode, actor, reason, tenantId);

    public PolicyRule Update(string action, string mode, string actor, string reason, string tenantId = "local")
    {
        ValidateAction(action);
        if (!Modes.Contains(mode))
            throw new ArgumentException("Unknown policy mode.");
        if (action == "refund_payment" && mode == "automatic")
            throw new ArgumentException("Refunds require human approval.");
        return database.Update(state =>
        {
            var previous = Current(state, action, tenantId);
            var next = previous with
            {
                Mode = mode,
                Version = checked(previous.Version + 1),
                TenantId = tenantId
            };
            state.Policies.RemoveAll(rule => rule.Action == action && string.Equals(rule.TenantId ?? "local", tenantId, StringComparison.Ordinal));
            state.Policies.Add(next);
            CommerceDatabase.Record(state, "-", "policy.updated", actor,
                "Changed " + action + " from " + previous.Mode + " to " + mode + ". Reason: " + reason,
                "policy", action, action + "-v" + next.Version, tenantId);
            return next;
        });
    }

    private static void ValidateAction(string action)
    {
        if (!KnownActions.Contains(action))
            throw new ArgumentException("Unknown policy action.");
    }
}

