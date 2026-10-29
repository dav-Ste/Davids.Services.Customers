namespace Davids.Services.Customers.Application.Services;

public interface IOutboxWriter
{
    void Enqueue(string eventType, object payload);
}
