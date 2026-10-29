using Davids.Services.Customers.Application.Commands;
using Davids.Services.Customers.Application.DTO;
using Davids.Services.Customers.Application.Events;
using Davids.Services.Customers.Core.Entities;
using Davids.Services.Customers.Core.Exceptions;
using Davids.Services.Customers.Core.Repositories;

namespace Davids.Services.Customers.Application.Services;

public sealed class CustomersService(
    ICustomerRepository customers,
    IOutboxWriter outbox) : ICustomersService
{
    public Task<IReadOnlyList<CustomerDto>> GetAllAsync(CancellationToken cancellationToken = default)
        => SearchAsync(new CustomerSearchQuery(), cancellationToken);

    public async Task<IReadOnlyList<CustomerDto>> SearchAsync(
        CustomerSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var effective = query ?? new CustomerSearchQuery();
        CustomerState? parsedState = null;
        if (!string.IsNullOrWhiteSpace(effective.State))
        {
            if (!Enum.TryParse<CustomerState>(effective.State.Trim(), true, out var candidate) ||
                !Enum.IsDefined(candidate) || candidate == CustomerState.Unknown)
            {
                throw new InvalidCustomerStateFilterException(effective.State);
            }

            parsedState = candidate;
        }

        var entities = await customers.SearchAsync(
            effective.Search,
            parsedState,
            effective.IsVip,
            cancellationToken);
        return entities.Select(ToDto).ToArray();
    }

    public async Task<CustomerDetailsDto> GetAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var customer = await GetCustomerAsync(customerId, cancellationToken);
        return ToDetailsDto(customer);
    }

    public async Task<CustomerStateDto> GetStateAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var customer = await GetCustomerAsync(customerId, cancellationToken);
        return new CustomerStateDto { Id = customer.Id, State = StateName(customer.State) };
    }

    public async Task CompleteRegistrationAsync(
        CompleteCustomerRegistration command,
        CancellationToken cancellationToken = default)
    {
        var customer = await GetCustomerAsync(command.CustomerId, cancellationToken);
        if (customer.State != CustomerState.Incomplete)
        {
            throw new CustomerAlreadyRegisteredException(command.CustomerId);
        }

        customer.CompleteRegistration(command.FullName, command.Address);
        outbox.Enqueue("customer_created", new CustomerCreated(customer.Id));
        await customers.SaveChangesAsync(cancellationToken);
    }

    public async Task<CustomerDetailsDto> UpdateAsync(
        UpdateCustomer command,
        CancellationToken cancellationToken = default)
    {
        var customer = await GetCustomerAsync(command.CustomerId, cancellationToken);
        var wasIncomplete = customer.State == CustomerState.Incomplete;

        CustomerState? requestedState = null;
        if (!string.IsNullOrWhiteSpace(command.State))
        {
            if (!Enum.TryParse<CustomerState>(command.State.Trim(), true, out var candidate) ||
                !Enum.IsDefined(candidate) || candidate == CustomerState.Unknown)
            {
                throw new CannotChangeCustomerStateException(customer.Id, CustomerState.Unknown);
            }

            requestedState = candidate;
        }

        var detailsChanged = customer.UpdateDetails(command.FullName, command.Address, command.Email);
        if (!string.IsNullOrWhiteSpace(command.Email) &&
            await customers.ExistsWithEmailAsync(customer.Email, customer.Id, cancellationToken))
        {
            throw new CustomerEmailInUseException(customer.Email);
        }

        var stateChanged = false;
        if (requestedState.HasValue)
        {
            if (customer.State != requestedState.Value)
            {
                var previousState = customer.ChangeState(requestedState.Value);
                stateChanged = true;
                if (wasIncomplete && customer.State == CustomerState.Valid)
                {
                    outbox.Enqueue("customer_created", new CustomerCreated(customer.Id));
                }

                outbox.Enqueue("customer_state_changed", new CustomerStateChanged(
                    customer.Id, StateName(customer.State), StateName(previousState)));
            }
        }
        else if (wasIncomplete)
        {
            var previousState = customer.ChangeState(CustomerState.Valid);
            stateChanged = true;
            outbox.Enqueue("customer_created", new CustomerCreated(customer.Id));
            outbox.Enqueue("customer_state_changed", new CustomerStateChanged(
                customer.Id, StateName(customer.State), StateName(previousState)));
        }

        if (detailsChanged)
        {
            outbox.Enqueue("customer_updated", new CustomerUpdated(
                customer.Id,
                customer.Email,
                customer.FullName,
                customer.Address,
                StateName(customer.State)));
        }

        if (detailsChanged || stateChanged)
        {
            await customers.SaveChangesAsync(cancellationToken);
        }

        return ToDetailsDto(customer);
    }

    public async Task ChangeStateAsync(Guid customerId, string state, CancellationToken cancellationToken = default)
    {
        var customer = await GetCustomerAsync(customerId, cancellationToken);
        if (!Enum.TryParse<CustomerState>(state, true, out var nextState) ||
            !Enum.IsDefined(nextState) || nextState == CustomerState.Unknown)
        {
            throw new CannotChangeCustomerStateException(customer.Id, CustomerState.Unknown);
        }

        if (customer.State == nextState)
        {
            return;
        }

        var previousState = customer.ChangeState(nextState);
        outbox.Enqueue("customer_state_changed", new CustomerStateChanged(
            customer.Id, StateName(customer.State), StateName(previousState)));
        await customers.SaveChangesAsync(cancellationToken);
    }

    public async Task CreateFromSignedUpAsync(SignedUp message, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidCustomerRoleException(message.Role);
        }

        var existing = await customers.GetAsync(message.UserId, cancellationToken);
        if (existing is not null)
        {
            throw new CustomerAlreadyCreatedException(message.UserId);
        }

        var customer = Customer.CreateIncomplete(message.UserId, message.Email, DateTime.UtcNow);
        customers.Add(customer);
        await customers.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordCompletedOrderAsync(OrderCompleted message, CancellationToken cancellationToken = default)
    {
        var customer = await GetCustomerAsync(message.CustomerId, cancellationToken);
        if (customer.AddCompletedOrder(message.OrderId))
        {
            outbox.Enqueue("customer_became_vip", new CustomerBecameVip(customer.Id));
        }

        await customers.SaveChangesAsync(cancellationToken);
    }

    private async Task<Customer> GetCustomerAsync(Guid id, CancellationToken cancellationToken)
        => await customers.GetAsync(id, cancellationToken) ?? throw new CustomerNotFoundException(id);

    private static CustomerDto ToDto(Customer customer) => new()
    {
        Id = customer.Id,
        Email = customer.Email,
        FullName = customer.FullName,
        Address = customer.Address,
        IsVip = customer.IsVip,
        State = StateName(customer.State),
        CreatedAt = DateTime.SpecifyKind(customer.CreatedAt, DateTimeKind.Utc),
        CompletedOrdersCount = customer.CompletedOrders.Count
    };

    private static CustomerDetailsDto ToDetailsDto(Customer customer) => new()
    {
        Id = customer.Id,
        Email = customer.Email,
        FullName = customer.FullName,
        Address = customer.Address,
        IsVip = customer.IsVip,
        State = StateName(customer.State),
        CreatedAt = DateTime.SpecifyKind(customer.CreatedAt, DateTimeKind.Utc),
        CompletedOrdersCount = customer.CompletedOrders.Count,
        CompletedOrders = customer.CompletedOrders.Select(order => order.OrderId).OrderBy(id => id).ToArray()
    };

    private static string StateName(CustomerState state) => state.ToString().ToLowerInvariant();
}
