using Davids.Services.Customers.Application.Commands;
using Davids.Services.Customers.Application.DTO;
using Davids.Services.Customers.Application.Events;

namespace Davids.Services.Customers.Application.Services;

public interface ICustomersService
{
    Task<IReadOnlyList<CustomerDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomerDto>> SearchAsync(CustomerSearchQuery query, CancellationToken cancellationToken = default);
    Task<CustomerDetailsDto> GetAsync(Guid customerId, CancellationToken cancellationToken = default);
    Task<CustomerStateDto> GetStateAsync(Guid customerId, CancellationToken cancellationToken = default);
    Task CompleteRegistrationAsync(
        CompleteCustomerRegistration command,
        CancellationToken cancellationToken = default,
        CustomerActorMetadata? actor = null);
    Task<CustomerDetailsDto> UpdateSelfServiceAsync(
        Guid customerId,
        UpdateMyCustomerProfile command,
        CustomerActorMetadata actor,
        CancellationToken cancellationToken = default);
    Task<CustomerDetailsDto> UpdateAsync(
        UpdateCustomer command,
        CancellationToken cancellationToken = default,
        CustomerActorMetadata? actor = null);
    Task ChangeStateAsync(
        Guid customerId,
        string state,
        CancellationToken cancellationToken = default,
        CustomerActorMetadata? actor = null);
    Task CreateFromSignedUpAsync(SignedUp message, CancellationToken cancellationToken = default);
    Task SyncEmailFromIdentityAsync(EmailChanged message, CancellationToken cancellationToken = default);
    Task RecordCompletedOrderAsync(OrderCompleted message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomerProfileChangeAuditDto>> BrowseChangeAuditsAsync(
        int limit = 100,
        Guid? targetCustomerId = null,
        string? actorSub = null,
        CancellationToken cancellationToken = default);
}
