using Davids.Services.Customers.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Davids.Services.Customers.Infrastructure.Persistence;

public sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerCompletedOrder> CompletedOrders => Set<CustomerCompletedOrder>();
    public DbSet<CustomerProfileChangeAuditEntry> CustomerProfileChangeAuditLogs => Set<CustomerProfileChangeAuditEntry>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.ToTable("Customers");
            entity.HasKey(customer => customer.Id);
            entity.Property(customer => customer.Email).HasMaxLength(320).IsRequired();
            entity.Property(customer => customer.FullName).HasMaxLength(200).IsRequired();
            entity.Property(customer => customer.Address).HasMaxLength(500).IsRequired();
            entity.Property(customer => customer.State).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.HasIndex(customer => customer.Email).IsUnique();
            entity.HasMany(customer => customer.CompletedOrders)
                .WithOne()
                .HasForeignKey(order => order.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomerCompletedOrder>(entity =>
        {
            entity.ToTable("CustomerCompletedOrders");
            entity.HasKey(order => new { order.CustomerId, order.OrderId });
            entity.HasIndex(order => order.OrderId);
        });

        modelBuilder.Entity<CustomerProfileChangeAuditEntry>(entity =>
        {
            entity.ToTable("CustomerProfileChangeAuditLogs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TargetEmail).IsRequired().HasMaxLength(320);
            entity.Property(x => x.Action).IsRequired().HasMaxLength(64);
            entity.Property(x => x.ActorSub).IsRequired().HasMaxLength(64);
            entity.Property(x => x.ActorEmail).HasMaxLength(320);
            entity.Property(x => x.ActorRole).IsRequired().HasMaxLength(32);
            entity.Property(x => x.ActorAuthority).IsRequired().HasMaxLength(32);
            entity.Property(x => x.ChangedFieldsJson).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(128);
            entity.HasIndex(x => x.OccurredAtUtc);
            entity.HasIndex(x => new { x.TargetCustomerId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.ActorSub, x.OccurredAtUtc });
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("Outbox");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Id).HasMaxLength(64);
            entity.Property(message => message.Type).HasMaxLength(128).IsRequired();
            entity.Property(message => message.Payload).IsRequired();
            entity.HasIndex(message => message.PublishedAt);
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("Inbox");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Id).HasMaxLength(128);
        });
    }
}

public sealed class OutboxMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public sealed class InboxMessage
{
    public string Id { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}
