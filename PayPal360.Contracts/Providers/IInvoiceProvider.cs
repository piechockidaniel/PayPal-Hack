namespace PayPal360.Contracts.Providers;

public interface IInvoiceProvider
{
    Task<CommerceInvoice?> GetInvoiceAsync(string externalId, CancellationToken cancellationToken);
}
