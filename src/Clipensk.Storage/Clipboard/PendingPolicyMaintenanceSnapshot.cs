namespace Clipensk.Storage.Clipboard;

public sealed record PendingPolicyMaintenanceSnapshot(
    Guid OperationId,
    string OperationKind,
    string StateJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
