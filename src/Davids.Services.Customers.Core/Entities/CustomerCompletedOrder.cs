namespace Davids.Services.Customers.Core.Entities;

public sealed class CustomerCompletedOrder
{
    private CustomerCompletedOrder() { }

    public CustomerCompletedOrder(Guid customerId, Guid orderId)
    {
        CustomerId = customerId;
        OrderId = orderId;
    }

    public Guid CustomerId { get; private set; }
    public Guid OrderId { get; private set; }
}
