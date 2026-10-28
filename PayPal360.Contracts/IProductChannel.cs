namespace PayPal360.Contracts;

public interface IProductChannel
{
    Task<ProductPublication?> GetPublicationAsync(string productId, CancellationToken cancellationToken);
}
