using Clipensk.Core.History;

namespace Clipensk.Core.Storage;

public sealed record ArchiveSegmentDescriptor(
    Guid DatabaseId,
    string FileName,
    JournalDateRange Coverage,
    bool IsSealed);
