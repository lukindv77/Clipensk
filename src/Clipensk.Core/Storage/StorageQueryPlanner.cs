using Clipensk.Core.History;

namespace Clipensk.Core.Storage;

public sealed class StorageQueryPlanner
{
    public StorageQueryPlan Build(
        JournalDateRange requestedRange,
        CurrentStoreDescriptor current,
        IReadOnlyCollection<ArchiveSegmentDescriptor> archives)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(archives);

        ValidateArchiveCoverage(archives);

        bool queryCurrent = current.AvailableRange is { } currentRange
            && currentRange.Intersects(requestedRange);

        ArchiveSegmentDescriptor[] selectedArchives = archives
            .Where(segment => segment.Coverage.Intersects(requestedRange))
            .OrderBy(segment => segment.Coverage.StartDate)
            .ThenBy(segment => segment.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StorageQueryPlan(requestedRange, queryCurrent, selectedArchives);
    }

    public static void ValidateArchiveCoverage(IReadOnlyCollection<ArchiveSegmentDescriptor> archives)
    {
        ArchiveSegmentDescriptor[] ordered = archives
            .OrderBy(segment => segment.Coverage.StartDate)
            .ThenBy(segment => segment.Coverage.EndDate)
            .ToArray();

        for (int index = 1; index < ordered.Length; index++)
        {
            ArchiveSegmentDescriptor previous = ordered[index - 1];
            ArchiveSegmentDescriptor current = ordered[index];

            if (previous.Coverage.Intersects(current.Coverage))
            {
                throw new InvalidOperationException(
                    $"Архивные сегменты '{previous.FileName}' и '{current.FileName}' имеют пересекающиеся периоды: " +
                    $"{previous.Coverage.StartDate:yyyy-MM-dd}..{previous.Coverage.EndDate:yyyy-MM-dd} и " +
                    $"{current.Coverage.StartDate:yyyy-MM-dd}..{current.Coverage.EndDate:yyyy-MM-dd}.");
            }
        }
    }
}
