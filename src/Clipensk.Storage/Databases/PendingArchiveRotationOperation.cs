using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;

namespace Clipensk.Storage.Databases;

public enum ArchiveRotationPhase
{
    Planned = 0,
    ReadyToPublish = 1,
    PhysicalPublished = 2,
    SourcePurged = 3,
    CatalogPublished = 4,
}

/// <summary>
/// One immutable rotation output. <see cref="ShadowPhysicalSizeBytes"/> is the measured physical
/// length of the validated staging shadow database, never a sum of clipboard payload bytes.
/// </summary>
public sealed record PendingArchiveRotationTarget(
    int SegmentOrder,
    ArchiveFileName FileName,
    Guid DatabaseId,
    JournalDateRange Coverage,
    long ExpectedRecordCount,
    long ShadowPhysicalSizeBytes);

/// <summary>
/// Durable pending Archive Rotation marker. The policy snapshot records the thresholds that were
/// in force when the plan was committed so recovery never re-plans from current settings.
/// </summary>
public sealed record PendingArchiveRotationOperation(
    Guid OperationId,
    ArchiveRotationSettings PolicySnapshot,
    ArchiveRotationPhase Phase,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<PendingArchiveRotationTarget> Targets);
