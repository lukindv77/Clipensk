using Clipensk.Core.Settings;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class DataRootRelocationMarkerTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clipensk-marker"));
    private static readonly string Source = Path.Combine(Root, "Source");
    private static readonly string Target = Path.Combine(Root, "Target");
    private static readonly DateTimeOffset Started = new(2026, 9, 23, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Paths_AreNormalizedAndComparedWithoutCase()
    {
        Assert.Equal(Source, DataRootPaths.Normalize(Source + Path.DirectorySeparatorChar));
        Assert.True(DataRootPaths.AreSame(Source, Source.ToUpperInvariant()));
        Assert.True(DataRootPaths.IsNested(Root, Source));
        Assert.True(DataRootPaths.IsNested(Root.ToUpperInvariant(), Source));
        Assert.False(DataRootPaths.IsNested(Source, Root));
        Assert.False(DataRootPaths.IsNested(Source, Source));

        // A sibling sharing a name prefix is not inside.
        Assert.False(DataRootPaths.IsNested(Source, Source + "Other"));
    }

    [Fact]
    public void ValidMarker_Passes()
    {
        Marker().Validate();
        (Marker() with { Phase = DataRootRelocationPhase.Switched }).Validate();
    }

    public static TheoryData<DataRootRelocationMarker> InvalidMarkers() => new()
    {
        Marker() with { OperationId = Guid.Empty },
        Marker() with { Phase = (DataRootRelocationPhase)7 },
        Marker() with { StartedAtUtc = new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.FromHours(3)) },
        Marker() with { SourcePath = "relative" },
        Marker() with { TargetPath = "" },
        Marker() with { TargetPath = Target + Path.DirectorySeparatorChar },
        Marker() with { TargetPath = Source.ToUpperInvariant() },
        Marker() with { TargetPath = Path.Combine(Source, "Inner") },
        Marker() with { TargetPath = Root },
    };

    [Theory]
    [MemberData(nameof(InvalidMarkers))]
    public void InvalidMarker_FailsClosed(DataRootRelocationMarker marker)
    {
        Assert.Throws<InvalidDataException>(marker.Validate);
    }

    private static DataRootRelocationMarker Marker() =>
        new(Guid.NewGuid(), Source, Target, TargetExisted: false, DataRootRelocationPhase.Copying, Started);
}
