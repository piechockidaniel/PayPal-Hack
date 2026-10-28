namespace PayPal360.Contracts.Exceptions;

public sealed class ProviderException(string message) : Exception(message);
