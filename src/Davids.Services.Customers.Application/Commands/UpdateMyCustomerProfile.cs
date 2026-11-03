namespace Davids.Services.Customers.Application.Commands;

public sealed record UpdateMyCustomerProfile(
    string FullName,
    string Address);
