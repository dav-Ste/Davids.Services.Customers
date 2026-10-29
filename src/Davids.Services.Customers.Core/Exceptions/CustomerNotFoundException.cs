namespace Davids.Services.Customers.Core.Exceptions;

public sealed class CustomerNotFoundException(Guid id)
    : DomainException("customer_not_found", $"Customer with id '{id}' was not found.")
{
    public Guid Id { get; } = id;
}
