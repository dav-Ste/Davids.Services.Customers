namespace Davids.Services.Customers.Application.Events;

public sealed record CustomerStateChanged(Guid CustomerId, string CurrentState, string PreviousState);
