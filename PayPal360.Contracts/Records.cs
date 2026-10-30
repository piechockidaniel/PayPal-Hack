namespace PayPal360.Contracts;

public record Product(string Id, string Name, decimal Price, string Currency);
public record Order(string Id, string ProductId, string ProductName, decimal Price, string Currency, string ProviderOrderId, string Status, string TenantId = "local", string? ProviderCaptureId = null);
public record AiReviewSuggestion(
    string Recommendation,
    string Category,
    decimal Confidence,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> RiskFlags,
    string Provider,
    string Model,
    string PromptVersion);
public record Proposal(
    string Id,
    string OrderId,
    string Reason,
    string Status,
    int PolicyVersion = 1,
    string TenantId = "local",
    string? Category = null,
    decimal? Confidence = null,
    IReadOnlyList<string>? Evidence = null,
    IReadOnlyList<string>? RiskFlags = null,
    string? Provider = null,
    string? Model = null,
    string? PromptVersion = null);
public record ProviderResult(string Id, string Status, string? CaptureId = null);

public record CommerceInvoice(string ExternalId, string Status);
public record Shipment(string ExternalId, string Status, string? TrackingNumber);
public record ProductPublication(string ProductId, string ChannelReference, string Status);
public record AuditEvent(
    DateTimeOffset At,
    string OrderId,
    string Event,
    string Actor,
    string Reason,
    string Source,
    string? Reference,
    string? EventId = null,
    string? PolicyVersion = null,
    string? PreviousHash = null,
    string? Hash = null,
    string TenantId = "local");

public record CreateRoleRequest(string? Name, string? Kind, string[]? Permissions, string? Operator, string? Reason);
public record CreateAgentRequest(string? Name, string? RoleId, string? ParentId, string? Operator, string? Reason, string[]? ResourceScope = null);

public record CheckoutRequest(string ProductId);
public record DecisionRequest(string? Operator, string Decision, string? Reason);



