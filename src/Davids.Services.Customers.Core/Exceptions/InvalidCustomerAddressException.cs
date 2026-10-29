namespace Davids.Services.Customers.Core.Exceptions;

public sealed class InvalidCustomerAddressException(Guid id)
    : DomainException("invalid_customer_address", $"Customer '{id}' must have a non-empty address of at most 500 characters.")
{
    public Guid Id { get; } = id;
}
