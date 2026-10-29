namespace Davids.Services.Customers.Application.Commands;

public sealed record CompleteCustomerRegistration(Guid CustomerId, string FullName, string Address);
