namespace Davids.Services.Customers.Application.Commands;

public sealed record UpdateCustomer(
    Guid CustomerId,
    string FullName,
    string Address,
    string? Email = null,
    string? State = null);
