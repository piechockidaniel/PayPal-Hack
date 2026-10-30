namespace PayPal360.Contracts;

public sealed record GmcCatalogProduct(
    string OfferId,
    string? Title,
    string? ContentLanguage,
    string? FeedLabel,
    string? DataSource,
    bool Archived,
    decimal? Price = null,
    string? Currency = null);

public sealed record GmcCatalogPage(
    IReadOnlyList<GmcCatalogProduct> Products,
    string? NextPageToken);

public interface IReadOnlyCatalogChannel
{
    bool IsConfigured
    {
        get;
    }
    Task<GmcCatalogPage> ListProductsAsync(string tenantId, string? pageToken, int pageSize, CancellationToken cancellationToken);
}
