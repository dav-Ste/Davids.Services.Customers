namespace Davids.Services.Customers.Core.Exceptions;

public sealed class CustomerAlreadyRegisteredException(Guid id)
    : DomainException("customer_already_registered", $"Customer with id '{id}' is already registered.")
{
    public Guid Id { get; } = id;
}
