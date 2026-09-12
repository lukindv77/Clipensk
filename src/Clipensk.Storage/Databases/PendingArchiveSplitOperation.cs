using Clipensk.Core.History;
using Clipensk.Core.Storage;

namespace Clipensk.Storage.Databases;

public enum ArchiveSplitPhase
{
    Planned = 0,
    ReadyToPublish = 1,
    PhysicalPublished = 2,
    CatalogPublished = 3,
}

public sealed record PendingArchiveSplitSegment(
    int SegmentOrder,
    ArchiveFileName FileName,
    Guid DatabaseId,
    JournalDateRange Coverage);

public sealed record PendingArchiveSplitOperation(
    Guid OperationId,
    ArchiveFileName SourceFileName,
    Guid SourceDatabaseId,
    JournalDateRange SourceCoverage,
    ArchiveSplitPhase Phase,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<PendingArchiveSplitSegment> Segments);
