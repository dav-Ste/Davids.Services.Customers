using Davids.Services.Customers.Core.Entities;

namespace Davids.Services.Customers.Core.Repositories;

public interface ICustomerRepository
{
    Task<Customer?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Customer>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Customer>> SearchAsync(
        string? search,
        CustomerState? state,
        bool? isVip,
        CancellationToken cancellationToken = default);
    Task<bool> ExistsWithEmailAsync(
        string email,
        Guid excludingCustomerId,
        CancellationToken cancellationToken = default);
    void Add(Customer customer);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
