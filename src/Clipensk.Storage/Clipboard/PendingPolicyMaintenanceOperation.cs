namespace Clipensk.Storage.Clipboard;

public sealed record PendingPolicyMaintenanceOperation(
    Guid OperationId,
    string OperationKind,
    string StateJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
