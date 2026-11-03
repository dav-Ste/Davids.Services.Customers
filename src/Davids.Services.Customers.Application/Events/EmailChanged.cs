namespace Davids.Services.Customers.Application.Events;

public sealed record EmailChanged(Guid UserId, string OldEmail, string NewEmail);
