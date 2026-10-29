namespace Davids.Services.Customers.Application.DTO;

public sealed class CustomerDetailsDto : CustomerDto
{
    public IEnumerable<Guid> CompletedOrders { get; set; } = [];
}
