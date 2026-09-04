using Clipensk.Core.History;

namespace Clipensk.Core.Storage;

public sealed record StorageQueryPlan(
    JournalDateRange RequestedRange,
    bool QueryCurrent,
    IReadOnlyList<ArchiveSegmentDescriptor> ArchiveSegments);
