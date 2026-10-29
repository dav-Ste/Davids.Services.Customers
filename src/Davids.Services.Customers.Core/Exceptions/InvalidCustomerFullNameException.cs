namespace Davids.Services.Customers.Core.Exceptions;

public sealed class InvalidCustomerFullNameException(Guid id)
    : DomainException("invalid_customer_fullname", $"Customer '{id}' must have a full name of at most 200 characters.")
{
    public Guid Id { get; } = id;
}
