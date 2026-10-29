namespace Davids.Services.Customers.Application.DTO;

public sealed class CustomerStateDto
{
    public Guid Id { get; set; }
    public string State { get; set; } = string.Empty;
}
