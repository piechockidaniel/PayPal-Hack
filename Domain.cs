using System.Text.Json;

namespace PayPal360;
public record Product(string Id, string Name, decimal Price, string Currency);
public record Order(string Id, string ProductId, string ProductName, decimal Price, string Currency, string ProviderOrderId, string Status);
public record Proposal(string Id, string OrderId, string Reason, string Status);
public record ProviderResult(string Id, string Status);
public interface IProductCatalog { IReadOnlyList<Product> List(); Product? Find(string id); }
public interface IPaymentProvider {
    Task<ProviderResult> CreateOrder(Product product, CancellationToken ct);
    Task<ProviderResult> CaptureOrder(string providerId, CancellationToken ct);
}
public sealed class DemoCatalog : IProductCatalog {
    private static readonly Product[] Products = [new("p1", "PayPal 360 demo product", 49m, "PLN"), new("p2", "Creative consultation", 120m, "PLN")];
    public IReadOnlyList<Product> List() => Products.ToArray();
    public Product? Find(string id) => Products.FirstOrDefault(p => p.Id == id);
}
public sealed class DemoStore {
    private readonly object gate = new();
    private readonly List<Order> orders = [];
    private readonly List<Proposal> proposals = [];
    public Order AddOrder(Product p, string externalId) { lock (gate) { var o = new Order(Guid.NewGuid().ToString("N"), p.Id, p.Name, p.Price, p.Currency, externalId, "CREATED"); orders.Add(o); return o; } }
    public IReadOnlyList<Order> ListOrders() { lock (gate) return orders.AsEnumerable().Reverse().ToArray(); }
    public IReadOnlyList<Proposal> ListProposals() { lock (gate) return proposals.AsEnumerable().Reverse().ToArray(); }
    public Order? FindOrder(string id) { lock (gate) return orders.FirstOrDefault(o => o.Id == id); }
    public Order MarkPaid(string id) { lock (gate) { var i = orders.FindIndex(o => o.Id == id); orders[i] = orders[i] with { Status = "PAID" }; return orders[i]; } }
    public Proposal AddProposal(string id, string reason) { lock (gate) { var p = new Proposal(Guid.NewGuid().ToString("N"), id, reason, "PENDING"); proposals.Add(p); return p; } }
    public Proposal? Decide(string id, string decision) {
        lock (gate) {
            var i = proposals.FindIndex(p => p.Id == id);
            if (i < 0 || proposals[i].Status != "PENDING" || !orders.Any(o => o.Id == proposals[i].OrderId && o.Status == "PAID")) return null;
            proposals[i] = proposals[i] with { Status = decision == "approve" ? "TASK_CREATED" : "REJECTED" };
            return proposals[i];
        }
    }
}
public record AuditEvent(DateTimeOffset At, string OrderId, string Event, string Actor, string Reason, string Source, string? Reference);
public sealed class AuditLog(IHostEnvironment environment) {
    private readonly object gate = new();
    private readonly string path = Path.Combine(environment.ContentRootPath, "data", "audit.ndjson");
    public void Write(string orderId, string type, string actor, string reason, string source, string? reference) {
        lock (gate) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.AppendAllText(path, JsonSerializer.Serialize(new AuditEvent(DateTimeOffset.UtcNow, orderId, type, actor, reason, source, reference)) + Environment.NewLine); }
    }
    public IReadOnlyList<AuditEvent> Read() {
        lock (gate) {
            if (!File.Exists(path)) return [];
            return File.ReadLines(path).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => JsonSerializer.Deserialize<AuditEvent>(s)).Where(e => e is not null).Cast<AuditEvent>().Reverse().Take(200).ToArray();
        }
    }
}
public sealed class ProviderException(string message) : Exception(message);


