namespace PayPal360.Contracts.Providers;

public interface IFulfillmentProvider
{
    Task<Shipment?> GetShipmentAsync(string externalId, CancellationToken cancellationToken);
}
