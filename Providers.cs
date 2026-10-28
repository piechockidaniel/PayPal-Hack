using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PayPal360;

public sealed class PayPalSandboxProvider(IHttpClientFactory clients, IConfiguration config) : IPaymentProvider {
    private const string BaseUrl = "https://api-m.sandbox.paypal.com";
    private async Task<string> Token(CancellationToken ct) {
        var id = config["PAYPAL_CLIENT_ID"];
        var secret = config["PAYPAL_CLIENT_SECRET"];
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) throw new ProviderException("PayPal sandbox credentials are not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/v1/oauth2/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret)));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
        using var response = await clients.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ProviderException("PayPal sandbox authentication failed. Check the sandbox credentials.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }
    private async Task<ProviderResult> Send(string path, object body, string requestId, CancellationToken ct) {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token(ct));
        request.Headers.Add("PayPal-Request-Id", requestId);
        request.Content = JsonContent.Create(body);
        using var response = await clients.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ProviderException("PayPal sandbox request failed (HTTP " + (int)response.StatusCode + ").");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return new ProviderResult(root.GetProperty("id").GetString()!, root.TryGetProperty("status", out var status) ? status.GetString() ?? "UNKNOWN" : "UNKNOWN");
    }
    public Task<ProviderResult> CreateOrder(Product p, CancellationToken ct) {
        var payload = new { intent = "CAPTURE", purchase_units = new[] { new { amount = new { currency_code = p.Currency, value = p.Price.ToString("F2", CultureInfo.InvariantCulture) }, description = p.Name } } };
        return Send("/v2/checkout/orders", payload, Guid.NewGuid().ToString(), ct);
    }
    public Task<ProviderResult> CaptureOrder(string providerId, CancellationToken ct) =>
        Send("/v2/checkout/orders/" + Uri.EscapeDataString(providerId) + "/capture", new { }, "capture-" + providerId, ct);
}

public sealed class AiReview(IHttpClientFactory clients, IConfiguration config) {
    public async Task<string> Suggest(Order order, CancellationToken ct) {
        var key = config["OPENAI_API_KEY"];
        var model = config["OPENAI_MODEL"];
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model)) throw new ProviderException("OpenAI API key and model are not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new {
            model,
            instructions = "You are reviewing a sandbox commerce order. In one short sentence recommend a sensible internal follow-up for the merchant. Do not claim that shipping or customer communications occurred. You cannot execute actions.",
            input = "Product: " + order.ProductName + ". Payment status: " + order.Status + ". Shipping status: not available."
        });
        using var response = await clients.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ProviderException("AI review failed (HTTP " + (int)response.StatusCode + "). Check the model and API configuration.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var parts = new List<string>();
        if (doc.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            foreach (var item in output.EnumerateArray())
                if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text" && part.TryGetProperty("text", out var text)) parts.Add(text.GetString() ?? "");
        var reason = string.Join(" ", parts).Trim();
        if (reason.Length == 0) throw new ProviderException("AI returned no review text.");
        return reason.Length <= 1000 ? reason : reason[..1000];
    }
}
