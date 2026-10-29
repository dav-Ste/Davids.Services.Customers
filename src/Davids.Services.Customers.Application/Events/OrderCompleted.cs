namespace Davids.Services.Customers.Application.Events;

// Consumed from the orders exchange.
public sealed record OrderCompleted(Guid OrderId, Guid CustomerId);
