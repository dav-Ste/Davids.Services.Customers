namespace Davids.Services.Customers.Core.Entities;

public sealed class CustomerProfileChangeAuditEntry
{
    private CustomerProfileChangeAuditEntry() { }

    public CustomerProfileChangeAuditEntry(
        Guid id,
        DateTime occurredAtUtc,
        Guid targetCustomerId,
        string targetEmail,
        string action,
        string actorSub,
        string? actorEmail,
        string actorRole,
        string actorAuthority,
        string changedFieldsJson,
        string? correlationId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Change audit ID cannot be empty.", nameof(id));
        }

        if (targetCustomerId == Guid.Empty)
        {
            throw new ArgumentException("Target customer ID cannot be empty.", nameof(targetCustomerId));
        }

        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("Audit action cannot be empty.", nameof(action));
        }

        Id = id;
        OccurredAtUtc = occurredAtUtc;
        TargetCustomerId = targetCustomerId;
        TargetEmail = Truncate(targetEmail?.Trim() ?? string.Empty, 320);
        Action = Truncate(action.Trim(), 64);
        ActorSub = Truncate(string.IsNullOrWhiteSpace(actorSub) ? targetCustomerId.ToString("D") : actorSub.Trim(), 64);
        ActorEmail = string.IsNullOrWhiteSpace(actorEmail) ? null : Truncate(actorEmail.Trim(), 320);
        ActorRole = Truncate(string.IsNullOrWhiteSpace(actorRole) ? "user" : actorRole.Trim(), 32);
        ActorAuthority = Truncate(string.IsNullOrWhiteSpace(actorAuthority) ? "customer" : actorAuthority.Trim(), 32);
        ChangedFieldsJson = string.IsNullOrWhiteSpace(changedFieldsJson) ? "[]" : changedFieldsJson;
        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? null : Truncate(correlationId.Trim(), 128);
    }

    public Guid Id { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public Guid TargetCustomerId { get; private set; }
    public string TargetEmail { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string ActorSub { get; private set; } = string.Empty;
    public string? ActorEmail { get; private set; }
    public string ActorRole { get; private set; } = string.Empty;
    public string ActorAuthority { get; private set; } = string.Empty;
    public string ChangedFieldsJson { get; private set; } = "[]";
    public string? CorrelationId { get; private set; }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
