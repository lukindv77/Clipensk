using Clipensk.Core.History;
using Clipensk.Core.Storage;

namespace Clipensk.Storage.Databases;

public sealed class ArchiveSplitPlanner
{
    private const int MaxSplitSequence = 9_999;

    public IReadOnlyList<PendingArchiveSplitSegment> Build(
        ArchiveFileName sourceFileName,
        Guid sourceDatabaseId,
        JournalDateRange sourceCoverage,
        IReadOnlyCollection<JournalDateRange> requestedResultRanges,
        IReadOnlyCollection<ArchiveFileName> occupiedFamilyFileNames,
        Func<Guid> databaseIdFactory)
    {
        ArgumentNullException.ThrowIfNull(requestedResultRanges);
        ArgumentNullException.ThrowIfNull(occupiedFamilyFileNames);
        ArgumentNullException.ThrowIfNull(databaseIdFactory);

        if (!IsCanonicalArchiveFileName(sourceFileName) || sourceDatabaseId == Guid.Empty)
        {
            throw new ArgumentException("Archive split source identity is invalid.");
        }

        if (requestedResultRanges.Count < 2)
        {
            throw new ArgumentException(
                "Archive split requires at least two result ranges.",
                nameof(requestedResultRanges));
        }

        JournalDateRange[] orderedRanges = requestedResultRanges
            .OrderBy(range => range.StartDate)
            .ThenBy(range => range.EndDate)
            .ToArray();
        ValidatePartition(sourceCoverage, orderedRanges);

        var occupiedFileNames = new HashSet<string>(StringComparer.Ordinal);
        var occupiedSequences = new HashSet<int>();
        int maxOccupiedSequence = sourceFileName.SplitSequence;

        foreach (ArchiveFileName occupied in occupiedFamilyFileNames)
        {
            if (!IsCanonicalArchiveFileName(occupied) ||
                occupied.BaseNumber != sourceFileName.BaseNumber)
            {
                throw new ArgumentException(
                    "Occupied archive family snapshot contains a noncanonical or foreign filename.",
                    nameof(occupiedFamilyFileNames));
            }

            if (!occupiedFileNames.Add(occupied.FileName) ||
                !occupiedSequences.Add(occupied.SplitSequence))
            {
                throw new ArgumentException(
                    "Occupied archive family snapshot contains duplicate filenames or sequences.",
                    nameof(occupiedFamilyFileNames));
            }

            maxOccupiedSequence = Math.Max(maxOccupiedSequence, occupied.SplitSequence);
        }

        occupiedFileNames.Add(sourceFileName.FileName);
        occupiedSequences.Add(sourceFileName.SplitSequence);

        int additionalSegmentCount = orderedRanges.Length - 1;
        if (additionalSegmentCount > MaxSplitSequence - maxOccupiedSequence)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedResultRanges),
                "Archive split family suffix allocation exceeds the four-digit canonical bound.");
        }

        var databaseIds = new HashSet<Guid> { sourceDatabaseId };
        var segments = new PendingArchiveSplitSegment[orderedRanges.Length];
        segments[0] = new PendingArchiveSplitSegment(
            0,
            sourceFileName,
            sourceDatabaseId,
            orderedRanges[0]);

        for (int index = 1; index < orderedRanges.Length; index++)
        {
            Guid databaseId = databaseIdFactory();
            if (databaseId == Guid.Empty || !databaseIds.Add(databaseId))
            {
                throw new InvalidOperationException(
                    "Archive split DatabaseId factory must return unique nonempty identifiers.");
            }

            int splitSequence = maxOccupiedSequence + index;
            ArchiveFileName fileName = sourceFileName.NextSplit(splitSequence);
            if (!IsCanonicalArchiveFileName(fileName) || !occupiedFileNames.Add(fileName.FileName))
            {
                throw new InvalidOperationException(
                    "Archive split filename allocation collided with the occupied family snapshot.");
            }

            segments[index] = new PendingArchiveSplitSegment(
                index,
                fileName,
                databaseId,
                orderedRanges[index]);
        }

        return Array.AsReadOnly(segments);
    }

    private static void ValidatePartition(
        JournalDateRange sourceCoverage,
        IReadOnlyList<JournalDateRange> orderedRanges)
    {
        foreach (JournalDateRange range in orderedRanges)
        {
            if (range.StartDate < sourceCoverage.StartDate ||
                range.EndDate > sourceCoverage.EndDate)
            {
                throw new ArgumentException(
                    "Archive split result range falls outside source coverage.",
                    nameof(orderedRanges));
            }
        }

        if (orderedRanges[0].StartDate != sourceCoverage.StartDate ||
            orderedRanges[^1].EndDate != sourceCoverage.EndDate)
        {
            throw new ArgumentException(
                "Archive split result ranges must exactly cover source coverage.",
                nameof(orderedRanges));
        }

        for (int index = 1; index < orderedRanges.Count; index++)
        {
            JournalDateRange previous = orderedRanges[index - 1];
            JournalDateRange current = orderedRanges[index];

            if (current.StartDate <= previous.EndDate)
            {
                throw new ArgumentException(
                    "Archive split result ranges must not overlap.",
                    nameof(orderedRanges));
            }

            if (current.StartDate.DayNumber != previous.EndDate.DayNumber + 1)
            {
                throw new ArgumentException(
                    "Archive split result ranges must not contain gaps.",
                    nameof(orderedRanges));
            }
        }
    }

    private static bool IsCanonicalArchiveFileName(ArchiveFileName fileName) =>
        ArchiveFileName.TryParse(fileName.FileName, out ArchiveFileName parsed) &&
        parsed == fileName &&
        string.Equals(parsed.FileName, fileName.FileName, StringComparison.Ordinal);
}
