using Clipensk.Core.History;

namespace Clipensk.Core.Storage;

public sealed record CurrentStoreDescriptor(JournalDateRange? AvailableRange);
