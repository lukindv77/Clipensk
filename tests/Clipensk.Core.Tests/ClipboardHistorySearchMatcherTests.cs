using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardHistorySearchMatcherTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_TreatsBlankAsNoSearch(string? rawTerm)
    {
        Assert.Null(ClipboardHistorySearchMatcher.Normalize(rawTerm));
    }

    [Fact]
    public void Normalize_TrimsSurroundingWhitespace()
    {
        Assert.Equal("term", ClipboardHistorySearchMatcher.Normalize("  term  "));
    }

    [Fact]
    public void Matches_FindsAnAsciiSubstringRegardlessOfCase()
    {
        Assert.True(ClipboardHistorySearchMatcher.Matches("Copied Text", "text"));
        Assert.True(ClipboardHistorySearchMatcher.Matches("Copied Text", "TEXT"));
        Assert.False(ClipboardHistorySearchMatcher.Matches("Copied Text", "other"));
    }

    [Fact]
    public void Matches_FoldsCyrillicCase()
    {
        // The primary Clipensk UI is Russian; SQLite's built-in LIKE/LOWER only fold ASCII case,
        // so this is the behavior a search box actually needs.
        Assert.True(ClipboardHistorySearchMatcher.Matches("Привет, мир", "привет"));
        Assert.True(ClipboardHistorySearchMatcher.Matches("привет, мир", "ПРИВЕТ"));
        Assert.False(ClipboardHistorySearchMatcher.Matches("Привет, мир", "пока"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Matches_NeverMatchesAnAbsentSearchProjection(string? searchText)
    {
        // Absence of a projection means nothing was extracted, not "matches everything."
        Assert.False(ClipboardHistorySearchMatcher.Matches(searchText, "anything"));
    }

    [Fact]
    public void Matches_RejectsAnEmptyTerm()
    {
        Assert.Throws<ArgumentException>(() => ClipboardHistorySearchMatcher.Matches("text", ""));
    }
}
