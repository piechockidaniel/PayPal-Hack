using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;

using System.Security.Cryptography;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PayPal360;
using PayPal360.Contracts;
using PayPal360.Contracts.Exceptions;
using PayPal360.Contracts.Providers;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>(optional: true, reloadOnChange: false);
var configuredWebhookHost = AppConfiguration.PayPalWebhookHost(builder.Configuration) ?? "";
builder.Services.AddHttpClient();
builder.Services.AddSingleton(new CommerceDatabase(Path.Combine(builder.Environment.ContentRootPath, "data")));
builder.Services.AddSingleton<DemoStore>();
builder.Services.AddSingleton<IdentityDefinitions>();
builder.Services.AddSingleton<PolicyManager>();
builder.Services.AddSingleton<CommerceActionService>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<AuditTrustAnchor>();
builder.Services.AddSingleton<IProductCatalog, DemoCatalog>();
builder.Services.AddSingleton<PayPalSandboxProvider>();
builder.Services.AddSingleton<IPaymentProvider>(services => services.GetRequiredService<PayPalSandboxProvider>());
builder.Services.AddSingleton<IRefundProvider>(services => services.GetRequiredService<PayPalSandboxProvider>());
builder.Services.AddSingleton<GmcCatalogProvider>();
builder.Services.AddSingleton<IReadOnlyCatalogChannel>(services => services.GetRequiredService<GmcCatalogProvider>());
builder.Services.AddSingleton<CheckoutCoordinator>();
builder.Services.AddSingleton<AiReview>();
builder.Services.AddSingleton<PayPalWebhookQueue>();
builder.Services.AddHostedService<PayPalWebhookWorker>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<LocalAuth>();
var keyDirectory = Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data", "keys"));
var dataProtection = builder.Services.AddDataProtection().PersistKeysToFileSystem(keyDirectory);
var protectionCertificatePath = AppConfiguration.DataProtectionCertificatePath(builder.Configuration);
if (!string.IsNullOrWhiteSpace(protectionCertificatePath))
{
    if (!File.Exists(protectionCertificatePath))
        throw new InvalidOperationException("Configured data-protection certificate was not found.");
    var certificate = X509CertificateLoader.LoadPkcs12FromFile(
        protectionCertificatePath,
        AppConfiguration.DataProtectionCertificatePassword(builder.Configuration),
        X509KeyStorageFlags.EphemeralKeySet);
    dataProtection.ProtectKeysWithCertificate(certificate);
}
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "PayPal360.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(4);
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = context =>
    {
        var auth = context.HttpContext.RequestServices.GetRequiredService<LocalAuth>();
        if (!auth.IsSessionValid(context.Principal?.FindFirstValue("session_id"), context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)))
            context.RejectPrincipal();
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-Token");
builder.Services.AddRateLimiter(options => options.AddPolicy("login", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));

var app = builder.Build();
// Initialize the administrator from configured local User Secrets (or the
// deployment environment fallback) only on an empty database.
_ = app.Services.GetRequiredService<LocalAuth>();
// The application remains local-only except for the explicitly configured
// PayPal webhook host. The webhook still has to arrive from the local reverse
// proxy/tunnel, so the remote address must remain loopback.
app.Use(async (context, next) =>
{
    var hostName = context.Request.Host.Host;
    var remoteAddress = context.Connection.RemoteIpAddress;
    var isPayPalWebhook = context.Request.Path == "/api/webhooks/paypal";
    var isLocalHost = hostName.Equals("localhost", StringComparison.OrdinalIgnoreCase) || hostName == "127.0.0.1" || hostName == "::1" || hostName == "[::1]";
    var isConfiguredWebhookHost = isPayPalWebhook && !string.IsNullOrWhiteSpace(configuredWebhookHost) && hostName.Equals(configuredWebhookHost, StringComparison.OrdinalIgnoreCase);
    if (!(isLocalHost || isConfiguredWebhookHost) ||
        (remoteAddress is not null && !IPAddress.IsLoopback(remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

app.UseAuthentication();
// Agents use short-lived, replaceable bearer credentials issued by an
// administrator. They never receive a human role or a browser session.
app.Use(async (context, next) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        var token = header["Bearer ".Length..].Trim();
        var agent = context.RequestServices.GetRequiredService<IdentityDefinitions>().Authenticate(token);
        if (agent is not null)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, agent.Id),
                new Claim(ClaimTypes.Name, agent.Name),
                new Claim(ClaimTypes.Role, "agent"),
                new Claim("agent_id", agent.Id),
                new Claim("tenant_id", agent.TenantId ?? "local")
            };
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "AgentBearer"));
        }
    }
    await next();
});
app.UseAuthorization();
// Record authorization failures with the identity that was actually present.
// The path is safe diagnostic metadata; request bodies, cookies and credentials
// are deliberately excluded from the audit reason.
app.Use(async (context, next) =>
{
    await next();
    if (context.Request.Path.StartsWithSegments("/api") && context.Response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
    {
        try
        {
            var actor = context.User.FindFirstValue("agent_id") is { } agentId
                ? "agent:" + agentId
                : context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId
                    ? "user:" + userId
                    : "unauthenticated";
            context.RequestServices.GetRequiredService<AuditLog>().Write("-", "access.denied", actor,
                "Access denied for " + context.Request.Method + " " + context.Request.Path + ".", "gatekeeper", null, null, Tenant(context));
        }
        catch
        {
            // Authorization must not be turned into an information leak when
            // the prototype audit store is unavailable.
        }
    }
});
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api"))
    {
        var isPublic = path == "/api/status" || path == "/api/products" || path == "/api/auth/csrf" ||
            path == "/api/auth/login" || path == "/api/auth/session" || path.StartsWithSegments("/api/checkout") ||
            path == "/api/webhooks/paypal";
        if (!isPublic && context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var isAgentBearer = context.User.Identity?.AuthenticationType == "AgentBearer";
        var isMutation = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);
        if (isMutation && context.Request.Headers.Origin.Count > 0 && !AllowedOrigin(context.Request.Headers.Origin.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Cross-origin requests are not allowed."
            });
            return;
        }
        var isPayPalWebhook = path == "/api/webhooks/paypal";
        if (!isAgentBearer && isMutation && !isPayPalWebhook)
        {
            try
            {
                await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Invalid or missing request verification token."
                });
                return;
            }
        }
    }
    await next();
});
app.MapGet("/api/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Ok(new
    {
        token = antiforgery.GetAndStoreTokens(context).RequestToken
    }));
app.MapGet("/api/auth/session", (HttpContext context, LocalAuth auth) =>
    Results.Ok(new
    {
        authenticated = context.User.Identity?.IsAuthenticated == true,
        username = context.User.Identity?.Name,
        configured = auth.IsConfigured(),
        agent = context.User.FindFirstValue("agent_id"),
        tenant = Tenant(context)
    }));
app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, LocalAuth auth, AuditLog audit) =>
{
    var user = auth.Verify(request.Username, request.Password);
    if (user is null)
    {
        audit.Write("-", "session.login_denied", "unauthenticated", "Invalid login credentials.", "local-auth", null, null, "local");
        return Results.Unauthorized();
    }
    var session = auth.CreateSession(user);
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id),
        new Claim(ClaimTypes.Name, user.Name),
        new Claim(ClaimTypes.Role, user.IsAdministrator ? "administrator" : "operator"),
        new Claim("tenant_id", user.TenantId ?? "local"),
        new Claim("session_id", session.Id)
    };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));

    return Results.Ok(new
    {
        username = user.Name
    });
}).RequireRateLimiting("login");
app.MapPost("/api/auth/logout", async (HttpContext context, LocalAuth auth) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
    auth.RevokeSession(context.User.FindFirstValue("session_id"), userId);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (IConfiguration c, LocalAuth auth) => new
{
    paypal = !string.IsNullOrWhiteSpace(AppConfiguration.PayPalClientId(c)) && !string.IsNullOrWhiteSpace(AppConfiguration.PayPalClientSecret(c)),
    clientId = AppConfiguration.PayPalClientId(c) ?? "",
    ai = !string.IsNullOrWhiteSpace(AppConfiguration.OpenAiApiKey(c)) && !string.IsNullOrWhiteSpace(AppConfiguration.OpenAiModel(c)),
    operatorConfigured = auth.IsConfigured(),
    environment = "PayPal sandbox only"
});
app.MapGet("/api/connectors", (HttpContext context, IConfiguration c, CommerceDatabase database) =>
{
    var tenantId = Tenant(context);
    var verifications = database.Read(state => state.ConnectorVerifications
        .Where(item => string.Equals(item.TenantId ?? "local", tenantId, StringComparison.Ordinal))
        .ToDictionary(item => item.ConnectorId, StringComparer.Ordinal));
    var paypalConfigured = !string.IsNullOrWhiteSpace(AppConfiguration.PayPalClientId(c)) && !string.IsNullOrWhiteSpace(AppConfiguration.PayPalClientSecret(c));
    var gmcConfigured = !string.IsNullOrWhiteSpace(AppConfiguration.GmcMerchantAccountId(c)) && !string.IsNullOrWhiteSpace(AppConfiguration.GmcServiceAccountJson(c));
    string Status(string id, bool configured) => !configured ? "not-configured" : verifications.TryGetValue(id, out var check) ? check.Status : "configured-not-verified";
    string Detail(string id, string fallback) => verifications.TryGetValue(id, out var check) ? check.Detail + " Checked " + check.CheckedAt.ToLocalTime().ToString("g") + "." : fallback;
    return new[] {
        new { id = "local-catalog", role = "Catalog", provider = "Local demo catalog", status = "available", detail = "Read-only sample data", canVerify = false },
        new { id = "paypal", role = "Payments", provider = "PayPal Orders API", status = Status("paypal", paypalConfigured), detail = Detail("paypal", "Sandbox credentials are configured; use Verify to test OAuth without creating an order."), canVerify = true },
        new { id = "paypal-invoicing", role = "Invoicing", provider = "PayPal", status = "unverified", detail = "Adapter not implemented", canVerify = false },
        new { id = "gmc", role = "Product channel", provider = "Google Merchant Center", status = Status("gmc", gmcConfigured), detail = Detail("gmc", "Read-only processed-product connector; local catalog, price, inventory, orders and tenant ownership remain authoritative."), canVerify = true },
        new { id = "shipping", role = "Shipping", provider = "Unassigned", status = "unavailable", detail = "No connector configured", canVerify = false },
        new { id = "stripe", role = "Payments", provider = "Stripe", status = "unavailable", detail = "Optional adapter not implemented", canVerify = false },
        new { id = "woocommerce", role = "Storefront", provider = "WooCommerce", status = "unavailable", detail = "Optional adapter not implemented", canVerify = false },
        new { id = "allegro", role = "Marketplace", provider = "Allegro", status = "unavailable", detail = "Optional adapter not implemented", canVerify = false }
    };
});
app.MapPost("/api/connectors/{connector}/verify", async (string connector, HttpContext context, LocalAuth auth, IdentityDefinitions identities, PayPalSandboxProvider paypal, IReadOnlyCatalogChannel gmc, CommerceDatabase database, CancellationToken ct) =>
{
    var tenantId = Tenant(context);
    var isPayPal = connector.Equals("paypal", StringComparison.OrdinalIgnoreCase);
    var isGmc = connector.Equals("gmc", StringComparison.OrdinalIgnoreCase);
    if (!isPayPal && !isGmc)
        return Results.NotFound(new
        {
            error = "This connector does not have an internal verifier."
        });
    if ((isPayPal && !HasPermission(context, auth, identities, "read_orders")) ||
        (isGmc && !HasPermission(context, auth, identities, "read_catalog")))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    var connectorId = isPayPal ? "paypal" : "gmc";
    try
    {
        var detail = isPayPal
            ? await paypal.VerifyConnectionAsync(ct)
            : await VerifyGmcAsync(gmc, tenantId, ct);
        var verification = SaveConnectorVerification(database, connectorId, tenantId, "verified", detail, context);
        return Results.Ok(verification);
    }
    catch (Exception e) when (e is ProviderException or JsonException or IOException or CryptographicException)
    {
        var verification = SaveConnectorVerification(database, connectorId, tenantId, "verification-failed", "Verification failed: " + e.Message, context);
        return Results.Ok(verification);
    }
});
app.MapGet("/api/products", (IProductCatalog catalog) => catalog.List());
app.MapGet("/api/connectors/gmc/products", async (HttpContext context, LocalAuth auth, IdentityDefinitions identities, IReadOnlyCatalogChannel channel, string? pageToken, int? pageSize, CancellationToken ct) =>
{
    if (!HasPermission(context, auth, identities, "read_catalog"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        return Results.Ok(await channel.ListProductsAsync(Tenant(context), pageToken, pageSize ?? 25, ct));
    }
    catch (ProviderException e)
    {
        return Results.Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapPost("/api/webhooks/paypal", async (HttpRequest request, IConfiguration configuration, PayPalSandboxProvider provider, PayPalWebhookQueue queue, CancellationToken ct) =>
{
    var webhookId = AppConfiguration.PayPalWebhookId(configuration);
    if (string.IsNullOrWhiteSpace(webhookId))
        return Results.Problem("PayPal webhook verification is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    var headers = new PayPalWebhookHeaders(
        request.Headers["PAYPAL-TRANSMISSION-ID"].ToString(),
        request.Headers["PAYPAL-TRANSMISSION-TIME"].ToString(),
        request.Headers["PAYPAL-CERT-URL"].ToString(),
        request.Headers["PAYPAL-AUTH-ALGO"].ToString(),
        request.Headers["PAYPAL-TRANSMISSION-SIG"].ToString());
    if (new[] { headers.TransmissionId, headers.TransmissionTime, headers.CertUrl, headers.AuthAlgorithm, headers.TransmissionSignature }.Any(string.IsNullOrWhiteSpace))
        return Results.BadRequest(new
        {
            error = "Required PayPal webhook headers are missing."
        });
    if (request.ContentLength is > 1_048_576)
        return Results.BadRequest(new
        {
            error = "Webhook payload is too large."
        });
    using var reader = new StreamReader(request.Body);
    var rawBody = await reader.ReadToEndAsync(ct);
    if (!await provider.VerifyWebhookAsync(headers, rawBody, webhookId, ct))
        return Results.BadRequest(new
        {
            error = "PayPal webhook signature verification failed."
        });
    try
    {
        using var document = JsonDocument.Parse(rawBody);
        if (!TryParsePayPalProviderEvent(document.RootElement, out var providerEvent))
            return Results.Ok(new
            {
                accepted = true,
                ignored = true
            });
        var acceptedEvent = queue.Accept(providerEvent!, rawBody);
        if (!acceptedEvent.IsDuplicate || acceptedEvent.Item.Status is "PENDING" or "PROCESSING")
            await queue.EnqueueAsync(acceptedEvent.Item, ct);
        return Results.Accepted(value: new
        {
            accepted = true,
            eventId = providerEvent!.EventId,
            duplicate = acceptedEvent.IsDuplicate
        });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new
        {
            error = "The verified webhook payload is not valid JSON."
        });
    }
});
app.MapGet("/api/orders", (HttpContext context, LocalAuth auth, IdentityDefinitions identities, DemoStore store) =>
    HasPermission(context, auth, identities, "read_orders") ? Results.Ok(store.ListOrders(Tenant(context), ResourceScope(context, identities))) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapGet("/api/proposals", (HttpContext context, LocalAuth auth, IdentityDefinitions identities, DemoStore store) =>
    HasPermission(context, auth, identities, "read_orders") ? Results.Ok(store.ListProposals(Tenant(context), ResourceScope(context, identities))) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapGet("/api/tasks", (HttpContext context, LocalAuth auth, IdentityDefinitions identities, DemoStore store) =>
    HasPermission(context, auth, identities, "read_orders") ? Results.Ok(store.ListTasks(Tenant(context), ResourceScope(context, identities))) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapGet("/api/audit", (HttpContext context, LocalAuth auth, IdentityDefinitions identities, AuditLog audit) =>
    HasPermission(context, auth, identities, "read_audit") ? Results.Ok(audit.Read(Tenant(context))) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapGet("/api/audit/export", (HttpContext context, LocalAuth auth, IdentityDefinitions identities, AuditLog audit) =>
{
    if (!context.User.IsInRole("administrator") || !HasPermission(context, auth, identities, "read_audit"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var body = string.Join(Environment.NewLine, audit.ExportAll().Select(entry => JsonSerializer.Serialize(entry))) + Environment.NewLine;
    return Results.File(Encoding.UTF8.GetBytes(body), "application/x-ndjson", "paypal360-audit.ndjson");
});

app.MapGet("/api/policies/followup", (HttpContext context, LocalAuth auth, IdentityDefinitions identities, PolicyManager policies) =>
    HasPermission(context, auth, identities, "manage_policies") ? Results.Ok(policies.GetFollowUp(Tenant(context))) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapGet("/api/policies", (HttpContext context, LocalAuth auth, IdentityDefinitions identities, PolicyManager policies) =>
    HasPermission(context, auth, identities, "manage_policies") ? Results.Ok(policies.List(Tenant(context))) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapPost("/api/policies/followup", (PolicyUpdateRequest body, HttpRequest request, LocalAuth auth, IdentityDefinitions identities, PolicyManager policies) =>
{
    if (!HasPermission(request.HttpContext, auth, identities, "manage_policies"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!ValidReason(body.Reason))
        return Results.BadRequest(new
        {
            error = "A decision reason is required."
        });
    try
    {
        return Results.Ok(policies.UpdateFollowUp(body.Mode ?? "", Actor(request.HttpContext), body.Reason!.Trim(), Tenant(request.HttpContext)));
    }
    catch (ArgumentException e)
    {
        return Results.BadRequest(new
        {
            error = e.Message
        });
    }
});
app.MapPost("/api/policies/{action}", (string action, PolicyUpdateRequest body, HttpRequest request, LocalAuth auth, IdentityDefinitions identities, PolicyManager policies) =>
{
    if (!HasPermission(request.HttpContext, auth, identities, "manage_policies"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!ValidReason(body.Reason))
        return Results.BadRequest(new
        {
            error = "A decision reason is required."
        });
    try
    {
        return Results.Ok(policies.Update(action, body.Mode ?? "", Actor(request.HttpContext), body.Reason!.Trim(), Tenant(request.HttpContext)));
    }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
});
app.MapGet("/api/action-requests", (HttpContext context, LocalAuth auth, IdentityDefinitions identities, CommerceActionService actions) =>
    HasPermission(context, auth, identities, "read_audit") ? Results.Ok(actions.List(Tenant(context))) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapPost("/api/actions/{action}", async (string action, ActionRequestInput body, HttpRequest request, LocalAuth auth, IdentityDefinitions identities, CommerceActionService actions, CancellationToken ct) =>
{
    var tenantId = Tenant(request.HttpContext);
    if (!CanRequestAction(request.HttpContext, auth, identities, action, body.ResourceId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!ValidReason(body.Reason) || string.IsNullOrWhiteSpace(body.ResourceId))
        return Results.BadRequest(new
        {
            error = "A resource ID and decision reason are required."
        });
    try
    {
        var result = await actions.RequestAsync(action, body.ResourceId!.Trim(), Actor(request.HttpContext), body.Reason!.Trim(), tenantId,
            request.HttpContext.User.FindFirstValue("agent_id") is null, ct, body.Amount);
        return ActionResponse(result);
    }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
});
app.MapPost("/api/actions/{id}/decision", async (string id, ActionDecisionRequest body, HttpRequest request, LocalAuth auth, IdentityDefinitions identities, CommerceActionService actions, CancellationToken ct) =>
{
    if (request.HttpContext.User.FindFirstValue("agent_id") is not null || !HasPermission(request.HttpContext, auth, identities, "approve_proposals"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (body.Decision is not ("approve" or "reject") || !ValidReason(body.Reason))
        return Results.BadRequest(new
        {
            error = "A valid decision and decision reason are required."
        });
    try
    {
        var result = await actions.DecideAsync(id, body.Decision, Actor(request.HttpContext), body.Reason!.Trim(), Tenant(request.HttpContext), ct);
        return ActionResponse(result);
    }
    catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
});
app.MapPost("/api/actions/{id}/reconcile", async (string id, ActionReconcileRequest body, HttpRequest request, LocalAuth auth, IdentityDefinitions identities, CommerceActionService actions, CancellationToken ct) =>
{
    if (request.HttpContext.User.FindFirstValue("agent_id") is not null || !HasPermission(request.HttpContext, auth, identities, "execute_actions"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!ValidReason(body.Reason))
        return Results.BadRequest(new
        {
            error = "A reconciliation reason is required."
        });
    try
    {
        var result = await actions.ReconcileAsync(id, Actor(request.HttpContext), body.Reason!.Trim(), Tenant(request.HttpContext), ct);
        return ActionResponse(result);
    }
    catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
});

app.MapGet("/api/roles", (HttpContext context, LocalAuth auth, IdentityDefinitions definitions) =>
    HasPermission(context, auth, definitions, "manage_definitions") ? Results.Ok(definitions.ListRoles(Tenant(context))) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapGet("/api/agents", (HttpContext context, LocalAuth auth, IdentityDefinitions definitions) =>
    HasPermission(context, auth, definitions, "manage_definitions") ? Results.Ok(definitions.ListAgents(Tenant(context))) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapPost("/api/roles", (CreateRoleRequest body, HttpRequest request, LocalAuth auth, IdentityDefinitions definitions) =>
{
    if (!HasPermission(request.HttpContext, auth, definitions, "manage_definitions"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!ValidReason(body.Reason))
        return Results.BadRequest(new
        {
            error = "A decision reason is required."
        });
    try
    {
        var role = definitions.CreateRole(body.Name ?? "", body.Kind ?? "", body.Permissions ?? [], Actor(request.HttpContext), body.Reason!.Trim(), Tenant(request.HttpContext));
        return Results.Ok(role);
    }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
});
app.MapPost("/api/agents", (CreateAgentRequest body, HttpRequest request, LocalAuth auth, IdentityDefinitions definitions) =>
{
    if (!HasPermission(request.HttpContext, auth, definitions, "manage_definitions"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!ValidReason(body.Reason))
        return Results.BadRequest(new
        {
            error = "A decision reason is required."
        });
    try
    {
        var agent = definitions.CreateAgent(body.Name ?? "", body.RoleId ?? "", body.ParentId, Actor(request.HttpContext), body.Reason!.Trim(), Tenant(request.HttpContext), body.ResourceScope ?? []);
        return Results.Ok(agent);
    }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
});

app.MapPost("/api/agents/{id}/credential", (string id, CreateCredentialRequest body, HttpRequest request, LocalAuth auth, IdentityDefinitions definitions) =>
{
    if (!HasPermission(request.HttpContext, auth, definitions, "manage_definitions"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!ValidReason(body.Reason))
        return Results.BadRequest(new
        {
            error = "A decision reason is required."
        });
    try
    {
        return Results.Ok(definitions.IssueCredential(id, Actor(request.HttpContext), body.Reason!.Trim(), Tenant(request.HttpContext)));
    }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
});

app.MapGet("/api/users", (HttpContext context, LocalAuth auth, IdentityDefinitions identities) =>
    HasPermission(context, auth, identities, "manage_users") ? Results.Ok(auth.ListUsers().Where(user => user.TenantId == Tenant(context))) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapPost("/api/users", (CreateUserRequest body, HttpRequest request, LocalAuth auth, IdentityDefinitions identities) =>
{
    if (!HasPermission(request.HttpContext, auth, identities, "manage_users"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!ValidReason(body.Reason) || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Password))
        return Results.BadRequest(new
        {
            error = "Name, password, and a decision reason are required."
        });
    try
    {
        return Results.Ok(auth.CreateUser(body.Name, body.Password, Tenant(request.HttpContext), body.RoleIds ?? [], Actor(request.HttpContext), body.Reason!.Trim()));
    }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
});

app.MapPost("/api/checkout", async (CheckoutRequest body, HttpRequest request, IProductCatalog catalog, CheckoutCoordinator coordinator, AuditLog audit, CancellationToken ct) =>
{
    var product = catalog.Find(body.ProductId);
    if (product is null)
        return Results.BadRequest(new
        {
            error = "Unknown product."
        });
    if (!Guid.TryParseExact(body.RequestId, "D", out var requestId))
        return Results.BadRequest(new
        {
            error = "A valid checkout request ID is required."
        });
    var checkoutAccessToken = request.Cookies["PayPal360.Checkout"] ?? CheckoutCoordinator.CreateAccessToken();
    try
    {
        var result = await coordinator.CreateAsync(product, requestId, ct, "local", checkoutAccessToken);
        request.HttpContext.Response.Cookies.Append("PayPal360.Checkout", checkoutAccessToken, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = request.IsHttps,
            MaxAge = TimeSpan.FromHours(5),
            Path = "/api/checkout"
        });
        return Results.Ok(new
        {
            orderId = result.Order.Id,
            paypalOrderId = result.Order.ProviderOrderId
        });
    }
    catch (InvalidOperationException e)
    {
        return Results.Conflict(new
        {
            error = e.Message
        });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    catch (ProviderException e)
    {
        audit.Write("-", "checkout.failed", "buyer", "Sandbox order creation failed.", "PayPal sandbox", null);
        return Results.Problem(e.Message, statusCode: 502);
    }
});

app.MapPost("/api/checkout/reconcile/{requestId:guid}", async (Guid requestId, HttpRequest request, CheckoutCoordinator coordinator, CancellationToken ct) =>
{
    var checkoutAccessToken = request.Cookies["PayPal360.Checkout"];
    if (!coordinator.CanAccessRequest(requestId, checkoutAccessToken))
        return Results.NotFound();
    var result = await coordinator.ReconcileAsync(requestId, ct, "local", checkoutAccessToken);
    if (result.Status == "NOT_FOUND")
        return Results.NotFound();
    if (result.Status == "FORBIDDEN")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (result.Order is null)
        return Results.Conflict(new
        {
            status = result.Status,
            error = "The provider outcome is still unknown; retry reconciliation later."
        });
    return Results.Ok(new
    {
        orderId = result.Order.Id,
        paypalOrderId = result.Order.ProviderOrderId,
        status = result.Order.Status
    });
});
app.MapPost("/api/checkout/{id}/capture", async (string id, HttpRequest request, CheckoutCoordinator coordinator, IPaymentProvider provider, DemoStore store, AuditLog audit, CancellationToken ct) =>
{
    if (!coordinator.CanAccessOrder(id, request.Cookies["PayPal360.Checkout"]))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var order = store.FindOrder(id);
    if (order is null)
        return Results.NotFound();
    if (order.Status == "PAID")
        return Results.Ok(order);
    order = store.BeginCapture(id);
    try
    {
        var result = await provider.CaptureOrder(order.ProviderOrderId, ct);
        if (result.Status != "COMPLETED")
        {
            try
            {
                var authoritative = await provider.GetOrder(order.ProviderOrderId, ct);
                if (authoritative.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
                    return Results.Ok(store.MarkPaid(id, "local", authoritative.CaptureId ?? authoritative.Id));
                result = authoritative;
            }
            catch (NotSupportedException) { }
            audit.Write(id, "capture.pending", "system", "PayPal returned status " + result.Status, "PayPal sandbox", result.Id);
            return Results.Conflict(new
            {
                error = "Payment is not completed: " + result.Status
            });
        }
        var updated = store.MarkPaid(id, "local", result.CaptureId ?? result.Id);
        return Results.Ok(updated);
    }
    catch (ProviderException e)
    {
        try
        {
            var authoritative = await provider.GetOrder(order.ProviderOrderId, ct);
            if (authoritative.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(store.MarkPaid(id, "local", authoritative.CaptureId ?? authoritative.Id));
        }
        catch (NotSupportedException) { }
        catch (ProviderException) { }
        audit.Write(id, "capture.failed", "system", "PayPal capture failed.", "PayPal sandbox", order.ProviderOrderId);
        return Results.Problem(e.Message, statusCode: 502);
    }
});

app.MapGet("/api/audit/verify", (HttpContext context, LocalAuth auth, IdentityDefinitions identities, AuditLog audit) =>
    HasPermission(context, auth, identities, "read_audit") ? Results.Ok(audit.Verify()) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapGet("/api/audit/anchor", (HttpContext context, LocalAuth auth, IdentityDefinitions identities, AuditTrustAnchor anchor) =>
{
    if (!HasPermission(context, auth, identities, "read_audit"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        return Results.Ok(anchor.Create());
    }
    catch (InvalidOperationException e)
    {
        return Results.Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapPost("/api/audit/anchor/verify", (AuditAnchorDocument? document, HttpContext context, LocalAuth auth, IdentityDefinitions identities, AuditTrustAnchor anchor) =>
    HasPermission(context, auth, identities, "read_audit") ? Results.Ok(anchor.Verify(document)) : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapPost("/api/orders/{id}/review", async (string id, ReviewRequest input, HttpRequest request, DemoStore store, IdentityDefinitions identities, PolicyManager policies, AiReview ai, AuditLog audit, CancellationToken ct) =>
{
    var auth = request.HttpContext.RequestServices.GetRequiredService<LocalAuth>();
    var tenantId = Tenant(request.HttpContext);
    var isAgent = request.HttpContext.User.FindFirstValue("agent_id") is not null;
    if (!isAgent && !HasPermission(request.HttpContext, auth, identities, "review_proposals"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (isAgent && input.AgentId != request.HttpContext.User.FindFirstValue("agent_id"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var order = store.FindOrder(id, tenantId);
    if (order is null)
        return Results.NotFound();
    if (order.Status != "PAID")
        return Results.Conflict(new
        {
            error = "Only completed payments can be reviewed."
        });
    var agent = identities.ListAgents(tenantId).FirstOrDefault(item => item.Id == input.AgentId);
    if (agent is null || !agent.EffectivePermissions.Contains("read_orders", StringComparer.Ordinal) || !agent.EffectivePermissions.Contains("propose_followup", StringComparer.Ordinal) || !identities.AgentCanAccessResource(agent.Id, id, tenantId))
    {
        audit.Write(id, "agent.access_denied", Actor(request.HttpContext), "Agent lacks read_orders or propose_followup permission.", "gatekeeper", input.AgentId, null, tenantId);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    var policy = policies.GetFollowUp(tenantId);
    if (policy.Mode is not ("automatic" or "approval_required"))
    {
        audit.Write(id, "policy.denied", "agent:" + agent.Id, "Agent follow-up denied by policy mode " + policy.Mode + ".", "gatekeeper", PolicyManager.FollowUpAction, "follow-up-v" + policy.Version, tenantId);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    audit.Write(id, "agent.access_allowed", Actor(request.HttpContext), "Agent has the required inherited permissions; the follow-up policy will be evaluated.", "gatekeeper", agent.Id, null, tenantId);
    try
    {
        var reason = await ai.Suggest(order, ct);
        var proposal = store.AddProposal(order.Id, reason, Actor(request.HttpContext), "agent:" + agent.Id + " (" + agent.Name + ")");
        if (proposal is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        return Results.Ok(proposal);
    }
    catch (ProviderException e)
    {
        audit.Write(id, "review.failed", "ai-agent", "AI review was unavailable.", "openai", null, null, tenantId);
        return Results.Problem(e.Message, statusCode: 502);
    }
});
app.MapPost("/api/orders/{id}/tasks", (string id, ManualTaskRequest input, HttpRequest request, DemoStore store, IdentityDefinitions identities, PolicyManager policies) =>
{
    var auth = request.HttpContext.RequestServices.GetRequiredService<LocalAuth>();
    if (request.HttpContext.User.FindFirstValue("agent_id") is not null || !HasPermission(request.HttpContext, auth, identities, "approve_proposals"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var description = (input.Description ?? "").Trim();
    if (description.Length is < 1 or > 1000)
        return Results.BadRequest(new
        {
            error = "A task description is required."
        });
    if (policies.GetFollowUp(Tenant(request.HttpContext)).Mode != "human_only")
        return Results.Conflict(new
        {
            error = "Manual task creation is available only under the human-only policy."
        });
    try
    {
        return Results.Ok(store.CreateManualTask(id, description, Actor(request.HttpContext), Tenant(request.HttpContext)));
    }
    catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
});
app.MapPost("/api/proposals/{id}/decision", (string id, DecisionRequest input, HttpRequest req, IConfiguration c, DemoStore store, AuditLog audit) =>
{
    var auth = req.HttpContext.RequestServices.GetRequiredService<LocalAuth>();
    var identities = req.HttpContext.RequestServices.GetRequiredService<IdentityDefinitions>();
    if (req.HttpContext.User.FindFirstValue("agent_id") is not null || !HasPermission(req.HttpContext, auth, identities, "approve_proposals"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var actor = Actor(req.HttpContext);
    var rationale = (input.Reason ?? "").Trim();
    if (rationale.Length is < 1 or > 500 || input.Decision is not ("approve" or "reject"))
        return Results.BadRequest(new
        {
            error = "Operator, decision, and reason are required."
        });
    var outcome = store.Decide(id, input.Decision, actor, rationale, Tenant(req.HttpContext));
    if (outcome is null)
        return Results.Conflict(new
        {
            error = "Proposal is missing, already decided, or the order is no longer eligible."
        });
    return Results.Ok(outcome);
});
app.Run();

static string Actor(HttpContext context) =>
    (context.User.FindFirstValue("agent_id") is { } agentId ? "agent:" + agentId : "user:" + (context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated identity ID is missing."))) +
    " (" + (context.User.Identity?.Name ?? "unknown") + ")";
static string Tenant(HttpContext context) => context.User.FindFirstValue("tenant_id") ?? "local";
static bool HasPermission(HttpContext context, LocalAuth auth, IdentityDefinitions identities, string permission)
{
    var tenant = Tenant(context);
    var agentId = context.User.FindFirstValue("agent_id");
    return agentId is not null
        ? identities.AgentHasPermission(agentId, permission, tenant)
        : auth.HasPermission(context.User.FindFirstValue(ClaimTypes.NameIdentifier), permission, tenant);
}
static bool CanRequestAction(HttpContext context, LocalAuth auth, IdentityDefinitions identities, string action, string? resourceId)
{
    var agentId = context.User.FindFirstValue("agent_id");
    var tenantId = Tenant(context);
    if (agentId is not null)
        return identities.AgentHasPermission(agentId, action, tenantId) &&
            !string.IsNullOrWhiteSpace(resourceId) && identities.AgentCanAccessResource(agentId, resourceId, tenantId);
    return HasPermission(context, auth, identities, "execute_actions");
}
static IResult ActionResponse(ActionOperationResult result) => result.Status switch
{
    "DENIED" => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
    "PENDING_APPROVAL" => Results.Accepted(value: result),
    "COMPLETED" or "REJECTED" => Results.Ok(result),
    "UNAVAILABLE" => Results.Json(result, statusCode: StatusCodes.Status501NotImplemented),
    "STALE" or "FAILED" or "PENDING_PROVIDER" or "UNKNOWN" => Results.Conflict(result),
    _ => Results.Json(result, statusCode: StatusCodes.Status500InternalServerError)
};
static IReadOnlyList<string>? ResourceScope(HttpContext context, IdentityDefinitions identities)
{
    var agentId = context.User.FindFirstValue("agent_id");
    return agentId is null ? null : identities.FindAgent(agentId)?.ResourceScope;
}
static bool ValidReason(string? reason) => (reason ?? "").Trim().Length is >= 1 and <= 500;
static async Task<string> VerifyGmcAsync(IReadOnlyCatalogChannel channel, string tenantId, CancellationToken ct)
{
    var page = await channel.ListProductsAsync(tenantId, null, 1, ct);
    return "Google Merchant API read-only product check returned " + page.Products.Count + " processed product(s).";
}
static ConnectorVerification SaveConnectorVerification(CommerceDatabase database, string connectorId, string tenantId, string status, string detail, HttpContext context) => database.Update(state =>
{
    var verification = new ConnectorVerification(connectorId, status, detail, DateTimeOffset.UtcNow, tenantId);
    state.ConnectorVerifications.RemoveAll(item => item.ConnectorId == connectorId && string.Equals(item.TenantId ?? "local", tenantId, StringComparison.Ordinal));
    state.ConnectorVerifications.Add(verification);
    CommerceDatabase.Record(state, "-", status == "verified" ? "connector.verified" : "connector.verification_failed", Actor(context), detail, connectorId, connectorId, null, tenantId);
    return verification;
});
static bool AllowedOrigin(string origin)
{
    return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1" || uri.Host == "::1");
}
static bool TryParsePayPalProviderEvent(JsonElement root, out ProviderEvent? providerEvent)
{
    providerEvent = null;
    var eventType = root.TryGetProperty("event_type", out var eventTypeElement) ? eventTypeElement.GetString() : null;
    var eventId = root.TryGetProperty("id", out var eventIdElement) ? eventIdElement.GetString() : null;
    if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(eventId))
        return false;
    var isCompleted = string.Equals(eventType, "PAYMENT.CAPTURE.COMPLETED", StringComparison.OrdinalIgnoreCase);
    var isDenied = string.Equals(eventType, "PAYMENT.CAPTURE.DENIED", StringComparison.OrdinalIgnoreCase);
    if (!isCompleted && !isDenied)
        return false;
    if (!root.TryGetProperty("resource", out var resource) ||
        !resource.TryGetProperty("supplementary_data", out var supplementary) ||
        !supplementary.TryGetProperty("related_ids", out var relatedIds) ||
        !relatedIds.TryGetProperty("order_id", out var orderIdElement))
        return false;
    var providerOrderId = orderIdElement.GetString();
    if (string.IsNullOrWhiteSpace(providerOrderId))
        return false;
    string? captureId = isCompleted && resource.TryGetProperty("id", out var captureIdElement)
        ? captureIdElement.GetString()
        : null;
    providerEvent = new ProviderEvent(eventId, providerOrderId, isCompleted ? "COMPLETED" : "DENIED", DateTimeOffset.UtcNow, "local", captureId);
    return true;
}
record CreateRoleRequest(string? Name, string? Kind, string[]? Permissions, string? Operator, string? Reason);
record CreateAgentRequest(string? Name, string? RoleId, string? ParentId, string? Operator, string? Reason, string[]? ResourceScope);

record PolicyUpdateRequest(string? Mode, string? Reason);
record ActionRequestInput(string? ResourceId, string? Reason, decimal? Amount = null);
record ActionDecisionRequest(string? Decision, string? Reason);
record ActionReconcileRequest(string? Reason);
record ReviewRequest(string? AgentId);
record LoginRequest(string? Username, string? Password);
record CheckoutRequest(string ProductId, string? RequestId);
record DecisionRequest(string? Operator, string Decision, string? Reason);
record CreateCredentialRequest(string? Reason);
record CreateUserRequest(string? Name, string? Password, string[]? RoleIds, string? Reason);
record ManualTaskRequest(string? Description);












public partial class Program
{
}
