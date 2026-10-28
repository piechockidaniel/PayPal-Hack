namespace PayPal360.Contracts;

// Initial boundaries. No provider claims these are implemented until an adapter is registered and verified.


public interface IOrderSource
{
    Task<Order?> GetOrderAsync(string id, CancellationToken cancellationToken);
}
