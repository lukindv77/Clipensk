using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ArchiveSplitPlannerTests
{
    private readonly ArchiveSplitPlanner _planner = new();

    [Fact]
    public void Build_TwoSegments_ReturnsExpectedCanonicalPlan()
    {
        var source = new ArchiveFileName(25, ArchiveFileName.NoSplit);
        Guid sourceId = Id(1);
        Guid secondId = Id(2);

        IReadOnlyList<PendingArchiveSplitSegment> plan = _planner.Build(
            source,
            sourceId,
            Range(2026, 1, 1, 2026, 1, 31),
            [
                Range(2026, 1, 1, 2026, 1, 15),
                Range(2026, 1, 16, 2026, 1, 31),
            ],
            [source],
            IdFactory(secondId));

        Assert.Equal(2, plan.Count);
        Assert.Equal(
            new PendingArchiveSplitSegment(
                0,
                source,
                sourceId,
                Range(2026, 1, 1, 2026, 1, 15)),
            plan[0]);
        Assert.Equal(
            new PendingArchiveSplitSegment(
                1,
                source.NextSplit(1),
                secondId,
                Range(2026, 1, 16, 2026, 1, 31)),
            plan[1]);
    }

    [Fact]
    public void Build_MultiSegment_NormalizesRangeOrderDeterministically()
    {
        var source = new ArchiveFileName(25, 2);
        Guid sourceId = Id(10);
        Guid secondId = Id(11);
        Guid thirdId = Id(12);
        JournalDateRange[] requested =
        [
            Range(2026, 2, 21, 2026, 2, 28),
            Range(2026, 2, 1, 2026, 2, 10),
            Range(2026, 2, 11, 2026, 2, 20),
        ];
        ArchiveFileName[] occupied =
        [
            new(25, ArchiveFileName.NoSplit),
            new(25, 1),
            source,
        ];

        IReadOnlyList<PendingArchiveSplitSegment> first = _planner.Build(
            source,
            sourceId,
            Range(2026, 2, 1, 2026, 2, 28),
            requested,
            occupied,
            IdFactory(secondId, thirdId));
        IReadOnlyList<PendingArchiveSplitSegment> second = _planner.Build(
            source,
            sourceId,
            Range(2026, 2, 1, 2026, 2, 28),
            requested,
            occupied,
            IdFactory(secondId, thirdId));

        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Equal(
            [0, 1, 2],
            first.Select(segment => segment.SegmentOrder).ToArray());
        Assert.Equal(
            [
                Range(2026, 2, 1, 2026, 2, 10),
                Range(2026, 2, 11, 2026, 2, 20),
                Range(2026, 2, 21, 2026, 2, 28),
            ],
            first.Select(segment => segment.Coverage).ToArray());
        Assert.Equal(source, first[0].FileName);
        Assert.Equal(new ArchiveFileName(25, 3), first[1].FileName);
        Assert.Equal(new ArchiveFileName(25, 4), first[2].FileName);
    }

    [Fact]
    public void Build_RejectsGap()
    {
        var source = new ArchiveFileName(30, ArchiveFileName.NoSplit);

        Assert.Throws<ArgumentException>(() => _planner.Build(
            source,
            Id(20),
            Range(2026, 3, 1, 2026, 3, 31),
            [
                Range(2026, 3, 1, 2026, 3, 15),
                Range(2026, 3, 17, 2026, 3, 31),
            ],
            [source],
            IdFactory(Id(21))));
    }

    [Fact]
    public void Build_RejectsOverlap()
    {
        var source = new ArchiveFileName(31, ArchiveFileName.NoSplit);

        Assert.Throws<ArgumentException>(() => _planner.Build(
            source,
            Id(30),
            Range(2026, 4, 1, 2026, 4, 30),
            [
                Range(2026, 4, 1, 2026, 4, 16),
                Range(2026, 4, 16, 2026, 4, 30),
            ],
            [source],
            IdFactory(Id(31))));
    }

    [Fact]
    public void Build_RejectsRangeOutsideSourceCoverage()
    {
        var source = new ArchiveFileName(32, ArchiveFileName.NoSplit);

        Assert.Throws<ArgumentException>(() => _planner.Build(
            source,
            Id(40),
            Range(2026, 5, 2, 2026, 5, 31),
            [
                Range(2026, 5, 1, 2026, 5, 15),
                Range(2026, 5, 16, 2026, 5, 31),
            ],
            [source],
            IdFactory(Id(41))));
    }

    [Fact]
    public void Build_RejectsEmptyOrSingleResultRequest()
    {
        var source = new ArchiveFileName(33, ArchiveFileName.NoSplit);
        JournalDateRange coverage = Range(2026, 6, 1, 2026, 6, 30);

        Assert.Throws<ArgumentException>(() => _planner.Build(
            source,
            Id(50),
            coverage,
            [],
            [source],
            IdFactory()));
        Assert.Throws<ArgumentException>(() => _planner.Build(
            source,
            Id(50),
            coverage,
            [coverage],
            [source],
            IdFactory()));
    }

    [Fact]
    public void Build_FirstSegmentPreservesSourceFilenameAndDatabaseId()
    {
        var source = new ArchiveFileName(34, 7);
        Guid sourceId = Id(60);

        IReadOnlyList<PendingArchiveSplitSegment> plan = _planner.Build(
            source,
            sourceId,
            Range(2026, 7, 1, 2026, 7, 31),
            [
                Range(2026, 7, 1, 2026, 7, 12),
                Range(2026, 7, 13, 2026, 7, 31),
            ],
            [new ArchiveFileName(34, ArchiveFileName.NoSplit), source],
            IdFactory(Id(61)));

        Assert.Equal(source, plan[0].FileName);
        Assert.Equal(sourceId, plan[0].DatabaseId);
        Assert.Equal(new ArchiveFileName(34, 8), plan[1].FileName);
    }

    [Fact]
    public void Build_AllocatesAfterMaximumOccupiedSiblingSequence()
    {
        var source = new ArchiveFileName(35, ArchiveFileName.NoSplit);

        IReadOnlyList<PendingArchiveSplitSegment> plan = _planner.Build(
            source,
            Id(70),
            Range(2026, 8, 1, 2026, 8, 31),
            [
                Range(2026, 8, 1, 2026, 8, 10),
                Range(2026, 8, 11, 2026, 8, 20),
                Range(2026, 8, 21, 2026, 8, 31),
            ],
            [source, new ArchiveFileName(35, 2), new ArchiveFileName(35, 7)],
            IdFactory(Id(71), Id(72)));

        Assert.Equal(new ArchiveFileName(35, 8), plan[1].FileName);
        Assert.Equal(new ArchiveFileName(35, 9), plan[2].FileName);
    }

    [Fact]
    public void Build_RejectsDuplicateOccupiedFilenames()
    {
        var source = new ArchiveFileName(36, ArchiveFileName.NoSplit);

        Assert.Throws<ArgumentException>(() => _planner.Build(
            source,
            Id(80),
            Range(2026, 9, 1, 2026, 9, 30),
            [
                Range(2026, 9, 1, 2026, 9, 15),
                Range(2026, 9, 16, 2026, 9, 30),
            ],
            [source, source],
            IdFactory(Id(81))));
    }

    [Fact]
    public void Build_RejectsDuplicateOrEmptyGeneratedDatabaseIds()
    {
        var source = new ArchiveFileName(37, ArchiveFileName.NoSplit);
        Guid sourceId = Id(90);
        JournalDateRange coverage = Range(2026, 10, 1, 2026, 10, 31);
        JournalDateRange[] ranges =
        [
            Range(2026, 10, 1, 2026, 10, 10),
            Range(2026, 10, 11, 2026, 10, 20),
            Range(2026, 10, 21, 2026, 10, 31),
        ];

        Assert.Throws<InvalidOperationException>(() => _planner.Build(
            source,
            sourceId,
            coverage,
            ranges,
            [source],
            IdFactory(sourceId, Id(91))));
        Assert.Throws<InvalidOperationException>(() => _planner.Build(
            source,
            sourceId,
            coverage,
            ranges,
            [source],
            IdFactory(Guid.Empty, Id(91))));
        Assert.Throws<InvalidOperationException>(() => _planner.Build(
            source,
            sourceId,
            coverage,
            ranges,
            [source],
            IdFactory(Id(91), Id(91))));
    }

    [Fact]
    public void Build_AllowsCanonicalUpperBoundsAndRejectsSuffixOverflow()
    {
        var source = new ArchiveFileName(999_999, ArchiveFileName.NoSplit);
        var lastOccupiedSibling = new ArchiveFileName(999_999, 9_998);

        IReadOnlyList<PendingArchiveSplitSegment> plan = _planner.Build(
            source,
            Id(100),
            Range(2026, 11, 1, 2026, 11, 30),
            [
                Range(2026, 11, 1, 2026, 11, 15),
                Range(2026, 11, 16, 2026, 11, 30),
            ],
            [source, lastOccupiedSibling],
            IdFactory(Id(101)));

        Assert.Equal("archive_999999_9999.db", plan[1].FileName.FileName);

        Assert.Throws<ArgumentOutOfRangeException>(() => _planner.Build(
            source,
            Id(100),
            Range(2026, 11, 1, 2026, 11, 30),
            [
                Range(2026, 11, 1, 2026, 11, 10),
                Range(2026, 11, 11, 2026, 11, 20),
                Range(2026, 11, 21, 2026, 11, 30),
            ],
            [source, lastOccupiedSibling],
            IdFactory(Id(101), Id(102))));
    }

    [Fact]
    public void Build_RejectsNoncanonicalBaseOrOccupiedSuffixBounds()
    {
        JournalDateRange coverage = Range(2026, 12, 1, 2026, 12, 31);
        JournalDateRange[] ranges =
        [
            Range(2026, 12, 1, 2026, 12, 15),
            Range(2026, 12, 16, 2026, 12, 31),
        ];

        Assert.Throws<ArgumentException>(() => _planner.Build(
            new ArchiveFileName(1_000_000, ArchiveFileName.NoSplit),
            Id(110),
            coverage,
            ranges,
            [],
            IdFactory(Id(111))));

        var source = new ArchiveFileName(999_999, ArchiveFileName.NoSplit);
        Assert.Throws<ArgumentException>(() => _planner.Build(
            source,
            Id(110),
            coverage,
            ranges,
            [source, new ArchiveFileName(999_999, 10_000)],
            IdFactory(Id(111))));
    }

    [Fact]
    public void Build_SuffixedSourceNeverProducesNestedFilename()
    {
        var source = new ArchiveFileName(42, 2);

        IReadOnlyList<PendingArchiveSplitSegment> plan = _planner.Build(
            source,
            Id(120),
            Range(2027, 1, 1, 2027, 1, 31),
            [
                Range(2027, 1, 1, 2027, 1, 15),
                Range(2027, 1, 16, 2027, 1, 31),
            ],
            [new ArchiveFileName(42, ArchiveFileName.NoSplit), new ArchiveFileName(42, 1), source],
            IdFactory(Id(121)));

        Assert.Equal("archive_000042_0003.db", plan[1].FileName.FileName);
        Assert.DoesNotContain("_0002_", plan[1].FileName.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ReturnsReadOnlyPlan()
    {
        var source = new ArchiveFileName(43, ArchiveFileName.NoSplit);
        IReadOnlyList<PendingArchiveSplitSegment> plan = _planner.Build(
            source,
            Id(130),
            Range(2027, 2, 1, 2027, 2, 28),
            [
                Range(2027, 2, 1, 2027, 2, 14),
                Range(2027, 2, 15, 2027, 2, 28),
            ],
            [source],
            IdFactory(Id(131)));

        var mutableView = Assert.IsAssignableFrom<IList<PendingArchiveSplitSegment>>(plan);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = mutableView[0]);
    }

    private static Func<Guid> IdFactory(params Guid[] ids)
    {
        int index = 0;
        return () => index < ids.Length
            ? ids[index++]
            : throw new InvalidOperationException("Test DatabaseId factory exhausted.");
    }

    private static Guid Id(int value) =>
        new(value, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    private static JournalDateRange Range(
        int startYear,
        int startMonth,
        int startDay,
        int endYear,
        int endMonth,
        int endDay) =>
        new(
            new DateOnly(startYear, startMonth, startDay),
            new DateOnly(endYear, endMonth, endDay));
}
