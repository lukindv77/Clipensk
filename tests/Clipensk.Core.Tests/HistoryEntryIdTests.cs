using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class HistoryEntryIdTests
{
    [Fact]
    public void Constructor_RejectsEmptyGuid()
    {
        Assert.Throws<ArgumentException>(() => new HistoryEntryId(Guid.Empty));
    }

    [Fact]
    public void New_CreatesDistinctNonEmptyDurableIds()
    {
        HistoryEntryId first = HistoryEntryId.New();
        HistoryEntryId second = HistoryEntryId.New();

        Assert.NotEqual(Guid.Empty, first.Value);
        Assert.NotEqual(Guid.Empty, second.Value);
        Assert.NotEqual(first, second);
        Assert.Equal(first.Value.ToString("D"), first.ToString());
    }
}
