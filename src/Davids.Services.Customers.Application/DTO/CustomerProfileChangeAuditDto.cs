using System.Text.Json;
using System.Text.Json.Serialization;
using Davids.Services.Customers.Core.Entities;

namespace Davids.Services.Customers.Application.DTO;

public sealed record CustomerActorMetadata(
    string ActorSub,
    string? ActorEmail,
    string ActorRole,
    string ActorAuthority,
    string? CorrelationId = null);

public sealed record CustomerProfileFieldChangeDto(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("oldValue")] string? OldValue,
    [property: JsonPropertyName("newValue")] string? NewValue);

public sealed record CustomerProfileChangeAuditDto(
    Guid Id,
    string Service,
    string Action,
    string TargetType,
    string TargetId,
    string? TargetLabel,
    string ActorSub,
    string? ActorEmail,
    string ActorRole,
    string ActorAuthority,
    IReadOnlyList<CustomerProfileFieldChangeDto> Changes,
    string? CorrelationId,
    DateTime OccurredAtUtc)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static CustomerProfileChangeAuditDto FromEntity(CustomerProfileChangeAuditEntry entry)
    {
        var changes = string.IsNullOrWhiteSpace(entry.ChangedFieldsJson)
            ? []
            : JsonSerializer.Deserialize<List<CustomerProfileFieldChangeDto>>(entry.ChangedFieldsJson, JsonOptions) ?? [];

        return new CustomerProfileChangeAuditDto(
            entry.Id,
            "customers",
            entry.Action,
            "customer_profile",
            entry.TargetCustomerId.ToString("D"),
            entry.TargetEmail,
            entry.ActorSub,
            entry.ActorEmail,
            entry.ActorRole,
            entry.ActorAuthority,
            changes,
            entry.CorrelationId,
            DateTime.SpecifyKind(entry.OccurredAtUtc, DateTimeKind.Utc));
    }
}
