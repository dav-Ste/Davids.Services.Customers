namespace Davids.Services.Customers.Core.Exceptions;

public sealed class InvalidCustomerRoleException(string role)
    : DomainException("invalid_customer_role", $"Cannot create a customer for role '{role}'.");
