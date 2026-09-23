using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardHistoryFilterTests
{
    private static readonly Guid First = new("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = new("20000000-0000-0000-0000-000000000002");

    [Fact]
    public void Constructor_SortsSourcesAndKeepsTheExactFormatName()
    {
        var filter = new ClipboardHistoryFilter([Second, First], "HTML Format");

        Assert.Equal([First, Second], filter.SourceApplicationIds);
        Assert.Equal("HTML Format", filter.FormatName);
    }

    [Fact]
    public void NullMeansAnyAndAnEmptySourceSetIsKept()
    {
        var any = new ClipboardHistoryFilter();
        var none = new ClipboardHistoryFilter([]);

        Assert.Null(any.SourceApplicationIds);
        Assert.Null(any.FormatName);
        Assert.NotNull(none.SourceApplicationIds);
        Assert.Empty(none.SourceApplicationIds);
        Assert.NotEqual(any, none);
    }

    [Fact]
    public void Constructor_RejectsEmptyOrRepeatedSourcesAndBlankFormats()
    {
        Assert.Throws<ArgumentException>(() => new ClipboardHistoryFilter([Guid.Empty]));
        Assert.Throws<ArgumentException>(() => new ClipboardHistoryFilter([First, First]));
        Assert.Throws<ArgumentException>(() => new ClipboardHistoryFilter(formatName: ""));
        Assert.Throws<ArgumentException>(() => new ClipboardHistoryFilter(formatName: "  "));
    }

    [Fact]
    public void Equality_ComparesSourcesAsASetAndFormatsOrdinally()
    {
        var left = new ClipboardHistoryFilter([First, Second], "Text");
        var right = new ClipboardHistoryFilter([Second, First], "Text");

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, new ClipboardHistoryFilter([First, Second], "text"));
        Assert.NotEqual(left, new ClipboardHistoryFilter([First], "Text"));
        Assert.Equal(ClipboardHistoryFilter.ForSource(First), new ClipboardHistoryFilter([First]));
    }
}
