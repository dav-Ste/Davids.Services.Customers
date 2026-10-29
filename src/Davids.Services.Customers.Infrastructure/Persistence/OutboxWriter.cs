using System.Text.Json;
using Davids.Services.Customers.Application.Services;

namespace Davids.Services.Customers.Infrastructure.Persistence;

public sealed class OutboxWriter(CustomersDbContext db) : IOutboxWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Enqueue(string eventType, object payload)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("An outbox event type is required.", nameof(eventType));
        }

        db.Outbox.Add(new OutboxMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = eventType,
            Payload = JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions),
            CreatedAt = DateTime.UtcNow
        });
    }
}
