namespace Davids.Services.Customers.Core.Exceptions;

public sealed class CustomerAlreadyCreatedException(Guid id)
    : DomainException("customer_already_created", $"Customer with id '{id}' already exists.")
{
    public Guid Id { get; } = id;
}
