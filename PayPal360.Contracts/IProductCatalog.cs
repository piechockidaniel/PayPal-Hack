namespace PayPal360.Contracts;

public interface IProductCatalog
{

    IReadOnlyList<Product> List();
    Product? Find(string id);
}
