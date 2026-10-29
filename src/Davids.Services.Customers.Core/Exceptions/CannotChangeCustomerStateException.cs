using Davids.Services.Customers.Core.Entities;

namespace Davids.Services.Customers.Core.Exceptions;

public sealed class CannotChangeCustomerStateException(Guid id, CustomerState state)
    : DomainException("cannot_change_customer_state", $"Cannot change customer '{id}' state to '{state}'.")
{
    public Guid Id { get; } = id;
    public CustomerState State { get; } = state;
}
