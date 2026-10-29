using Davids.Services.Customers.Core.Entities;
using Davids.Services.Customers.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Davids.Services.Customers.Infrastructure.Persistence;

public sealed class CustomerRepository(CustomersDbContext db) : ICustomerRepository
{
    public Task<Customer?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => db.Customers.Include(customer => customer.CompletedOrders)
            .SingleOrDefaultAsync(customer => customer.Id == id, cancellationToken);

    public Task<IReadOnlyList<Customer>> GetAllAsync(CancellationToken cancellationToken = default)
        => SearchAsync(null, null, null, cancellationToken);

    public async Task<IReadOnlyList<Customer>> SearchAsync(
        string? search,
        CustomerState? state,
        bool? isVip,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Customer> query = db.Customers.AsNoTracking()
            .Include(customer => customer.CompletedOrders);

        if (state.HasValue)
        {
            query = query.Where(customer => customer.State == state.Value);
        }

        if (isVip.HasValue)
        {
            query = query.Where(customer => customer.IsVip == isVip.Value);
        }

        var trimmed = search?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return await query
                .OrderBy(customer => customer.CreatedAt)
                .ThenBy(customer => customer.Id)
                .ToArrayAsync(cancellationToken);
        }

        var term = trimmed.ToLowerInvariant();
        if (Guid.TryParse(trimmed, out var exactId))
        {
            return await query
                .Where(customer =>
                    customer.Id == exactId ||
                    customer.FullName.ToLower().Contains(term) ||
                    customer.Email.ToLower().Contains(term) ||
                    customer.Address.ToLower().Contains(term))
                .OrderBy(customer => customer.CreatedAt)
                .ThenBy(customer => customer.Id)
                .ToArrayAsync(cancellationToken);
        }

        if (!IsHexOrHyphen(trimmed))
        {
            return await query
                .Where(customer =>
                    customer.FullName.ToLower().Contains(term) ||
                    customer.Email.ToLower().Contains(term) ||
                    customer.Address.ToLower().Contains(term))
                .OrderBy(customer => customer.CreatedAt)
                .ThenBy(customer => customer.Id)
                .ToArrayAsync(cancellationToken);
        }

        var candidates = await query
            .OrderBy(customer => customer.CreatedAt)
            .ThenBy(customer => customer.Id)
            .ToArrayAsync(cancellationToken);

        return candidates
            .Where(customer =>
                customer.Id.ToString("D").Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                customer.Id.ToString("N").Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                customer.FullName.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                customer.Email.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                customer.Address.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public Task<bool> ExistsWithEmailAsync(
        string email,
        Guid excludingCustomerId,
        CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return db.Customers.AsNoTracking()
            .AnyAsync(
                customer => customer.Id != excludingCustomerId && customer.Email.ToLower() == normalized,
                cancellationToken);
    }

    public void Add(Customer customer) => db.Customers.Add(customer);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        => await db.SaveChangesAsync(cancellationToken);

    private static bool IsHexOrHyphen(string value)
        => value.All(character => character == '-' || Uri.IsHexDigit(character));
}
