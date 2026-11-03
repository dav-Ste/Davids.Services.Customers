using System.Diagnostics.Metrics;
using Davids.Services.Customers.Core.Entities;
using Davids.Services.Customers.Core.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Davids.Services.Customers.Infrastructure.Persistence;

public sealed class CustomerRepository(
    CustomersDbContext db,
    ILoggerFactory? loggerFactory = null) : ICustomerRepository
{
    private static readonly Meter AuditMeter = new("Davids.Security.Audit", "1.0.0");
    private static readonly Counter<long> ChangeAuditCounter =
        AuditMeter.CreateCounter<long>("davids.security.change_audit", unit: "{event}");

    private readonly List<CustomerProfileChangeAuditEntry> _pendingAudits = [];

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

    public void AddChangeAudit(CustomerProfileChangeAuditEntry entry)
    {
        db.CustomerProfileChangeAuditLogs.Add(entry);
        _pendingAudits.Add(entry);
    }

    public async Task<IReadOnlyList<CustomerProfileChangeAuditEntry>> BrowseChangeAuditsAsync(
        int limit = 100,
        Guid? targetCustomerId = null,
        string? actorSub = null,
        CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(limit, 1, 500);
        IQueryable<CustomerProfileChangeAuditEntry> query = db.CustomerProfileChangeAuditLogs.AsNoTracking();

        if (targetCustomerId.HasValue)
        {
            query = query.Where(x => x.TargetCustomerId == targetCustomerId.Value);
        }

        if (!string.IsNullOrWhiteSpace(actorSub))
        {
            var trimmed = actorSub.Trim();
            query = query.Where(x => x.ActorSub == trimmed);
        }

        return await query
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenByDescending(x => x.Id)
            .Take(clamped)
            .ToArrayAsync(cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await db.SaveChangesAsync(cancellationToken);
        if (_pendingAudits.Count == 0)
        {
            return;
        }

        var auditLogger = loggerFactory?.CreateLogger("Davids.Security.Audit");
        foreach (var entry in _pendingAudits)
        {
            ChangeAuditCounter.Add(
                1,
                new KeyValuePair<string, object?>("service", "customers"),
                new KeyValuePair<string, object?>("action", entry.Action),
                new KeyValuePair<string, object?>("actor_authority", entry.ActorAuthority));
            auditLogger?.LogInformation(
                "Audit: recorded customers profile change {Action} on {TargetCustomerId} by {ActorAuthority} {ActorSub} (ActorRole={ActorRole}, SecurityAuditEvent={SecurityAuditEvent}, CorrelationId={CorrelationId}).",
                entry.Action,
                entry.TargetCustomerId,
                entry.ActorAuthority,
                entry.ActorSub,
                entry.ActorRole,
                "ChangeAudit",
                entry.CorrelationId ?? "none");
        }

        _pendingAudits.Clear();
    }

    private static bool IsHexOrHyphen(string value)
        => value.All(character => character == '-' || Uri.IsHexDigit(character));
}
