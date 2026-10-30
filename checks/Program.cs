using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PayPal360;
using PayPal360.Contracts;
using PayPal360.Contracts.Providers;

static void Check(bool ok, string message)
{
    if (!ok)
        throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

if (args.Any(arg => arg.Equals("--live-openai", StringComparison.OrdinalIgnoreCase)))
{
    var liveConfiguration = new ConfigurationBuilder()
        .AddUserSecrets<AiReview>(optional: true)
        .Build();
    var liveAi = new AiReview(new LiveHttpClientFactory(), liveConfiguration);
    var liveReview = await liveAi.Suggest(
        new Order("live-ai-smoke", "synthetic", "Synthetic PayPal 360 smoke-test order", 1m, "PLN", "synthetic-provider-order", "PAID"),
        CancellationToken.None);
    Check(liveReview.Provider == "openai" && !string.IsNullOrWhiteSpace(liveReview.Model), "live OpenAI returned a structured review");
    Console.WriteLine($"Live OpenAI smoke test passed: model={liveReview.Model}, category={liveReview.Category}, confidence={liveReview.Confidence:F2}");
    return;
}

var catalog = new DemoCatalog();
Check(
    catalog.List().Count == 2
        && catalog.Find("p1")?.Price == 49m
        && catalog.Find("missing") is null,
    "catalog resolves only known products"
);

var draftProviderAction = new ProviderActionRequest(
    CommerceProviderActions.RefundPayment,
    "local",
    "order-1",
    "refund-order-1-1",
    "test-operator");
var draftProviderResult = new ProviderActionResult(
    "PENDING_PROVIDER",
    "provider-ref-1",
    draftProviderAction.IdempotencyKey,
    true,
    "Reconcile before retrying.");
Check(
    draftProviderAction.Action == "refund_payment"
        && !string.IsNullOrWhiteSpace(draftProviderAction.IdempotencyKey)
        && draftProviderResult.ProviderReference == "provider-ref-1"
        && draftProviderResult.RequiresReconciliation,
    "draft external action contracts carry idempotency, provider reference and reconciliation state"
);

var testDirectory = Path.Combine(Path.GetTempPath(), "paypal360-check-" + Guid.NewGuid().ToString("N"));
var database = new CommerceDatabase(testDirectory);
var store = new DemoStore(database);
var order = store.AddOrder(catalog.Find("p1")!, "SANDBOX-ORDER");
var proposal = new PayPal360.Contracts.Proposal(Guid.NewGuid().ToString("N"), order.Id, "Check fulfillment", "PENDING");
database.Update(state => { state.Proposals.Add(proposal); return true; });

Check(store.Decide(proposal.Id, "approve", "test-operator", "test reason") is null, "unpaid orders cannot be approved");

store.MarkPaid(order.Id);

Check(
    store.Decide(proposal.Id, "approve", "test-operator", "test reason")?.Status == "TASK_CREATED",
    "paid order can receive an approved internal task"
);

Check(store.Decide(proposal.Id, "approve", "test-operator", "test reason") is null, "a proposal cannot be approved twice");

var second = store.AddProposal(order.Id, "Check address", "user:test", "agent:test")!;

Check(store.Decide(second.Id, "reject", "test-operator", "test reason")?.Status == "REJECTED", "rejected proposal creates no task");

var tasks = store.ListTasks();

Check(
    tasks.Count == 1
        && tasks[0].ProposalId == proposal.Id
        && tasks[0].OrderId == order.Id
        && tasks[0].Description == "Check fulfillment"
        && tasks[0].Status == "OPEN",
    "approval creates a linked, visible follow-up task"
);

Check(
    store.FindTaskByProposal(proposal.Id)?.Id == tasks[0].Id,
    "audit can reference the actual task"
);
var providerEvent = new ProviderEvent("capture-event-1", order.ProviderOrderId, "COMPLETED", DateTimeOffset.UtcNow);
store.ApplyProviderEvent(providerEvent);
store.ApplyProviderEvent(providerEvent);
Check(new AuditLog(database).Read().Count(entry => entry.Event == "provider.event_applied") == 1, "provider event processing is idempotent");

Check(store.FindTaskByProposal(second.Id) is null, "rejection does not create a follow-up task");

var otherTenantOrder = store.AddOrder(catalog.Find("p1")!, "TENANT-B-ORDER", "tenant-b");
store.MarkPaid(otherTenantOrder.Id, "tenant-b");
var otherTenantProposal = store.AddProposal(otherTenantOrder.Id, "Tenant B follow-up", "user:tenant-b", "agent:tenant-b")!;
Check(store.Decide(otherTenantProposal.Id, "approve", "user:test", "cross-tenant attempt") is null &&
      otherTenantProposal.Status == "PENDING", "a local operator cannot decide another tenant's proposal");
Check(store.Decide(otherTenantProposal.Id, "approve", "user:tenant-b", "tenant-scoped approval", "tenant-b")?.Status == "TASK_CREATED", "the owning tenant can decide its proposal");
try
{
    store.CreateManualTask(otherTenantOrder.Id, "Cross-tenant task", "user:test");
    throw new Exception("cross-tenant manual task was created");
}
catch (InvalidOperationException e) when (e.Message.Contains("Only completed payments"))
{
    Console.WriteLine("PASS a local operator cannot create another tenant's manual task");
}
Check(store.CreateManualTask(otherTenantOrder.Id, "Tenant B task", "user:tenant-b", "tenant-b").Status == "OPEN", "the owning tenant can create its manual task");

var placeholderSettings = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["PayPal:PAYPAL_CLIENT_ID"] = "ChangeMe",
        ["PAYPAL_CLIENT_ID"] = "legacy-client",
        ["AgentAI:OPENAI_API_KEY"] = "your_openai_api_key",
        ["OPENAI_API_KEY"] = "legacy-key",
        ["Web:PAYPAL360_PUBLIC_HOST"] = "ChangeMe",
        ["PAYPAL360_PUBLIC_HOST"] = "demo.example"
    })
    .Build();
Check(AppConfiguration.PayPalClientId(placeholderSettings) == "legacy-client" && AppConfiguration.OpenAiApiKey(placeholderSettings) == "legacy-key" && AppConfiguration.PublicHost(placeholderSettings) == "demo.example", "configuration placeholders are treated as unset");

var prioritySettings = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["PayPal:PAYPAL_CLIENT_ID"] = "ChangeMe",
        ["PAYPAL_CLIENT_ID"] = "legacy-client"
    })
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["PayPal:PAYPAL_CLIENT_ID"] = "user-secret-client"
    })
    .Build();
Check(AppConfiguration.PayPalClientId(prioritySettings) == "user-secret-client", "scoped User Secret values override placeholders and legacy fallbacks");

var settings = new ConfigurationBuilder()
    .AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["PayPal:PAYPAL_CLIENT_ID"] = "test-client",
            ["PayPal:PAYPAL_CLIENT_SECRET"] = "test-secret",
            ["AgentAI:OPENAI_API_KEY"] = "test-key",
            ["AgentAI:OPENAI_MODEL"] = "test-model",
        }
    )
    .Build();

var handler = new FakeHandler();
var clients = new FakeFactory(handler);
var paypal = new PayPalSandboxProvider(clients, settings);
Check((await paypal.VerifyConnectionAsync(CancellationToken.None)).Contains("no order", StringComparison.OrdinalIgnoreCase), "PayPal verification checks OAuth without creating an order");
var created = await paypal.CreateOrder(catalog.Find("p1")!, "checkout-key", CancellationToken.None);

Check(created.Id == "SANDBOX-ORDER", "sandbox adapter parses created order");
Check(
    handler.LastUrl == "https://api-m.sandbox.paypal.com/v2/checkout/orders"
        && handler.LastBody.Contains("49.00")
        && handler.LastBody.Contains("PLN")
        && handler.LastRequestId == "checkout-key",
    "order request uses sandbox, catalog price and supplied idempotency key"
);

await paypal.CaptureOrder(created.Id, CancellationToken.None);

Check(
    handler.LastUrl.EndsWith("/SANDBOX-ORDER/capture")
        && handler.LastRequestId == "capture-SANDBOX-ORDER",
    "capture uses a repeatable idempotency key"
);
var webhookHeaders = new PayPalWebhookHeaders(
    "transmission-1",
    DateTimeOffset.UtcNow.ToString("O"),
    "https://api-m.sandbox.paypal.com/v1/notifications/certs/test",
    "SHA256withRSA",
    "signature");
Check(
    await paypal.VerifyWebhookAsync(webhookHeaders, "{\"id\":\"event-1\"}", "webhook-1", CancellationToken.None),
    "sandbox webhook signature verification accepts the provider verification response"
);

var ai = new AiReview(clients, settings);
var review = await ai.Suggest(store.FindOrder(order.Id)!, CancellationToken.None);
Check(
    review.Recommendation == "Review fulfillment."
        && review.Category == "manual_follow_up"
        && review.Confidence == 0.92m
        && review.Evidence.Single() == "Payment is captured."
        && review.RiskFlags.Count == 0
        && review.Provider == "openai"
        && handler.LastUrl == "https://api.openai.com/v1/responses",
    "AI adapter extracts and validates the structured response"
);

var gmcKeyPath = Path.Combine(testDirectory, "gmc-service-account.json");
using (var gmcKey = RSA.Create(2048))
{
    File.WriteAllText(gmcKeyPath, JsonSerializer.Serialize(new
    {
        type = "service_account",
        project_id = "test-project",
        client_email = "gmc-test@test-project.iam.gserviceaccount.com",
        private_key = gmcKey.ExportPkcs8PrivateKeyPem()
    }));
}
var gmcSettings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["GoogleMerchant:GMC_MERCHANT_ACCOUNT_ID"] = "5308690318",
    ["GoogleMerchant:GMC_SERVICE_ACCOUNT_JSON"] = gmcKeyPath,
    ["GoogleMerchant:GMC_TENANT_ID"] = "local"
}).Build();
var gmcHandler = new GmcFakeHandler();
var gmc = new GmcCatalogProvider(new FakeFactory(gmcHandler), gmcSettings);
var gmcPage = await gmc.ListProductsAsync("local", null, 1, CancellationToken.None);
Check(
    gmc.IsConfigured
        && gmcPage.Products.Single().OfferId == "sku-1"
        && gmcPage.Products.Single().Title == "Read-only GMC product"
        && gmcHandler.LastMethod == HttpMethod.Get
        && gmcHandler.LastUrl.Contains("accounts/5308690318/products"),
    "GMC connector authenticates with an external service account and only lists processed products"
);
var retryDatabase = new CommerceDatabase(Path.Combine(testDirectory, "retry"));
var paymentStub = new RetryPaymentProvider();
var coordinator = new CheckoutCoordinator(paymentStub, retryDatabase);
var checkoutKey = Guid.NewGuid();
try
{
    await coordinator.CreateAsync(catalog.Find("p1")!, checkoutKey, CancellationToken.None);
    throw new Exception("expected uncertain response");
}
catch (InvalidOperationException e) when (e.Message == "Simulated uncertain response") { }
var retried = await coordinator.CreateAsync(
    catalog.Find("p1")!,
    checkoutKey,
    CancellationToken.None
);
var repeated = await coordinator.CreateAsync(
    catalog.Find("p1")!,
    checkoutKey,
    CancellationToken.None
);
Check(
    retried.Created
        && !repeated.Created
        && repeated.Order.Id == retried.Order.Id
        && paymentStub.Keys.Count == 2
        && paymentStub.Keys.All(key => key == checkoutKey.ToString("D")),
    "uncertain checkout retries reuse provider key and return one local order"
);
var accessDirectory = Path.Combine(testDirectory, "checkout-access");
var accessCoordinator = new CheckoutCoordinator(new IdempotentPaymentProvider(), new CommerceDatabase(accessDirectory));
var accessRequestId = Guid.NewGuid();
var accessToken = CheckoutCoordinator.CreateAccessToken();
var accessResult = await accessCoordinator.CreateAsync(catalog.Find("p1")!, accessRequestId, CancellationToken.None, "local", accessToken);
Check(
    accessCoordinator.CanAccessRequest(accessRequestId, accessToken)
        && accessCoordinator.CanAccessOrder(accessResult.Order.Id, accessToken)
        && !accessCoordinator.CanAccessOrder(accessResult.Order.Id, CheckoutCoordinator.CreateAccessToken()),
    "checkout and capture access requires the matching server-side checkout token"
);
var webhookDirectory = Path.Combine(testDirectory, "webhook-inbox");
var webhookDatabase = new CommerceDatabase(webhookDirectory);
var webhookQueue = new PayPalWebhookQueue(webhookDatabase);
var durableEvent = new ProviderEvent("durable-event", "unknown-provider-order", "COMPLETED", DateTimeOffset.UtcNow);
var durablePayload = "{\"id\":\"durable-event\",\"raw\":true}";
var acceptedWebhook = webhookQueue.Accept(durableEvent, durablePayload);
var duplicateWebhook = webhookQueue.Accept(durableEvent, "different-payload");
Check(!acceptedWebhook.IsDuplicate && duplicateWebhook.IsDuplicate && duplicateWebhook.Item.RawPayload == durablePayload, "verified webhook payload is persisted and duplicate delivery is idempotent");
var webhookAttempt = webhookQueue.BeginAttempt(durableEvent.EventId)!;
webhookQueue.MarkDeadLetter(durableEvent.EventId, "No matching local order.");
Check(
    webhookDatabase.Read(state => state.WebhookInbox.Single(item => item.EventId == durableEvent.EventId).Status == "DEAD_LETTER" &&
        state.WebhookInbox.Single(item => item.EventId == durableEvent.EventId).Attempts == webhookAttempt.Attempts &&
        state.WebhookInbox.Single(item => item.EventId == durableEvent.EventId).RawPayload == durablePayload),
    "failed webhook processing preserves raw evidence in the durable dead-letter state"
);
try
{
    await coordinator.CreateAsync(catalog.Find("p2")!, checkoutKey, CancellationToken.None);
    throw new Exception("checkout key reused with different product");
}
catch (InvalidOperationException e) when (e.Message.Contains("different product"))
{
    Console.WriteLine("PASS checkout request key cannot switch products");
}

var definitions = new IdentityDefinitions(database);
var human = definitions.CreateRole("Reviewer", "human", ["read_orders", "review_proposals"], "test-operator", "test reason");
var parentRole = definitions.CreateRole("Parent", "agent", ["read_orders", "read_catalog"], "test-operator", "test reason");
var childRole = definitions.CreateRole("Child", "agent", ["read_orders", "propose_followup"], "test-operator", "test reason");
var parent = definitions.CreateAgent("Manager agent", parentRole.Id, null, "test-operator", "test reason");
var child = definitions.CreateAgent("Order sub-agent", childRole.Id, parent.Id, "test-operator", "test reason");
Check(
    child.EffectivePermissions.SequenceEqual(["read_orders"]),
    "sub-agent permissions are the intersection of parent and assigned role"
);
var grandchild = definitions.CreateAgent("Nested agent", childRole.Id, child.Id, "test-operator", "test reason");
Check(
    grandchild.EffectivePermissions.SequenceEqual(["read_orders"]),
    "delegation cannot recover a permission excluded by an ancestor"
);
var scopedParent = definitions.CreateAgent("Scoped parent", parentRole.Id, null, "test-operator", "test reason", "local", ["order-a"]);
var scopedChild = definitions.CreateAgent("Scoped child", childRole.Id, scopedParent.Id, "test-operator", "test reason", "local", ["order-a", "order-b"]);
Check((scopedChild.ResourceScope ?? []).SequenceEqual(["order-a"]), "resource scope cannot exceed parent delegation");
try
{
    definitions.CreateAgent("Invalid", human.Id, null, "test-operator", "test reason");
    throw new Exception("human role incorrectly accepted");
}
catch (ArgumentException)
{
    Console.WriteLine("PASS human roles cannot be assigned to agents");
}
try
{
    definitions.CreateRole("Too powerful", "agent", ["manage_definitions"], "test-operator", "test reason");
    throw new Exception("invalid permission incorrectly accepted");
}
catch (ArgumentException)
{
    Console.WriteLine("PASS agent roles cannot receive human administrative capabilities");
}
try
{
    definitions.CreateAgent("Missing parent", childRole.Id, "missing", "test-operator", "test reason");
    throw new Exception("invalid parent incorrectly accepted");
}
catch (ArgumentException)
{
    Console.WriteLine("PASS unknown parent rejected");
}

var reopenedStore = new DemoStore(new CommerceDatabase(testDirectory));
var reopenedAudit = new AuditLog(new CommerceDatabase(testDirectory));
Check(reopenedStore.ListTasks().Count == 1 && reopenedStore.ListProposals().Count == 2, "approval and task survive restart");
Check(reopenedAudit.Read().Any(entry => entry.Event == "approval.approve" && entry.Reference == proposal.Id), "approval audit survives restart");
Check(reopenedAudit.Verify().IsValid && reopenedAudit.Verify().CheckedEvents > 0, "audit hash chain verifies after restart");
var integrityDirectory = Path.Combine(testDirectory, "integrity");
var integrityDatabase = new CommerceDatabase(integrityDirectory);
new DemoStore(integrityDatabase).AddOrder(catalog.Find("p1")!, "INTEGRITY-ORDER");
integrityDatabase.Update(state => { state.Audit[0] = state.Audit[0] with { Reason = "tampered" }; return true; });
Check(!new AuditLog(integrityDatabase).Verify().IsValid, "audit verifier detects local tampering");
var anchorDirectory = Path.Combine(testDirectory, "audit-anchor");
var anchorDatabase = new CommerceDatabase(anchorDirectory);
new DemoStore(anchorDatabase).AddOrder(catalog.Find("p1")!, "ANCHOR-ORDER");
var anchorConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Audit:PAYPAL360_AUDIT_ANCHOR_SECRET"] = "0123456789abcdef0123456789abcdef"
    })
    .Build();
var trustAnchor = new AuditTrustAnchor(anchorDatabase, anchorConfiguration);
var anchorDocument = trustAnchor.Create();
Check(trustAnchor.Verify(anchorDocument) is { IsConfigured: true, IsValid: true, CheckedEvents: 1 }, "externally held audit secret creates a verifiable checkpoint");
anchorDatabase.Update(state => { CommerceDatabase.Record(state, "-", "post-anchor", "test", "append after checkpoint", "test", null); return true; });
Check(trustAnchor.Verify(anchorDocument).IsValid, "audit checkpoint remains valid as later events are appended");
anchorDatabase.Update(state => { state.Audit[0] = state.Audit[0] with { Reason = "tampered before checkpoint" }; return true; });
Check(!trustAnchor.Verify(anchorDocument).IsValid, "audit checkpoint detects tampering in its anchored prefix");
try
{
    trustAnchor.Create();
    throw new Exception("invalid audit chain was checkpointed");
}
catch (InvalidOperationException e) when (e.Message.Contains("audit chain is invalid"))
{
    Console.WriteLine("PASS invalid audit chain cannot create a new checkpoint");
}
var auditBeforeFailure = reopenedAudit.Read().Count;
try
{
    database.Update<bool>(state =>
    {
        state.Tasks.Clear();
        CommerceDatabase.Record(state, "-", "injected.failure", "test", "injected", "test", null);
        throw new InvalidOperationException("Injected failure");
    });
}
catch (InvalidOperationException e) when (e.Message == "Injected failure") { }
Check(reopenedStore.ListTasks().Count == 1 && reopenedAudit.Read().Count == auditBeforeFailure, "transaction rollback preserves both task and audit state");
var restartedCoordinator = new CheckoutCoordinator(paymentStub, new CommerceDatabase(Path.Combine(testDirectory, "retry")));
var afterRestart = await restartedCoordinator.CreateAsync(catalog.Find("p1")!, checkoutKey, CancellationToken.None);
Check(!afterRestart.Created && afterRestart.Order.Id == retried.Order.Id && paymentStub.Keys.Count == 2, "completed checkout request survives restart without another provider call");
var reconcileDirectory = Path.Combine(testDirectory, "reconcile");
var reconcileProvider = new ReconcilePaymentProvider();
var reconcileCoordinator = new CheckoutCoordinator(reconcileProvider, new CommerceDatabase(reconcileDirectory));
var reconcileKey = Guid.NewGuid();
try
{
    await reconcileCoordinator.CreateAsync(catalog.Find("p1")!, reconcileKey, CancellationToken.None);
}
catch (InvalidOperationException e) when (e.Message.Contains("response lost")) { }
var reconciled = await reconcileCoordinator.ReconcileAsync(reconcileKey, CancellationToken.None);
Check(reconciled.Status == "RESOLVED" && reconciled.Order is not null && reconcileProvider.GetCalls == 1, "uncertain checkout can be reconciled against provider status");
var concurrentDirectory = Path.Combine(testDirectory, "concurrent");
var concurrentProvider = new IdempotentPaymentProvider();
var coordinatorA = new CheckoutCoordinator(concurrentProvider, new CommerceDatabase(concurrentDirectory));
var coordinatorB = new CheckoutCoordinator(concurrentProvider, new CommerceDatabase(concurrentDirectory));
var concurrentKey = Guid.NewGuid();
var concurrentResults = await Task.WhenAll(
    coordinatorA.CreateAsync(catalog.Find("p1")!, concurrentKey, CancellationToken.None),
    coordinatorB.CreateAsync(catalog.Find("p1")!, concurrentKey, CancellationToken.None));
Check(new CommerceDatabase(concurrentDirectory).Read(state => state.Orders.Count == 1) && concurrentResults.Select(result => result.Order.Id).Distinct().Count() == 1, "concurrent checkout requests produce one local order");
var reloadedIdentities = new IdentityDefinitions(new CommerceDatabase(testDirectory));
Check(reloadedIdentities.ListRoles().Any(role => role.Id == parentRole.Id) && reloadedIdentities.ListAgents().Any(agent => agent.Id == child.Id), "roles and agent hierarchy survive restart");

var authDatabase = new CommerceDatabase(Path.Combine(testDirectory, "auth-usernames"));
var localAuth = new LocalAuth(authDatabase, new ConfigurationBuilder().Build());
localAuth.CreateUser("shared-name", "tenant-a-password-123", "tenant-a", [], "user:admin", "test user");
try
{
    localAuth.CreateUser("shared-name", "tenant-b-password-123", "tenant-b", [], "user:admin", "duplicate test");
    throw new Exception("duplicate local username was accepted across tenants");
}
catch (ArgumentException e) when (e.Message.Contains("already exists"))
{
    Console.WriteLine("PASS local usernames are globally unique for deterministic login");
}

var oldKey = Guid.NewGuid();
retryDatabase.Update(state => { state.CheckoutAttempts.Add(new CheckoutAttempt(oldKey, catalog.Find("p1")!, DateTimeOffset.UtcNow.AddHours(-7), null)); return true; });
try
{
    await restartedCoordinator.CreateAsync(catalog.Find("p1")!, oldKey, CancellationToken.None);
    throw new Exception("expired checkout was retried");
}
catch (InvalidOperationException e) when (e.Message.Contains("retry window has expired")) { Console.WriteLine("PASS expired uncertain checkout requires reconciliation"); }

var legacyDirectory = Path.Combine(testDirectory, "legacy");
Directory.CreateDirectory(legacyDirectory);
var legacyFile = Path.Combine(legacyDirectory, "audit.ndjson");
File.WriteAllText(legacyFile, System.Text.Json.JsonSerializer.Serialize(new PayPal360.Contracts.AuditEvent(DateTimeOffset.UtcNow, "legacy-order", "legacy.event", "legacy-user", "old record", "legacy", null)) + Environment.NewLine);
var legacyDb = new CommerceDatabase(legacyDirectory);
var legacyAudit = new AuditLog(legacyDb);
Check(legacyAudit.Read().Count == 1 && File.Exists(legacyFile), "legacy audit imports without deleting the source file");
var legacyReopened = new AuditLog(new CommerceDatabase(legacyDirectory));
Check(legacyReopened.Read().Count == 1, "legacy audit import is not duplicated after restart");

var policies = new PolicyManager(database);
var pendingBeforePolicyChange = store.AddProposal(order.Id, "Pending before change", "user:test", "agent:test")!;
var automaticPolicy = policies.UpdateFollowUp("automatic", "user:test", "Enable automation for demo");
Check(automaticPolicy.Version == 2, "policy update increments version");
Check(store.Decide(pendingBeforePolicyChange.Id, "approve", "user:test", "stale") is null, "approval under a changed policy is rejected");
var automaticProposal = store.AddProposal(order.Id, "Automatic follow-up", "user:test", "agent:test");
Check(automaticProposal?.Status == "TASK_CREATED" && store.FindTaskByProposal(automaticProposal.Id) is not null, "automatic policy creates an internal task");
policies.UpdateFollowUp("human_only", "user:test", "Require human operation");
Check(store.AddProposal(order.Id, "Not allowed", "user:test", "agent:test") is null, "human-only policy denies agent follow-up");
policies.UpdateFollowUp("blocked", "user:test", "Block the action");
Check(store.AddProposal(order.Id, "Not allowed", "user:test", "agent:test") is null, "blocked policy denies agent follow-up");
Check(new PolicyManager(new CommerceDatabase(testDirectory)).GetFollowUp().Mode == "blocked", "policy choice survives restart");
Check(policies.Get("refund_payment").Mode == "approval_required", "refunds default to human approval");
Check(policies.Update("refund_payment", "approval_required", "user:test", "Require review for future refunds").Mode == "approval_required", "non-follow-up policy can be versioned safely");
var actionDirectory = Path.Combine(testDirectory, "actions");
var actionDatabase = new CommerceDatabase(actionDirectory);
var actionStore = new DemoStore(actionDatabase);
var actionPolicies = new PolicyManager(actionDatabase);
var refundProvider = new FakeRefundProvider();
var actionService = new CommerceActionService(actionDatabase, actionPolicies, actionStore, paypal, refundProvider);
var blockedAction = await actionService.RequestAsync("fulfill_order", "missing-order", "agent:blocked", "Fulfillment request", "local", false, CancellationToken.None);
Check(blockedAction.Status == "DENIED" && blockedAction.Request.Status == "DENIED", "non-follow-up actions fail closed while blocked");
var captureOrder = actionStore.AddOrder(catalog.Find("p1")!, "ACTION-ORDER");
actionPolicies.Update("capture_payment", "automatic", "user:test", "Enable reviewed capture handler");
var capturedAction = await actionService.RequestAsync("capture_payment", captureOrder.Id, "agent:payments", "Capture the authorized payment", "local", false, CancellationToken.None);
Check(capturedAction.Status == "COMPLETED" && actionStore.FindOrder(captureOrder.Id)!.Status == "PAID", "automatic capture policy executes through the provider action handler");
actionPolicies.Update("capture_payment", "approval_required", "user:test", "Require human approval for capture");
var approvalOrder = actionStore.AddOrder(catalog.Find("p1")!, "ACTION-APPROVAL-ORDER");
var pendingAction = await actionService.RequestAsync("capture_payment", approvalOrder.Id, "agent:payments", "Capture after review", "local", false, CancellationToken.None);
var approvedAction = await actionService.DecideAsync(pendingAction.Request.Id, "approve", "user:operator", "Reviewed the capture", "local", CancellationToken.None);
Check(pendingAction.Status == "PENDING_APPROVAL" && approvedAction.Status == "COMPLETED", "approval-required action waits for a human and then executes");
var refundAction = await actionService.RequestAsync("refund_payment", captureOrder.Id, "user:operator", "Refund after settlement", "local", true, CancellationToken.None, 10m);
var completedRefund = await actionService.DecideAsync(refundAction.Request.Id, "approve", "user:operator", "Reviewed the refund", "local", CancellationToken.None);
Check(refundAction.Status == "PENDING_APPROVAL"
      && completedRefund.Status == "COMPLETED"
      && completedRefund.Request.Amount == 10m
      && completedRefund.Request.ProviderReference == "REFUND-1"
      && completedRefund.Request.IdempotencyKey == "refund-" + completedRefund.Request.Id
      && refundProvider.LastRequest?.ProviderReference == "CAPTURE-1",
    "approved partial refunds use the persisted capture reference and stable idempotency key");
var fullRefundOrder = actionStore.AddOrder(catalog.Find("p1")!, "ACTION-FULL-REFUND-ORDER");
actionStore.MarkPaid(fullRefundOrder.Id, "local", "CAPTURE-FULL");
var fullRefund = await actionService.RequestAsync("refund_payment", fullRefundOrder.Id, "user:operator", "Refund the complete order", "local", true, CancellationToken.None);
var completedFullRefund = await actionService.DecideAsync(fullRefund.Request.Id, "approve", "user:operator", "Reviewed the full refund", "local", CancellationToken.None);
var repeatedFullRefund = await actionService.RequestAsync("refund_payment", fullRefundOrder.Id, "user:operator", "Repeat refund test", "local", true, CancellationToken.None);
var rejectedRepeatedFullRefund = await actionService.DecideAsync(repeatedFullRefund.Request.Id, "approve", "user:operator", "Checked that the balance is exhausted", "local", CancellationToken.None);
Check(completedFullRefund.Status == "COMPLETED" && completedFullRefund.Request.Amount is null && rejectedRepeatedFullRefund.Status == "FAILED",
    "a completed full refund exhausts the refundable balance");
var overRefund = await actionService.RequestAsync("refund_payment", captureOrder.Id, "user:operator", "Refund remaining amount", "local", true, CancellationToken.None, 40m);
var rejectedOverRefund = await actionService.DecideAsync(overRefund.Request.Id, "approve", "user:operator", "Checked remaining refundable balance", "local", CancellationToken.None);
Check(rejectedOverRefund.Status == "FAILED", "local refund invariant prevents over-refunding");
refundProvider.ReturnPending = true;
var uncertainRefund = await actionService.RequestAsync("refund_payment", captureOrder.Id, "user:operator", "Exercise provider reconciliation", "local", true, CancellationToken.None, 5m);
var pendingRefund = await actionService.DecideAsync(uncertainRefund.Request.Id, "approve", "user:operator", "Reviewed provider uncertainty", "local", CancellationToken.None);
var reconciledRefund = await actionService.ReconcileAsync(pendingRefund.Request.Id, "user:operator", "Provider status lookup confirmed the refund", "local", CancellationToken.None);
Check(pendingRefund.Status == "PENDING_PROVIDER" && reconciledRefund.Status == "COMPLETED" && reconciledRefund.Request.ReconciliationRequired == false && reconciledRefund.Request.ReconciliationReason == "Provider status lookup confirmed the refund",
    "uncertain refunds reconcile to an authoritative completed provider result");
policies.UpdateFollowUp("human_only", "user:test", "Require an explicit operator task");
var manualTask = store.CreateManualTask(order.Id, "Operator follow-up", "user:test");
Check(manualTask.Status == "OPEN" && store.ListTasks().Any(task => task.Id == manualTask.Id), "human-only policy supports an explicit manual task");
try
{
    policies.UpdateFollowUp("unknown", "user:test", "invalid");
    throw new Exception("invalid policy mode accepted");
}
catch (ArgumentException) { Console.WriteLine("PASS unknown policy mode is rejected"); }

var failureDirectory = Path.Combine(testDirectory, "failure");
var failureDb = new CommerceDatabase(failureDirectory);
var failureStore = new DemoStore(failureDb);
var failureOrder = failureStore.AddOrder(catalog.Find("p1")!, "FAILURE-ORDER");
failureStore.MarkPaid(failureOrder.Id);
var failureProposal = failureStore.AddProposal(failureOrder.Id, "Needs approval", "user:test", "agent:test")!;
var beforeWriteFailure = new AuditLog(failureDb).Read().Count;
using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(failureDirectory, "paypal360.db")))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "CREATE TRIGGER fail_state_write BEFORE UPDATE ON commerce_state BEGIN SELECT RAISE(ABORT, 'injected write failure'); END;";
    command.ExecuteNonQuery();
}
try
{
    failureStore.Decide(failureProposal.Id, "approve", "user:test", "approved");
    throw new Exception("write failure was not propagated");
}
catch (Microsoft.Data.Sqlite.SqliteException) { }
var afterWriteFailure = new DemoStore(new CommerceDatabase(failureDirectory));
Check(afterWriteFailure.ListProposals().Single().Status == "PENDING" && afterWriteFailure.ListTasks().Count == 0 &&
      new AuditLog(new CommerceDatabase(failureDirectory)).Read().Count == beforeWriteFailure,
      "failed database write leaves decision, task, and audit unchanged");

database.Update(state =>
{
    state.Policies.Clear();
    state.Policies.Add(new PolicyRule(PolicyManager.FollowUpAction, "unknown", 99));
    return true;
});
Check(store.AddProposal(order.Id, "Unknown policy", "user:test", "agent:test") is null, "unknown stored policy fails closed");
Console.WriteLine("All checks passed.");

sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, false);
}

sealed class LiveHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(45) };

    public HttpClient CreateClient(string name) => client;
}

sealed class GmcFakeHandler : HttpMessageHandler
{
    public string LastUrl = "";
    public HttpMethod LastMethod = HttpMethod.Options;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastUrl = request.RequestUri!.ToString();
        LastMethod = request.Method;
        var json = LastUrl.StartsWith("https://oauth2.googleapis.com/token", StringComparison.Ordinal)
            ? "{\"access_token\":\"gmc-test-token\",\"expires_in\":3600}"
            : "{\"products\":[{\"name\":\"accounts/5308690318/products/en~PL~sku-1\",\"offerId\":\"sku-1\",\"contentLanguage\":\"en\",\"feedLabel\":\"PL\",\"dataSource\":\"accounts/5308690318/dataSources/1\",\"productAttributes\":{\"title\":\"Read-only GMC product\"},\"archived\":false}],\"nextPageToken\":\"next\"}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}

sealed class FakeHandler : HttpMessageHandler
{
    public string LastUrl = "",
        LastBody = "",
        LastRequestId = "";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct
    )
    {
        LastUrl = request.RequestUri!.ToString();
        LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        LastRequestId = request.Headers.TryGetValues("PayPal-Request-Id", out var ids)
            ? ids.First()
            : "";
        var json =
            LastUrl.EndsWith("/v1/oauth2/token") ? "{\"access_token\":\"token\"}"
            : LastUrl.Contains("verify-webhook-signature")
                ? "{\"verification_status\":\"SUCCESS\"}"
            : LastUrl.Contains("api.openai.com")
                ? StructuredAiResponse()
            : LastUrl.Contains("/capture")
                ? "{\"id\":\"CAPTURE-1\",\"status\":\"COMPLETED\"}"
            : "{\"id\":\"SANDBOX-ORDER\",\"status\":\"COMPLETED\"}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static string StructuredAiResponse()
    {
        var payload = JsonSerializer.Serialize(new
        {
            recommendation = "Review fulfillment.",
            category = "manual_follow_up",
            confidence = 0.92m,
            evidence = new[] { "Payment is captured." },
            risk_flags = Array.Empty<string>()
        });
        return JsonSerializer.Serialize(new
        {
            output = new[]
            {
                new { content = new[] { new { type = "output_text", text = payload } } }
            }
        });
    }
}

sealed class RetryPaymentProvider : PayPal360.Contracts.Providers.IPaymentProvider
{
    public List<string> Keys { get; } = [];

    public Task<PayPal360.Contracts.ProviderResult> CreateOrder(
        PayPal360.Contracts.Product product,
        string requestId,
        CancellationToken ct
    )
    {
        Keys.Add(requestId);
        if (Keys.Count == 1)
            throw new InvalidOperationException("Simulated uncertain response");
        return Task.FromResult(new PayPal360.Contracts.ProviderResult("RETRIED-PAYPAL", "CREATED"));
    }

    public Task<PayPal360.Contracts.ProviderResult> CaptureOrder(
        string providerId,
        CancellationToken ct
    ) => throw new NotImplementedException();
}

sealed class ReconcilePaymentProvider : PayPal360.Contracts.Providers.IPaymentProvider
{
    public int CreateCalls
    {
        get; private set;
    }
    public int GetCalls
    {
        get; private set;
    }

    public Task<PayPal360.Contracts.ProviderResult> CreateOrder(PayPal360.Contracts.Product product, string requestId, CancellationToken ct)
    {
        CreateCalls++;
        if (CreateCalls == 1)
            throw new InvalidOperationException("response lost after provider success");
        return Task.FromResult(new PayPal360.Contracts.ProviderResult("RECONCILED-PAYPAL", "CREATED"));
    }

    public Task<PayPal360.Contracts.ProviderResult> CaptureOrder(string providerId, CancellationToken ct) => throw new NotImplementedException();

    public Task<PayPal360.Contracts.ProviderResult> GetOrder(string providerId, CancellationToken ct)
    {
        GetCalls++;
        return Task.FromResult(new PayPal360.Contracts.ProviderResult(providerId, "CREATED"));
    }
}

sealed class IdempotentPaymentProvider : PayPal360.Contracts.Providers.IPaymentProvider
{
    public List<string> Keys { get; } = [];

    public async Task<PayPal360.Contracts.ProviderResult> CreateOrder(PayPal360.Contracts.Product product, string requestId, CancellationToken ct)
    {
        lock (Keys)
            Keys.Add(requestId);
        await Task.Delay(10, ct);
        return new PayPal360.Contracts.ProviderResult("CONCURRENT-PAYPAL", "CREATED");
    }

    public Task<PayPal360.Contracts.ProviderResult> CaptureOrder(string providerId, CancellationToken ct) => throw new NotImplementedException();
}

sealed class FakeRefundProvider : IRefundProvider
{
    public bool ReturnPending
    {
        get; set;
    }
    public ProviderActionRequest? LastRequest
    {
        get; private set;
    }

    public Task<ProviderActionResult> RefundAsync(ProviderActionRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new ProviderActionResult(
            ReturnPending ? "PENDING" : "COMPLETED",
            "REFUND-1",
            request.IdempotencyKey,
            ReturnPending,
            ReturnPending ? "Provider outcome requires reconciliation." : "Refund completed."));
    }

    public Task<ProviderReconciliationResult> ReconcileRefundAsync(ProviderReconciliationRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new ProviderReconciliationResult("COMPLETED", request.ProviderReference, true, "Provider lookup confirmed the refund."));
}
