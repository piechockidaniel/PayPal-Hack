using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayPal360;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<DemoStore>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<IProductCatalog, DemoCatalog>();
builder.Services.AddSingleton<IPaymentProvider, PayPalSandboxProvider>();
builder.Services.AddSingleton<AiReview>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (IConfiguration c) => new {
    paypal = !string.IsNullOrWhiteSpace(c["PAYPAL_CLIENT_ID"]) && !string.IsNullOrWhiteSpace(c["PAYPAL_CLIENT_SECRET"]),
    clientId = c["PAYPAL_CLIENT_ID"] ?? "",
    ai = !string.IsNullOrWhiteSpace(c["OPENAI_API_KEY"]) && !string.IsNullOrWhiteSpace(c["OPENAI_MODEL"]),
    operatorConfigured = !string.IsNullOrWhiteSpace(c["PAYPAL360_OPERATOR_TOKEN"]),
    environment = "PayPal sandbox only"
});
app.MapGet("/api/connectors", (IConfiguration c) => new[] {
    new { role = "Catalog", provider = "Local demo catalog", status = "available", detail = "Read-only sample data" },
    new { role = "Payments", provider = "PayPal Orders API", status = string.IsNullOrWhiteSpace(c["PAYPAL_CLIENT_ID"]) || string.IsNullOrWhiteSpace(c["PAYPAL_CLIENT_SECRET"]) ? "not-configured" : "configured-not-verified", detail = "Sandbox only; verify with a test checkout" },
    new { role = "Invoicing", provider = "PayPal", status = "unverified", detail = "Adapter not implemented" },
    new { role = "Product channel", provider = "Google Merchant Center", status = "unverified", detail = "Adapter not implemented" },
    new { role = "Shipping", provider = "Unassigned", status = "unavailable", detail = "No connector configured" }
});
app.MapGet("/api/products", (IProductCatalog catalog) => catalog.List());
app.MapGet("/api/orders", (DemoStore store) => store.ListOrders());
app.MapGet("/api/proposals", (DemoStore store) => store.ListProposals());
app.MapGet("/api/audit", (AuditLog audit) => audit.Read());

app.MapPost("/api/checkout", async (CheckoutRequest body, IProductCatalog catalog, IPaymentProvider provider, DemoStore store, AuditLog audit, CancellationToken ct) => {
    var product = catalog.Find(body.ProductId);
    if (product is null) return Results.BadRequest(new { error = "Unknown product." });
    try {
        var external = await provider.CreateOrder(product, ct);
        var order = store.AddOrder(product, external.Id);
        audit.Write(order.Id, "checkout.created", "buyer", "PayPal sandbox order created from server-side product price.", "PayPal sandbox", external.Id);
        return Results.Ok(new { orderId = order.Id, paypalOrderId = external.Id });
    } catch (ProviderException e) {
        audit.Write("-", "checkout.failed", "buyer", "Sandbox order creation failed.", "PayPal sandbox", null);
        return Results.Problem(e.Message, statusCode: 502);
    }
});
app.MapPost("/api/checkout/{id}/capture", async (string id, IPaymentProvider provider, DemoStore store, AuditLog audit, CancellationToken ct) => {
    var order = store.FindOrder(id);
    if (order is null) return Results.NotFound();
    if (order.Status == "PAID") return Results.Ok(order);
    try {
        var result = await provider.CaptureOrder(order.ProviderOrderId, ct);
        if (result.Status != "COMPLETED") {
            audit.Write(id, "capture.pending", "system", "PayPal returned status " + result.Status, "PayPal sandbox", result.Id);
            return Results.Conflict(new { error = "Payment is not completed: " + result.Status });
        }
        var updated = store.MarkPaid(id);
        audit.Write(id, "capture.completed", "system", "Buyer approved; PayPal confirmed capture.", "PayPal sandbox", result.Id);
        return Results.Ok(updated);
    } catch (ProviderException e) {
        audit.Write(id, "capture.failed", "system", "PayPal capture failed.", "PayPal sandbox", order.ProviderOrderId);
        return Results.Problem(e.Message, statusCode: 502);
    }
});
app.MapPost("/api/orders/{id}/review", async (string id, DemoStore store, AiReview ai, AuditLog audit, CancellationToken ct) => {
    var order = store.FindOrder(id);
    if (order is null) return Results.NotFound();
    if (order.Status != "PAID") return Results.Conflict(new { error = "Only completed payments can be reviewed." });
    try {
        var reason = await ai.Suggest(order, ct);
        var proposal = store.AddProposal(order.Id, reason);
        audit.Write(order.Id, "proposal.created", "ai-agent", "AI proposed an internal follow-up: " + reason, "openai", proposal.Id);
        audit.Write(order.Id, "policy.evaluated", "gatekeeper", "Policy follow-up-v1: human approval required before creating an internal task.", "approval-required", proposal.Id);
        return Results.Ok(proposal);
    } catch (ProviderException e) {
        audit.Write(id, "review.failed", "ai-agent", "AI review was unavailable.", "openai", null);
        return Results.Problem(e.Message, statusCode: 502);
    }
});
app.MapPost("/api/proposals/{id}/decision", (string id, DecisionRequest input, HttpRequest req, IConfiguration c, DemoStore store, AuditLog audit) => {
    var expected = c["PAYPAL360_OPERATOR_TOKEN"];
    var presented = req.Headers["X-Operator-Token"].ToString();
    if (string.IsNullOrWhiteSpace(expected) || !TokensEqual(expected, presented)) return Results.Unauthorized();
    var actor = (input.Operator ?? "").Trim();
    var rationale = (input.Reason ?? "").Trim();
    if (actor.Length is < 1 or > 80 || rationale.Length is < 1 or > 500 || input.Decision is not ("approve" or "reject")) return Results.BadRequest(new { error = "Operator, decision, and reason are required." });
    var outcome = store.Decide(id, input.Decision);
    if (outcome is null) return Results.Conflict(new { error = "Proposal is missing, already decided, or the order is no longer eligible." });
    audit.Write(outcome.OrderId, "approval." + input.Decision, "operator (self-reported): " + actor, "Policy follow-up-v1; human decision: " + input.Decision + ". Operator reason: " + rationale, "operator-token", outcome.Id);
    if (input.Decision == "approve")
        audit.Write(outcome.OrderId, "task.created", "system", "Internal follow-up created after approval; no external communication sent.", "local-task", outcome.Id);
    return Results.Ok(outcome);
});
app.Run();

static bool TokensEqual(string a, string b) {
    var aa = Encoding.UTF8.GetBytes(a); var bb = Encoding.UTF8.GetBytes(b);
    return aa.Length == bb.Length && CryptographicOperations.FixedTimeEquals(aa, bb);
}
record CheckoutRequest(string ProductId);
record DecisionRequest(string? Operator, string Decision, string? Reason);



