namespace Davids.Services.Customers.Application.Events;

public sealed record CustomerUpdated(
    Guid CustomerId,
    string Email,
    string FullName,
    string Address,
    string State);
