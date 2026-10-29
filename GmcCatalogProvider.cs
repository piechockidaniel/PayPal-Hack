using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayPal360.Contracts;
using PayPal360.Contracts.Exceptions;

namespace PayPal360;

public sealed class GmcCatalogProvider(IHttpClientFactory clients, IConfiguration configuration) : IReadOnlyCatalogChannel
{
    private const string Scope = "https://www.googleapis.com/auth/content";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string ProductsEndpoint = "https://merchantapi.googleapis.com/products/v1";
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private CachedToken? cachedToken;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccountId) && !string.IsNullOrWhiteSpace(CredentialsPath);

    private string? AccountId => configuration["GMC_MERCHANT_ACCOUNT_ID"]?.Trim();
    private string? CredentialsPath => configuration["GMC_SERVICE_ACCOUNT_JSON"]?.Trim()
        ?? configuration["GOOGLE_APPLICATION_CREDENTIALS"]?.Trim();
    private string TenantId => configuration["GMC_TENANT_ID"]?.Trim() is { Length: > 0 } tenant ? tenant : "local";

    public async Task<GmcCatalogPage> ListProductsAsync(string tenantId, string? pageToken, int pageSize, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new ProviderException("GMC read-only access is not configured. Set GMC_MERCHANT_ACCOUNT_ID and GMC_SERVICE_ACCOUNT_JSON outside the repository.");
        if (!string.Equals(tenantId, TenantId, StringComparison.Ordinal))
            throw new ProviderException("The configured GMC account is not assigned to this tenant.");
        if (!ulong.TryParse(AccountId, out _) || AccountId!.Length > 20)
            throw new ProviderException("GMC_MERCHANT_ACCOUNT_ID must be a valid numeric Merchant Center account ID.");

        var query = new List<string> { "pageSize=" + Math.Clamp(pageSize, 1, 1000) };
        if (!string.IsNullOrWhiteSpace(pageToken))
            query.Add("pageToken=" + Uri.EscapeDataString(pageToken));
        var uri = ProductsEndpoint + "/accounts/" + Uri.EscapeDataString(AccountId) + "/products?" + string.Join("&", query);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(cancellationToken));
        using var response = await clients.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException("Google Merchant API product listing failed (HTTP " + (int)response.StatusCode + ").");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var products = new List<GmcCatalogProduct>();
        if (document.RootElement.TryGetProperty("products", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
            {
                var offerId = StringProperty(item, "offerId") ?? StringProperty(item, "name") ?? "";
                if (offerId.Length == 0)
                    continue;
                products.Add(new GmcCatalogProduct(
                    offerId,
                    NestedStringProperty(item, "productAttributes", "title"),
                    StringProperty(item, "contentLanguage"),
                    StringProperty(item, "feedLabel"),
                    StringProperty(item, "dataSource"),
                    item.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True));
            }

        return new GmcCatalogPage(
            products,
            document.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedToken is not null && cachedToken.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                return cachedToken.Value;

            if (string.IsNullOrWhiteSpace(CredentialsPath) || !File.Exists(CredentialsPath))
                throw new ProviderException("GMC service-account JSON was not found. Keep it outside the repository.");
            using var credentials = JsonDocument.Parse(await File.ReadAllTextAsync(CredentialsPath, cancellationToken));
            var root = credentials.RootElement;
            var clientEmail = StringProperty(root, "client_email");
            var privateKey = StringProperty(root, "private_key")?.Replace("\\n", "\n", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(clientEmail) || string.IsNullOrWhiteSpace(privateKey))
                throw new ProviderException("GMC service-account JSON is missing client_email or private_key.");

            var now = DateTimeOffset.UtcNow;
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "RS256",
                typ = "JWT"
            }));
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = clientEmail,
                scope = Scope,
                aud = TokenEndpoint,
                iat = now.ToUnixTimeSeconds(),
                exp = now.AddMinutes(55).ToUnixTimeSeconds()
            }));
            var unsigned = header + "." + payload;
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKey);
            var signature = rsa.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var assertion = unsigned + "." + Base64Url(signature);

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion
                })
            };
            using var response = await clients.CreateClient().SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException("Google service-account authentication failed (HTTP " + (int)response.StatusCode + ").");
            using var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var accessToken = StringProperty(token.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new ProviderException("Google service-account authentication returned no access token.");
            var lifetime = token.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 3600;
            cachedToken = new CachedToken(accessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, lifetime - 60)));
            return accessToken;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string? NestedStringProperty(JsonElement element, string parent, string child) =>
        element.TryGetProperty(parent, out var nested) ? StringProperty(nested, child) : null;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);
}
