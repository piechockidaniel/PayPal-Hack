using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PayPal360.Contracts;
using PayPal360.Contracts.Exceptions;
using PayPal360.Contracts.Providers;

namespace PayPal360;

public sealed class PayPalSandboxProvider(IHttpClientFactory clients, IConfiguration config)
    : IPaymentProvider, IRefundProvider
{
    private const string BaseUrl = "https://api-m.sandbox.paypal.com";

    private async Task<string> Token(CancellationToken ct)
    {
        var id = AppConfiguration.PayPalClientId(config);
        var secret = AppConfiguration.PayPalClientSecret(config);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
            throw new ProviderException("PayPal sandbox credentials are not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/v1/oauth2/token");

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret))
        );


        request.Content = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["grant_type"] = "client_credentials" }
        );

        using var response = await clients.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException(
                "PayPal sandbox authentication failed. Check the sandbox credentials."
            );
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    public async Task<string> VerifyConnectionAsync(CancellationToken cancellationToken)
    {
        await Token(cancellationToken);
        return "PayPal Sandbox credentials were accepted; no order or payment operation was created.";
    }

    private async Task<ProviderResult> Send(
        string path,
        object body,
        string requestId,
        CancellationToken ct
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token(ct));
        request.Headers.Add("PayPal-Request-Id", requestId);
        request.Content = JsonContent.Create(body);
        using var response = await clients.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException(
                "PayPal sandbox request failed (HTTP " + (int)response.StatusCode + ")."
            );
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return new ProviderResult(
            root.GetProperty("id").GetString()!,
            root.TryGetProperty("status", out var status)
                ? status.GetString() ?? "UNKNOWN"
                : "UNKNOWN",
            TryGetCaptureId(root)
        );
    }

    public Task<ProviderResult> CreateOrder(Product p, string requestId, CancellationToken ct)
    {
        var payload = new
        {
            intent = "CAPTURE",
            purchase_units = new[]
            {
                new
                {
                    amount = new
                    {
                        currency_code = p.Currency,
                        value = p.Price.ToString("F2", CultureInfo.InvariantCulture),
                    },
                    description = p.Name,
                },
            },
        };
        return Send("/v2/checkout/orders", payload, requestId, ct);
    }

    public Task<ProviderResult> CaptureOrder(string providerId, CancellationToken ct) =>
        Send(
            "/v2/checkout/orders/" + Uri.EscapeDataString(providerId) + "/capture",
            new
            {
            },
            "capture-" + providerId,
            ct
        );

    public async Task<ProviderResult> GetOrder(string providerId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BaseUrl + "/v2/checkout/orders/" + Uri.EscapeDataString(providerId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token(ct));
        using var response = await clients.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException(
                "PayPal sandbox order lookup failed (HTTP " + (int)response.StatusCode + ").");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return new ProviderResult(
            root.GetProperty("id").GetString()!,
            root.TryGetProperty("status", out var status)
                ? status.GetString() ?? "UNKNOWN"
                : "UNKNOWN",
            TryGetCaptureId(root));
    }

    public async Task<ProviderActionResult> RefundAsync(ProviderActionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderReference))
            throw new ProviderException("A PayPal capture reference is required for a refund.");

        object payload = request.Amount is { } amount
            ? new
            {
                amount = new
                {
                    currency_code = request.Currency ?? "PLN",
                    value = amount.ToString("F2", CultureInfo.InvariantCulture)
                }
            }
            : new
            {
            };
        var result = await Send(
            "/v2/payments/captures/" + Uri.EscapeDataString(request.ProviderReference) + "/refund",
            payload,
            request.IdempotencyKey,
            cancellationToken);
        var complete = result.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase);
        return new ProviderActionResult(
            result.Status,
            result.Id,
            request.IdempotencyKey,
            !complete,
            "PayPal refund provider result: " + result.Status + ".");
    }

    public async Task<ProviderReconciliationResult> ReconcileRefundAsync(ProviderReconciliationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderReference))
            throw new ProviderException("A PayPal refund reference is required for reconciliation.");

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            BaseUrl + "/v2/payments/refunds/" + Uri.EscapeDataString(request.ProviderReference));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token(cancellationToken));
        using var response = await clients.CreateClient().SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException("PayPal sandbox refund lookup failed (HTTP " + (int)response.StatusCode + ").");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : request.ProviderReference;
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? "UNKNOWN" : "UNKNOWN";
        return new ProviderReconciliationResult(
            status,
            id,
            status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase),
            "PayPal refund lookup returned status " + status + ".");
    }

    private static string? TryGetCaptureId(JsonElement root)
    {
        if (!root.TryGetProperty("purchase_units", out var units) || units.ValueKind != JsonValueKind.Array || units.GetArrayLength() == 0)
            return null;
        var unit = units[0];
        if (!unit.TryGetProperty("payments", out var payments) || !payments.TryGetProperty("captures", out var captures) || captures.ValueKind != JsonValueKind.Array || captures.GetArrayLength() == 0)
            return null;
        return captures[0].TryGetProperty("id", out var captureId) ? captureId.GetString() : null;
    }

    public async Task<bool> VerifyWebhookAsync(PayPalWebhookHeaders headers, string rawBody, string webhookId, CancellationToken ct)
    {
        if (!DateTimeOffset.TryParse(headers.TransmissionTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var transmissionTime) ||
            Math.Abs((DateTimeOffset.UtcNow - transmissionTime).TotalMinutes) > 5)
            return false;
        if (!Uri.TryCreate(headers.CertUrl, UriKind.Absolute, out var certificateUri) ||
            certificateUri.Scheme != Uri.UriSchemeHttps ||
            !(certificateUri.Host.Equals("paypal.com", StringComparison.OrdinalIgnoreCase) ||
              certificateUri.Host.EndsWith(".paypal.com", StringComparison.OrdinalIgnoreCase)))
            return false;
        using var eventDocument = JsonDocument.Parse(rawBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/v1/notifications/verify-webhook-signature");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token(ct));
        request.Content = JsonContent.Create(new
        {
            auth_algo = headers.AuthAlgorithm,
            cert_url = headers.CertUrl,
            transmission_id = headers.TransmissionId,
            transmission_sig = headers.TransmissionSignature,
            transmission_time = headers.TransmissionTime,
            webhook_id = webhookId,
            webhook_event = eventDocument.RootElement.Clone()
        });
        using var response = await clients.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return false;
        using var verification = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return verification.RootElement.TryGetProperty("verification_status", out var status) &&
            string.Equals(status.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AiReview(IHttpClientFactory clients, IConfiguration config)
{
    public const string PromptVersion = "order-followup-v1";

    public async Task<AiReviewSuggestion> Suggest(Order order, CancellationToken ct)
    {
        var key = AppConfiguration.OpenAiApiKey(config);
        var model = AppConfiguration.OpenAiModel(config);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model))
            throw new ProviderException("OpenAI API key and model are not configured.");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.openai.com/v1/responses"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(
            new
            {
                model,
                max_output_tokens = 500,
                instructions = "You are reviewing a PayPal Sandbox commerce order for internal merchant triage. Return only the requested structured review. Recommend at most one internal follow-up. Use only the supplied order facts. Do not claim that shipping, fraud checks, address verification, inventory changes, customer communications, refunds, fulfillment, or any other external action occurred. You cannot execute actions.",
                input = "Product: "
                    + order.ProductName
                    + ". Payment status: "
                    + order.Status
                    + ". Amount: "
                    + order.Price.ToString("F2", CultureInfo.InvariantCulture)
                    + " "
                    + order.Currency
                    + ". Shipping status: not available.",
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "commerce_followup_review",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                recommendation = new { type = "string" },
                                category = new
                                {
                                    type = "string",
                                    @enum = new[] { "manual_follow_up", "payment_review", "inventory_review", "no_action" }
                                },
                                confidence = new { type = "number" },
                                evidence = new { type = "array", items = new { type = "string" } },
                                risk_flags = new { type = "array", items = new { type = "string" } }
                            },
                            required = new[] { "recommendation", "category", "confidence", "evidence", "risk_flags" },
                            additionalProperties = false
                        }
                    }
                }
            }
        );
        using var response = await clients.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException(
                "AI review failed (HTTP "
                    + (int)response.StatusCode
                    + "). Check the model and API configuration."
            );
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var parts = new List<string>();
        if (doc.RootElement.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            parts.Add(outputText.GetString() ?? "");
        if (
            parts.Count == 0
            && doc.RootElement.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Array
        )
            foreach (var item in output.EnumerateArray())
                if (
                    item.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array
                )
                    foreach (var part in content.EnumerateArray())
                        if (
                            part.TryGetProperty("type", out var type)
                            && type.GetString() == "output_text"
                            && part.TryGetProperty("text", out var text)
                        )
                            parts.Add(text.GetString() ?? "");
        var reason = string.Join(" ", parts).Trim();
        if (reason.Length == 0)
            throw new ProviderException("AI returned no review text.");
        AiReviewPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AiReviewPayload>(reason, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            throw new ProviderException("AI returned an invalid structured review.");
        }
        if (payload is null || string.IsNullOrWhiteSpace(payload.Recommendation) || string.IsNullOrWhiteSpace(payload.Category) ||
            payload.Confidence is < 0 or > 1 || payload.Evidence is null || payload.RiskFlags is null)
            throw new ProviderException("AI returned an incomplete structured review.");
        if (payload.Recommendation.Length > 1000 || payload.Evidence.Count > 6 || payload.RiskFlags.Count > 6 ||
            payload.Evidence.Any(item => item.Length > 240) || payload.RiskFlags.Any(item => item.Length > 240))
            throw new ProviderException("AI returned a structured review outside the allowed limits.");
        var category = payload.Category.Trim().ToLowerInvariant();
        if (category is not ("manual_follow_up" or "payment_review" or "inventory_review" or "no_action"))
            throw new ProviderException("AI returned an unsupported review category.");
        return new AiReviewSuggestion(
            payload.Recommendation.Trim(),
            category,
            payload.Confidence,
            payload.Evidence.Select(item => item.Trim()).Where(item => item.Length > 0).ToArray(),
            payload.RiskFlags.Select(item => item.Trim()).Where(item => item.Length > 0).ToArray(),
            "openai",
            model,
            PromptVersion);
    }

    private sealed record AiReviewPayload(
        string Recommendation,
        string Category,
        decimal Confidence,
        IReadOnlyList<string>? Evidence,
        [property: System.Text.Json.Serialization.JsonPropertyName("risk_flags")] IReadOnlyList<string>? RiskFlags);
}
