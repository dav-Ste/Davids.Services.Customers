using Davids.Services.Customers.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Davids.Services.Customers.Infrastructure.Persistence;

public sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerCompletedOrder> CompletedOrders => Set<CustomerCompletedOrder>();
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
