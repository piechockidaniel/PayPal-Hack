
namespace PayPal360;

// Initial boundaries. No provider claims these are implemented until an adapter is registered and verified.
public record CommerceInvoice(string ExternalId, string Status);
public record Shipment(string ExternalId, string Status, string? TrackingNumber);
public record ProductPublication(string ProductId, string ChannelReference, string Status);

public interface IOrderSource {
    Task<Order?> GetOrderAsync(string id, CancellationToken cancellationToken);
}
public interface IInvoiceProvider {
    Task<CommerceInvoice?> GetInvoiceAsync(string externalId, CancellationToken cancellationToken);
}
public interface IFulfillmentProvider {
    Task<Shipment?> GetShipmentAsync(string externalId, CancellationToken cancellationToken);
}
public interface IProductChannel {
    Task<ProductPublication?> GetPublicationAsync(string productId, CancellationToken cancellationToken);
}
