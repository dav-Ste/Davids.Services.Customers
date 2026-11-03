using System.Text.Json;
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
    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task<IReadOnlyList<CustomerDto>> GetAllAsync(CancellationToken cancellationToken = default)
        => SearchAsync(new CustomerSearchQuery(), cancellationToken);

    public async Task<IReadOnlyList<CustomerDto>> SearchAsync(
        CustomerSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var effective = query ?? new CustomerSearchQuery();
        CustomerState? parsedState = null;
        if (!string.IsNullOrEmpty(effective.State))
        {
            if (!TryParseStrictState(effective.State, out var candidate))
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
        CancellationToken cancellationToken = default,
        CustomerActorMetadata? actor = null)
    {
        var customer = await GetCustomerAsync(command.CustomerId, cancellationToken);
        if (customer.State != CustomerState.Incomplete)
        {
            throw new CustomerAlreadyRegisteredException(command.CustomerId);
        }

        var oldName = customer.FullName;
        var oldAddress = customer.Address;
        var oldState = StateName(customer.State);
        customer.CompleteRegistration(command.FullName, command.Address);

        var changes = new List<CustomerProfileFieldChangeDto>
        {
            new("fullName", oldName, customer.FullName),
            new("address", oldAddress, customer.Address),
            new("state", oldState, StateName(customer.State))
        };
        RecordChangeAudit(customer, "registration_completed", changes, actor, defaultAuthority: "customer", defaultRole: "user");

        outbox.Enqueue("customer_created", new CustomerCreated(customer.Id));
        await customers.SaveChangesAsync(cancellationToken);
    }

    public async Task<CustomerDetailsDto> UpdateSelfServiceAsync(
        Guid customerId,
        UpdateMyCustomerProfile command,
        CustomerActorMetadata actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);

        var customer = await GetCustomerAsync(customerId, cancellationToken);
        var oldName = customer.FullName;
        var oldAddress = customer.Address;
        var oldState = StateName(customer.State);

        if (customer.State == CustomerState.Incomplete)
        {
            customer.CompleteRegistration(command.FullName, command.Address);
            var completionChanges = new List<CustomerProfileFieldChangeDto>
            {
                new("fullName", oldName, customer.FullName),
                new("address", oldAddress, customer.Address),
                new("state", oldState, StateName(customer.State))
            };
            RecordChangeAudit(customer, "registration_completed", completionChanges, actor, "customer", "user");
            outbox.Enqueue("customer_created", new CustomerCreated(customer.Id));
            await customers.SaveChangesAsync(cancellationToken);
            return ToDetailsDto(customer);
        }

        var changed = customer.UpdateSelfServiceDetails(command.FullName, command.Address);
        if (changed)
        {
            var changes = new List<CustomerProfileFieldChangeDto>();
            if (!string.Equals(oldName, customer.FullName, StringComparison.Ordinal))
            {
                changes.Add(new CustomerProfileFieldChangeDto("fullName", oldName, customer.FullName));
            }

            if (!string.Equals(oldAddress, customer.Address, StringComparison.Ordinal))
            {
                changes.Add(new CustomerProfileFieldChangeDto("address", oldAddress, customer.Address));
            }

            RecordChangeAudit(customer, "profile_updated", changes, actor, "customer", "user");
            outbox.Enqueue("customer_updated", new CustomerUpdated(
                customer.Id,
                customer.Email,
                customer.FullName,
                customer.Address,
                StateName(customer.State)));
            await customers.SaveChangesAsync(cancellationToken);
        }

        return ToDetailsDto(customer);
    }

    public async Task<CustomerDetailsDto> UpdateAsync(
        UpdateCustomer command,
        CancellationToken cancellationToken = default,
        CustomerActorMetadata? actor = null)
    {
        var customer = await GetCustomerAsync(command.CustomerId, cancellationToken);
        var wasIncomplete = customer.State == CustomerState.Incomplete;
        var oldName = customer.FullName;
        var oldAddress = customer.Address;
        var oldEmail = customer.Email;
        var oldState = StateName(customer.State);

        CustomerState? requestedState = null;
        if (command.State is not null)
        {
            if (!TryParseStrictState(command.State, out var candidate))
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
            var changes = new List<CustomerProfileFieldChangeDto>();
            if (!string.Equals(oldName, customer.FullName, StringComparison.Ordinal))
            {
                changes.Add(new CustomerProfileFieldChangeDto("fullName", oldName, customer.FullName));
            }

            if (!string.Equals(oldAddress, customer.Address, StringComparison.Ordinal))
            {
                changes.Add(new CustomerProfileFieldChangeDto("address", oldAddress, customer.Address));
            }

            if (!string.Equals(oldEmail, customer.Email, StringComparison.Ordinal))
            {
                changes.Add(new CustomerProfileFieldChangeDto("email", oldEmail, customer.Email));
            }

            if (!string.Equals(oldState, StateName(customer.State), StringComparison.Ordinal))
            {
                changes.Add(new CustomerProfileFieldChangeDto("state", oldState, StateName(customer.State)));
            }

            RecordChangeAudit(customer, "profile_updated", changes, actor, defaultAuthority: "employee", defaultRole: "admin");
            await customers.SaveChangesAsync(cancellationToken);
        }

        return ToDetailsDto(customer);
    }

    public async Task ChangeStateAsync(
        Guid customerId,
        string state,
        CancellationToken cancellationToken = default,
        CustomerActorMetadata? actor = null)
    {
        var customer = await GetCustomerAsync(customerId, cancellationToken);
        if (!TryParseStrictState(state, out var nextState))
        {
            throw new CannotChangeCustomerStateException(customer.Id, CustomerState.Unknown);
        }

        if (customer.State == nextState)
        {
            return;
        }

        var previousState = customer.ChangeState(nextState);
        var changes = new List<CustomerProfileFieldChangeDto>
        {
            new("state", StateName(previousState), StateName(customer.State))
        };
        RecordChangeAudit(customer, "state_changed", changes, actor, defaultAuthority: "employee", defaultRole: "admin");

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

    public async Task SyncEmailFromIdentityAsync(EmailChanged message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var customer = await customers.GetAsync(message.UserId, cancellationToken);
        if (customer is null)
        {
            return;
        }

        if (await customers.ExistsWithEmailAsync(message.NewEmail, customer.Id, cancellationToken))
        {
            throw new CustomerEmailInUseException(message.NewEmail);
        }

        var oldEmail = customer.Email;
        if (!customer.SyncEmailFromIdentity(message.NewEmail))
        {
            return;
        }

        var changes = new List<CustomerProfileFieldChangeDto>
        {
            new("email", oldEmail, customer.Email)
        };
        customers.AddChangeAudit(new CustomerProfileChangeAuditEntry(
            Guid.NewGuid(),
            DateTime.UtcNow,
            customer.Id,
            customer.Email,
            "email_synced_from_identity",
            "identity-outbox",
            customer.Email,
            "system",
            "identity-sync",
            JsonSerializer.Serialize(changes, AuditJsonOptions),
            correlationId: null));

        outbox.Enqueue("customer_updated", new CustomerUpdated(
            customer.Id,
            customer.Email,
            customer.FullName,
            customer.Address,
            StateName(customer.State)));
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

    public async Task<IReadOnlyList<CustomerProfileChangeAuditDto>> BrowseChangeAuditsAsync(
        int limit = 100,
        Guid? targetCustomerId = null,
        string? actorSub = null,
        CancellationToken cancellationToken = default)
    {
        var entries = await customers.BrowseChangeAuditsAsync(limit, targetCustomerId, actorSub, cancellationToken);
        return entries.Select(CustomerProfileChangeAuditDto.FromEntity).ToArray();
    }

    private void RecordChangeAudit(
        Customer customer,
        string action,
        List<CustomerProfileFieldChangeDto> changes,
        CustomerActorMetadata? actor,
        string defaultAuthority,
        string defaultRole)
    {
        if (changes.Count == 0)
        {
            return;
        }

        var actorSub = string.IsNullOrWhiteSpace(actor?.ActorSub)
            ? customer.Id.ToString("D")
            : actor.ActorSub;
        var actorAuthority = string.IsNullOrWhiteSpace(actor?.ActorAuthority)
            ? defaultAuthority
            : actor.ActorAuthority;
        var actorRole = string.IsNullOrWhiteSpace(actor?.ActorRole)
            ? defaultRole
            : actor.ActorRole;

        customers.AddChangeAudit(new CustomerProfileChangeAuditEntry(
            Guid.NewGuid(),
            DateTime.UtcNow,
            customer.Id,
            customer.Email,
            action,
            actorSub,
            actor?.ActorEmail ?? (actorAuthority == "customer" ? customer.Email : null),
            actorRole,
            actorAuthority,
            JsonSerializer.Serialize(changes, AuditJsonOptions),
            actor?.CorrelationId));
    }

    private async Task<Customer> GetCustomerAsync(Guid id, CancellationToken cancellationToken)
        => await customers.GetAsync(id, cancellationToken) ?? throw new CustomerNotFoundException(id);

    private static bool TryParseStrictState(string? raw, out CustomerState state)
    {
        switch (raw)
        {
            case "valid":
                state = CustomerState.Valid;
                return true;
            case "incomplete":
                state = CustomerState.Incomplete;
                return true;
            case "suspicious":
                state = CustomerState.Suspicious;
                return true;
            case "locked":
                state = CustomerState.Locked;
                return true;
            default:
                state = CustomerState.Unknown;
                return false;
        }
    }

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
