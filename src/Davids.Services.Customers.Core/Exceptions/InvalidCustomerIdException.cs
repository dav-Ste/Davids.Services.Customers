namespace Davids.Services.Customers.Core.Exceptions;

public sealed class InvalidCustomerIdException()
    : DomainException("invalid_customer_id", "A non-empty customer id is required.")
{
}
