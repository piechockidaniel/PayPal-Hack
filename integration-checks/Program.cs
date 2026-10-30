using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Text;
using Microsoft.Extensions.Configuration;

internal static class IntegrationChecks
{
    private static void Check(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    public static async Task Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "paypal360-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseContentRoot(root);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalAuth:PAYPAL360_ADMIN_USERNAME"] = "test-admin",
                ["LocalAuth:PAYPAL360_ADMIN_PASSWORD"] = "test-only-pass-12345",
                ["AgentAI:OPENAI_API_KEY"] = "test-key",
                ["AgentAI:OPENAI_MODEL"] = "test-model",
                ["PayPal:PAYPAL_CLIENT_ID"] = "test-client",
                ["PayPal:PAYPAL_CLIENT_SECRET"] = "test-secret",
                ["PayPal:PAYPAL_WEBHOOK_ID"] = "test-webhook",
                ["Audit:PAYPAL360_AUDIT_ANCHOR_SECRET"] = "0123456789abcdef0123456789abcdef"
            }));
            builder.ConfigureTestServices(services => { services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(new FakeAiFactory()); });
            builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        Check((await client.GetAsync("/api/products")).StatusCode == HttpStatusCode.OK, "public catalog remains available");
        var checkoutRequestId = Guid.NewGuid().ToString("D");
        var csrfResponse = await client.GetAsync("/api/auth/csrf");
        if (!csrfResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"CSRF endpoint returned {(int)csrfResponse.StatusCode}: {await csrfResponse.Content.ReadAsStringAsync()}");
        var checkoutCsrf = (await csrfResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var checkout = await Send(client, "/api/checkout", new
        {
            productId = "p1",
            requestId = checkoutRequestId
        }, checkoutCsrf);
        Check(checkout.StatusCode == HttpStatusCode.OK && checkout.Headers.TryGetValues("Set-Cookie", out var checkoutCookies) && checkoutCookies.Any(value => value.StartsWith("PayPal360.Checkout=", StringComparison.Ordinal)), "buyer checkout establishes a scoped HttpOnly checkout session");
        var checkoutBody = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var checkoutOrderId = checkoutBody.GetProperty("orderId").GetString();
        using (var mismatchedCheckoutClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }))
        {
            var mismatchedCsrf = (await mismatchedCheckoutClient.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("token").GetString()!;
            Check((await Send(mismatchedCheckoutClient, "/api/checkout", new
            {
                productId = "p1",
                requestId = checkoutRequestId
            }, mismatchedCsrf)).StatusCode == HttpStatusCode.Forbidden, "a checkout request cannot be replayed from a different checkout session");
        }
        Check((await Send(client, "/api/checkout/" + checkoutOrderId + "/capture", new
        {
        }, checkoutCsrf)).StatusCode == HttpStatusCode.OK, "checkout session can capture its own payment");
        using (var unboundCaptureClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }))
        {
            var unboundCsrf = (await unboundCaptureClient.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("token").GetString()!;
            Check((await Send(unboundCaptureClient, "/api/checkout/" + checkoutOrderId + "/capture", new
            {
            }, unboundCsrf)).StatusCode == HttpStatusCode.Forbidden, "a client without the checkout session cannot capture the payment");
        }
        Check((await client.GetAsync("http://nonlocal.example/api/products")).StatusCode == HttpStatusCode.Forbidden, "non-local host names are rejected");
        Check((await client.GetAsync("/api/orders")).StatusCode == HttpStatusCode.Unauthorized, "merchant data is blocked without a session");
        Check((await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "test-admin",
            password = "test-only-pass-12345"
        })).StatusCode == HttpStatusCode.BadRequest, "login requires a CSRF token");
        using (var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username = "test-admin", password = "test-only-pass-12345" })
        })
        {
            crossOrigin.Headers.Add("Origin", "https://evil.example");
            Check((await client.SendAsync(crossOrigin)).StatusCode == HttpStatusCode.Forbidden, "cross-origin mutations are rejected");
        }

        var anonymousCsrf = (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("token").GetString()!;
        var wrong = await Send(client, "/api/auth/login", new
        {
            username = "test-admin",
            password = "incorrect-password"
        }, anonymousCsrf);
        Check(wrong.StatusCode == HttpStatusCode.Unauthorized, "wrong password is rejected");
        var login = await Send(client, "/api/auth/login", new
        {
            username = "test-admin",
            password = "test-only-pass-12345"
        }, anonymousCsrf);
        Check(login.StatusCode == HttpStatusCode.OK, "local administrator can authenticate");
        var localUser = factory.Services.GetRequiredService<PayPal360.LocalAuth>().Verify("test-admin", "test-only-pass-12345");
        Check(localUser is not null && localUser.PasswordHash != "test-only-pass-12345", "administrator password is stored as a hash");
        var sessionCookie = login.Headers.GetValues("Set-Cookie").First(value => value.StartsWith("PayPal360.Session=", StringComparison.Ordinal)).Split(';')[0];
        Check(login.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(v => v.Contains("PayPal360.Session") && v.Contains("httponly", StringComparison.OrdinalIgnoreCase) && v.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)), "session cookie is HttpOnly and SameSite Strict");
        Check((await client.GetAsync("/api/orders")).StatusCode == HttpStatusCode.OK, "authenticated administrator can read merchant data");
        var auditVerification = await client.GetFromJsonAsync<JsonElement>("/api/audit/verify");
        Check(auditVerification.GetProperty("isValid").GetBoolean() && auditVerification.GetProperty("checkedEvents").GetInt32() > 0, "authenticated audit chain verification returns a valid result");
        Check((await client.GetAsync("/api/connectors/gmc/products")).StatusCode == HttpStatusCode.ServiceUnavailable, "unconfigured GMC connector remains read-only and visibly unavailable");
        Check((await client.PostAsJsonAsync("/api/roles", new
        {
            name = "reviewer",
            kind = "agent",
            permissions = new[] { "read_orders" },
            reason = "testing"
        })).StatusCode == HttpStatusCode.BadRequest, "authenticated writes require a CSRF token");

        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("token").GetString()!;
        var paypalVerification = await Send(client, "/api/connectors/paypal/verify", new
        {
        }, csrf);
        var paypalVerificationBody = await paypalVerification.Content.ReadFromJsonAsync<JsonElement>();
        Check(paypalVerification.StatusCode == HttpStatusCode.OK && paypalVerificationBody.GetProperty("status").GetString() == "verified", "GUI connector verification checks PayPal credentials without creating an order");
        var connectors = await client.GetFromJsonAsync<JsonElement[]>("/api/connectors");
        Check(connectors?.Any(item => item.GetProperty("id").GetString() == "paypal" && item.GetProperty("status").GetString() == "verified") == true, "connector registry persists the verified PayPal status");
        var created = await Send(client, "/api/roles", new
        {
            name = "reviewer",
            kind = "agent",
            permissions = new[] { "read_orders" },
            reason = "testing",
            @operator = "forged-actor"
        }, csrf);
        Check(created.StatusCode == HttpStatusCode.OK, "authorized role creation succeeds");
        var restrictedRole = await created.Content.ReadFromJsonAsync<JsonElement>();
        var restrictedAgent = await Send(client, "/api/agents", new
        {
            name = "restricted",
            roleId = restrictedRole.GetProperty("id").GetString(),
            parentId = (string?)null,
            reason = "testing"
        }, csrf);
        var restrictedAgentId = (await restrictedAgent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var store = factory.Services.GetRequiredService<PayPal360.DemoStore>();
        var testOrder = store.AddOrder(new PayPal360.Contracts.Product("item", "Item", 10m, "PLN"), "TEST-ORDER");
        store.MarkPaid(testOrder.Id, "local", "HTTP-CAPTURE");
        Check((await Send(client, "/api/policies/capture_payment", new
        {
            mode = "automatic",
            reason = "Enable the reviewed capture handler"
        }, csrf)).StatusCode == HttpStatusCode.OK, "administrator can configure the capture action policy");
        var captureAction = await Send(client, "/api/actions/capture_payment", new
        {
            resourceId = testOrder.Id,
            reason = "Capture the authorized payment"
        }, csrf);
        var captureActionBody = await captureAction.Content.ReadFromJsonAsync<JsonElement>();
        Check(captureAction.StatusCode == HttpStatusCode.OK && captureActionBody.GetProperty("status").GetString() == "COMPLETED", "policy-aware capture action executes through the HTTP gateway");
        Check((await Send(client, "/api/policies/refund_payment", new
        {
            mode = "automatic",
            reason = "Attempt to bypass human refund approval"
        }, csrf)).StatusCode == HttpStatusCode.BadRequest, "refund policy cannot be changed to automatic execution");
        var refundRequest = await Send(client, "/api/actions/refund_payment", new
        {
            resourceId = testOrder.Id,
            amount = 5m,
            reason = "Request a human-reviewed partial refund"
        }, csrf);
        var refundRequestBody = await refundRequest.Content.ReadFromJsonAsync<JsonElement>();
        Check(refundRequest.StatusCode == HttpStatusCode.Accepted && refundRequestBody.GetProperty("status").GetString() == "PENDING_APPROVAL", "refund action waits for human approval");
        var approvedRefund = await Send(client, "/api/actions/" + refundRequestBody.GetProperty("request").GetProperty("id").GetString() + "/decision", new
        {
            decision = "approve",
            reason = "Human reviewed the partial refund"
        }, csrf);
        Check(approvedRefund.StatusCode == HttpStatusCode.OK, "approved refund is executed through the provider boundary");
        var database = factory.Services.GetRequiredService<PayPal360.CommerceDatabase>();
        var uncertainRequest = database.Update(state =>
        {
            var request = new PayPal360.ActionRequest(
                "http-reconcile-action",
                "refund_payment",
                testOrder.Id,
                "PENDING_PROVIDER",
                1,
                "local",
                "user:operator",
                "Provider response was uncertain",
                DateTimeOffset.UtcNow,
                Amount: 1m,
                ProviderReference: "HTTP-REFUND",
                IdempotencyKey: "refund-http-reconcile",
                ReconciliationRequired: true);
            state.ActionRequests.Add(request);
            return request;
        });
        var reconciled = await Send(client, "/api/actions/" + uncertainRequest.Id + "/reconcile", new
        {
            reason = "Confirmed the provider refund status before closing the uncertainty"
        }, csrf);
        var reconciledBody = await reconciled.Content.ReadFromJsonAsync<JsonElement>();
        Check(reconciled.StatusCode == HttpStatusCode.OK
              && reconciledBody.GetProperty("request").GetProperty("reconciliationReason").GetString() == "Confirmed the provider refund status before closing the uncertainty"
              && factory.Services.GetRequiredService<PayPal360.AuditLog>().Read().Any(item => item.Event == "action.completed" && item.Reason.Contains("Confirmed the provider refund status before closing the uncertainty", StringComparison.Ordinal)),
            "HTTP reconciliation persists the operator rationale in the action and audit record");
        using (var webhook = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/paypal")
        {
            Content = new StringContent("{\"id\":\"webhook-event-1\",\"event_type\":\"PAYMENT.CAPTURE.COMPLETED\",\"resource\":{\"supplementary_data\":{\"related_ids\":{\"order_id\":\"TEST-ORDER\"}}}}", Encoding.UTF8, "application/json")
        })
        {
            webhook.Headers.Add("PAYPAL-TRANSMISSION-ID", "transmission-1");
            webhook.Headers.Add("PAYPAL-TRANSMISSION-TIME", DateTimeOffset.UtcNow.ToString("O"));
            webhook.Headers.Add("PAYPAL-CERT-URL", "https://api-m.sandbox.paypal.com/v1/notifications/certs/test");
            webhook.Headers.Add("PAYPAL-AUTH-ALGO", "SHA256withRSA");
            webhook.Headers.Add("PAYPAL-TRANSMISSION-SIG", "test-signature");
            Check((await client.SendAsync(webhook)).StatusCode == HttpStatusCode.Accepted, "verified PayPal webhook is accepted without a browser session");
        }
        for (var attempt = 0; attempt < 20 && !(factory.Services.GetRequiredService<PayPal360.AuditLog>().Read().Any(item => item.Event == "provider.event_applied")); attempt++)
            await Task.Delay(10);
        Check(factory.Services.GetRequiredService<PayPal360.AuditLog>().Read().Count(item => item.Event == "provider.event_applied") == 1, "verified webhook is applied by the idempotent worker");
        var deniedReview = await Send(client, "/api/orders/" + testOrder.Id + "/review", new
        {
            agentId = restrictedAgentId
        }, csrf);
        Check(deniedReview.StatusCode == HttpStatusCode.Forbidden, "agent without delegated proposal permission cannot invoke AI review");

        var fullRoleResponse = await Send(client, "/api/roles", new
        {
            name = "full-agent",
            kind = "agent",
            permissions = new[] { "read_orders", "propose_followup" },
            reason = "testing"
        }, csrf);
        var fullRoleId = (await fullRoleResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var fullAgentResponse = await Send(client, "/api/agents", new
        {
            name = "full",
            roleId = fullRoleId,
            parentId = (string?)null,
            reason = "testing"
        }, csrf);
        var fullAgentId = (await fullAgentResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var credentialResponse = await Send(client, "/api/agents/" + fullAgentId + "/credential", new
        {
            reason = "issue test credential"
        }, csrf);
        var credential = await credentialResponse.Content.ReadFromJsonAsync<JsonElement>();
        var agentToken = credential.GetProperty("token").GetString();
        using var agentClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        agentClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", agentToken);
        Check((await agentClient.GetAsync("/api/orders")).StatusCode == HttpStatusCode.OK, "authenticated agent can read tenant orders with its bearer credential");
        Check((await agentClient.PostAsJsonAsync("/api/orders/" + testOrder.Id + "/review", new
        {
            agentId = fullAgentId
        })).StatusCode == HttpStatusCode.OK, "authenticated agent can invoke only its delegated review action");
        var humanRoleResponse = await Send(client, "/api/roles", new
        {
            name = "read-only-human",
            kind = "human",
            permissions = new[] { "read_orders" },
            reason = "testing human assignment"
        }, csrf);
        var humanRoleId = (await humanRoleResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var userResponse = await Send(client, "/api/users", new
        {
            name = "reader",
            password = "reader-password-12345",
            roleIds = new[] { humanRoleId },
            reason = "testing human account"
        }, csrf);
        Check(userResponse.StatusCode == HttpStatusCode.OK, "administrator can create a tenant-scoped human account");
        using var readerClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var readerCsrf = (await readerClient.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("token").GetString()!;
        var readerLogin = await Send(readerClient, "/api/auth/login", new
        {
            username = "reader",
            password = "reader-password-12345"
        }, readerCsrf);
        Check(readerLogin.StatusCode == HttpStatusCode.OK && (await readerClient.GetAsync("/api/orders")).StatusCode == HttpStatusCode.OK, "assigned human account can read only its permitted data");
        Check((await readerClient.GetAsync("/api/policies/followup")).StatusCode == HttpStatusCode.Forbidden, "human role cannot access an unassigned policy capability");
        Check((await readerClient.GetAsync("/api/audit/export")).StatusCode == HttpStatusCode.Forbidden, "non-administrator cannot export the complete audit stream");
        var review = await Send(client, "/api/orders/" + testOrder.Id + "/review", new
        {
            agentId = fullAgentId
        }, csrf);
        Check(review.StatusCode == HttpStatusCode.OK, "authorized agent can propose a follow-up through the policy gate");
        var proposalId = (await review.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var decision = await Send(client, "/api/proposals/" + proposalId + "/decision", new
        {
            decision = "approve",
            reason = "reviewed",
            @operator = "forged-actor"
        }, csrf);
        Check(decision.StatusCode == HttpStatusCode.OK, "authenticated human can approve the proposal");
        var storedTask = store.FindTaskByProposal(proposalId!);
        Check(storedTask is not null && storedTask.Status == "OPEN", "approval creates a durable internal task");

        var audit = await client.GetFromJsonAsync<JsonElement[]>("/api/audit") ?? [];
        Check(audit.Any(item => item.GetProperty("event").GetString() == "session.login_denied"), "failed login is recorded without a password");
        Check(audit.Any(item => item.GetProperty("event").GetString() == "agent.access_denied") &&
              audit.Any(item => item.GetProperty("event").GetString() == "proposal.created" && item.GetProperty("actor").GetString()!.Contains(fullAgentId!)) &&
              audit.Any(item => item.GetProperty("event").GetString() == "approval.approve" && item.GetProperty("actor").GetString()!.Contains("test-admin")), "audit distinguishes denial, agent proposal and human approval");
        Check(audit.Any(item => item.GetProperty("event").GetString() == "approval.approve" && item.GetProperty("policyVersion").GetString() == "follow-up-v1" && !string.IsNullOrEmpty(item.GetProperty("eventId").GetString())), "approval audit includes event ID and policy version");
        Check(audit.Any(item => item.GetProperty("event").GetString() == "role.created" && item.GetProperty("actor").GetString()!.Contains("test-admin") && !item.GetProperty("actor").GetString()!.Contains("forged-actor")), "audit attributes the decision to the authenticated user");
        var anchorResponse = await client.GetAsync("/api/audit/anchor");
        Check(anchorResponse.StatusCode == HttpStatusCode.OK, "configured audit anchor endpoint returns a signed checkpoint");
        var anchorDocument = await anchorResponse.Content.ReadFromJsonAsync<JsonElement>();
        var anchorVerification = await Send(client, "/api/audit/anchor/verify", anchorDocument, csrf);
        var anchorVerificationBody = await anchorVerification.Content.ReadFromJsonAsync<JsonElement>();
        Check(anchorVerification.StatusCode == HttpStatusCode.OK && anchorVerificationBody.GetProperty("isValid").GetBoolean(), "audit anchor endpoint verifies the checkpoint after later audit writes");
        var auditExport = await client.GetAsync("/api/audit/export");
        var auditExportBody = await auditExport.Content.ReadAsStringAsync();
        Check(auditExport.StatusCode == HttpStatusCode.OK && auditExport.Content.Headers.ContentType?.MediaType == "application/x-ndjson" && auditExportBody.Contains("session.login_denied", StringComparison.Ordinal), "administrator can export the complete audit stream for external archival");
        var staleReview = await Send(client, "/api/orders/" + testOrder.Id + "/review", new
        {
            agentId = fullAgentId
        }, csrf);
        var staleId = (await staleReview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Check((await Send(client, "/api/policies/followup", new
        {
            mode = "automatic",
            reason = "testing automation"
        }, csrf)).StatusCode == HttpStatusCode.OK, "administrator can configure the follow-up policy");
        Check((await Send(client, "/api/proposals/" + staleId + "/decision", new
        {
            decision = "approve",
            reason = "stale"
        }, csrf)).StatusCode == HttpStatusCode.Conflict, "policy change invalidates older pending approvals");
        var automaticReview = await Send(client, "/api/orders/" + testOrder.Id + "/review", new
        {
            agentId = fullAgentId
        }, csrf);
        var automaticBody = await automaticReview.Content.ReadFromJsonAsync<JsonElement>();
        Check(automaticReview.StatusCode == HttpStatusCode.OK && automaticBody.GetProperty("status").GetString() == "TASK_CREATED", "automatic policy creates only an internal follow-up");
        await Send(client, "/api/policies/followup", new
        {
            mode = "human_only",
            reason = "testing restriction"
        }, csrf);
        Check((await Send(client, "/api/orders/" + testOrder.Id + "/review", new
        {
            agentId = fullAgentId
        }, csrf)).StatusCode == HttpStatusCode.Forbidden, "human-only policy blocks the agent");
        Check((await Send(client, "/api/orders/" + testOrder.Id + "/tasks", new
        {
            description = "Operator follow-up task"
        }, csrf)).StatusCode == HttpStatusCode.OK, "human-only policy permits an explicit human task");
        await Send(client, "/api/policies/followup", new
        {
            mode = "blocked",
            reason = "testing block"
        }, csrf);
        Check((await Send(client, "/api/orders/" + testOrder.Id + "/review", new
        {
            agentId = fullAgentId
        }, csrf)).StatusCode == HttpStatusCode.Forbidden, "blocked policy denies the action");

        Check((await Send(client, "/api/auth/logout", new
        {
        }, csrf)).StatusCode == HttpStatusCode.OK, "logout succeeds");
        Check((await client.GetAsync("/api/orders")).StatusCode == HttpStatusCode.Unauthorized, "logout revokes the browser session");
        using var replay = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        replay.DefaultRequestHeaders.Add("Cookie", sessionCookie);
        Check((await replay.GetAsync("/api/orders")).StatusCode == HttpStatusCode.Unauthorized, "a copied session cookie is rejected after logout");
        Check(File.Exists(Path.Combine(root, "data", "paypal360.db")), "integration test uses its isolated database");
        Console.WriteLine("All HTTP checks passed.");
    }

    private static async Task<HttpResponseMessage> Send<T>(HttpClient client, string path, T body, string csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-Token", csrf);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
}


internal sealed class FakeAiFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new FakeAiHandler());
}
internal sealed class FakeAiHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri?.ToString() ?? "";
        var body = url.EndsWith("/v1/oauth2/token", StringComparison.Ordinal)
            ? "{\"access_token\":\"test-token\"}"
            : url.Contains("verify-webhook-signature", StringComparison.Ordinal)
                ? "{\"verification_status\":\"SUCCESS\"}"
                : url.EndsWith("/v2/checkout/orders", StringComparison.Ordinal)
                    ? "{\"id\":\"HTTP-ORDER\",\"status\":\"CREATED\"}"
                    : url.EndsWith("/capture", StringComparison.Ordinal)
                        ? "{\"id\":\"HTTP-CAPTURE\",\"status\":\"COMPLETED\"}"
                        : url.Contains("/v2/payments/refunds/", StringComparison.Ordinal)
                            ? "{\"id\":\"HTTP-REFUND\",\"status\":\"COMPLETED\"}"
                        : url.Contains("/v2/payments/captures/", StringComparison.Ordinal)
                            ? "{\"id\":\"HTTP-REFUND\",\"status\":\"COMPLETED\"}"
                        : url.Contains("/v2/checkout/orders/", StringComparison.Ordinal)
                            ? "{\"id\":\"HTTP-ORDER\",\"status\":\"CREATED\"}"
                            : "{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"Review fulfillment.\"}]}]}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}




