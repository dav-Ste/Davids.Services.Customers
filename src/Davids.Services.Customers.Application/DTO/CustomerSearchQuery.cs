namespace Davids.Services.Customers.Application.DTO;

public sealed record CustomerSearchQuery(
    string? Search = null,
    string? State = null,
    bool? IsVip = null);
