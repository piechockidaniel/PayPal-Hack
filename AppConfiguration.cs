using Microsoft.Extensions.Configuration;

namespace PayPal360;

/// <summary>
/// Reads local settings from .NET User Secrets sections and supports the
/// legacy flat environment names as a deployment fallback. A non-empty
/// scoped value always wins over the fallback so a stale process environment
/// cannot silently replace a local User Secret.
/// </summary>
public static class AppConfiguration
{
    private static readonly string[] PlaceholderValues =
    [
        "ChangeMe",
        "your_openai_api_key",
        "your_openai_model"
    ];

    private static bool IsConfigured(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !PlaceholderValues.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string? Get(IConfiguration configuration, string section, string key)
    {
        var scoped = configuration[$"{section}:{key}"];
        if (IsConfigured(scoped))
            return scoped;

        var legacy = configuration[key];
        return IsConfigured(legacy) ? legacy : null;
    }

    public static string? GetTrimmed(IConfiguration configuration, string section, string key) =>
        Get(configuration, section, key)?.Trim();

    public static string? PayPalClientId(IConfiguration configuration) => Get(configuration, "PayPal", "PAYPAL_CLIENT_ID");
    public static string? PayPalClientSecret(IConfiguration configuration) => Get(configuration, "PayPal", "PAYPAL_CLIENT_SECRET");
    public static string? PayPalWebhookId(IConfiguration configuration) => GetTrimmed(configuration, "PayPal", "PAYPAL_WEBHOOK_ID");
    public static string? PayPalWebhookHost(IConfiguration configuration) => GetTrimmed(configuration, "PayPal", "PAYPAL360_WEBHOOK_HOST");
    public static string? PublicHost(IConfiguration configuration) => GetTrimmed(configuration, "Web", "PAYPAL360_PUBLIC_HOST");
    public static string? DataDirectory(IConfiguration configuration) => GetTrimmed(configuration, "Data", "PAYPAL360_DATA_DIRECTORY");

    public static string? OpenAiApiKey(IConfiguration configuration) => Get(configuration, "AgentAI", "OPENAI_API_KEY");
    public static string? OpenAiModel(IConfiguration configuration) => GetTrimmed(configuration, "AgentAI", "OPENAI_MODEL");

    public static string? AdminUsername(IConfiguration configuration) => GetTrimmed(configuration, "LocalAuth", "PAYPAL360_ADMIN_USERNAME");
    public static string? AdminPassword(IConfiguration configuration) => Get(configuration, "LocalAuth", "PAYPAL360_ADMIN_PASSWORD");

    public static string? GmcMerchantAccountId(IConfiguration configuration) => GetTrimmed(configuration, "GoogleMerchant", "GMC_MERCHANT_ACCOUNT_ID");
    public static string? GmcServiceAccountJson(IConfiguration configuration) =>
        GetTrimmed(configuration, "GoogleMerchant", "GMC_SERVICE_ACCOUNT_JSON") ??
        GetTrimmed(configuration, "GoogleMerchant", "GOOGLE_APPLICATION_CREDENTIALS");
    public static string GmcTenantId(IConfiguration configuration) =>
        GetTrimmed(configuration, "GoogleMerchant", "GMC_TENANT_ID") is { Length: > 0 } tenant ? tenant : "local";

    public static string? AuditAnchorSecret(IConfiguration configuration) => GetTrimmed(configuration, "Audit", "PAYPAL360_AUDIT_ANCHOR_SECRET");
    public static string? DataProtectionCertificatePath(IConfiguration configuration) => GetTrimmed(configuration, "DataProtection", "PAYPAL360_DATA_PROTECTION_CERTIFICATE_PATH");
    public static string? DataProtectionCertificatePassword(IConfiguration configuration) => Get(configuration, "DataProtection", "PAYPAL360_DATA_PROTECTION_CERTIFICATE_PASSWORD");
}
