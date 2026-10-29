namespace Davids.Services.Customers.Core.Exceptions;

public sealed class InvalidCustomerStateFilterException(string state)
    : DomainException("invalid_customer_state_filter", $"Unsupported customer state filter '{state}'.")
{
    public string State { get; } = state;
}
