using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using PayPal360;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }

var catalog = new DemoCatalog();
Check(catalog.List().Count == 2 && catalog.Find("p1")?.Price == 49m && catalog.Find("missing") is null, "catalog resolves only known products");
var store = new DemoStore();
var order = store.AddOrder(catalog.Find("p1")!, "SANDBOX-ORDER");
var proposal = store.AddProposal(order.Id, "Check fulfillment");
Check(store.Decide(proposal.Id, "approve") is null, "unpaid orders cannot be approved");
store.MarkPaid(order.Id);
Check(store.Decide(proposal.Id, "approve")?.Status == "TASK_CREATED", "paid order can receive an approved internal task");
Check(store.Decide(proposal.Id, "approve") is null, "a proposal cannot be approved twice");
var second = store.AddProposal(order.Id, "Check address");
Check(store.Decide(second.Id, "reject")?.Status == "REJECTED", "rejected proposal creates no task");

var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
    ["PAYPAL_CLIENT_ID"] = "test-client", ["PAYPAL_CLIENT_SECRET"] = "test-secret",
    ["OPENAI_API_KEY"] = "test-key", ["OPENAI_MODEL"] = "test-model"
}).Build();
var handler = new FakeHandler();
var clients = new FakeFactory(handler);
var paypal = new PayPalSandboxProvider(clients, settings);
var created = await paypal.CreateOrder(catalog.Find("p1")!, CancellationToken.None);
Check(created.Id == "SANDBOX-ORDER", "sandbox adapter parses created order");
Check(handler.LastUrl == "https://api-m.sandbox.paypal.com/v2/checkout/orders" && handler.LastBody.Contains("49.00") && handler.LastBody.Contains("PLN"), "order request uses sandbox and catalog price");
await paypal.CaptureOrder(created.Id, CancellationToken.None);
Check(handler.LastUrl.EndsWith("/SANDBOX-ORDER/capture") && handler.LastRequestId == "capture-SANDBOX-ORDER", "capture uses a repeatable idempotency key");
var ai = new AiReview(clients, settings);
var reason = await ai.Suggest(store.FindOrder(order.Id)!, CancellationToken.None);
Check(reason == "Review fulfillment." && handler.LastUrl == "https://api.openai.com/v1/responses", "AI adapter extracts response text");
Console.WriteLine("All checks passed.");

sealed class FakeFactory(FakeHandler handler) : IHttpClientFactory {
    public HttpClient CreateClient(string name) => new(handler, false);
}
sealed class FakeHandler : HttpMessageHandler {
    public string LastUrl = "", LastBody = "", LastRequestId = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        LastUrl = request.RequestUri!.ToString();
        LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        LastRequestId = request.Headers.TryGetValues("PayPal-Request-Id", out var ids) ? ids.First() : "";
        var json = LastUrl.EndsWith("/v1/oauth2/token") ? "{\"access_token\":\"token\"}" :
            LastUrl.Contains("api.openai.com") ? "{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"Review fulfillment.\"}]}]}" :
            "{\"id\":\"SANDBOX-ORDER\",\"status\":\"COMPLETED\"}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
