namespace Davids.Services.Customers.Application.Events;

// Consumed from the identity exchange. Property names are part of the integration contract.
public sealed record SignedUp(Guid UserId, string Email, string Role);
