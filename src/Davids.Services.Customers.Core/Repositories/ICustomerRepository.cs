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
    void AddChangeAudit(CustomerProfileChangeAuditEntry entry);
    Task<IReadOnlyList<CustomerProfileChangeAuditEntry>> BrowseChangeAuditsAsync(
        int limit = 100,
        Guid? targetCustomerId = null,
        string? actorSub = null,
        CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
