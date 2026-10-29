namespace Davids.Services.Customers.Core.Exceptions;

public sealed class InvalidCustomerEmailException(Guid id)
    : DomainException("invalid_customer_email", $"Customer '{id}' must have a valid email address of at most 320 characters.")
{
    public Guid Id { get; } = id;
}
