namespace Davids.Services.Customers.Core.Exceptions;

public sealed class CustomerEmailInUseException(string email)
    : DomainException("customer_email_in_use", $"A customer account already uses email '{email}'.")
{
    public string Email { get; } = email;
}
