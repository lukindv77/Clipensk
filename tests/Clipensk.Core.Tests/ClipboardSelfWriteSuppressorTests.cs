using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardSelfWriteSuppressorTests
{
    [Fact]
    public void ShouldCapture_CapturesEveryUpdateWhenNothingIsSuppressed()
    {
        var suppressor = new ClipboardSelfWriteSuppressor();

        Assert.True(suppressor.ShouldCapture(1));
        Assert.True(suppressor.ShouldCapture(2));
    }

    [Fact]
    public void ShouldCapture_SkipsTheMatchingSelfWriteExactlyOnce()
    {
        var suppressor = new ClipboardSelfWriteSuppressor();
        suppressor.SuppressSequenceNumber(42);

        Assert.False(suppressor.ShouldCapture(42));
        // A later copy that happens to reach the same number is a real user action.
        Assert.True(suppressor.ShouldCapture(42));
    }

    [Fact]
    public void ShouldCapture_CapturesAndDisarmsWhenAnotherApplicationWinsTheRace()
    {
        var suppressor = new ClipboardSelfWriteSuppressor();
        suppressor.SuppressSequenceNumber(42);

        // Another application changed the clipboard first: this update is real.
        Assert.True(suppressor.ShouldCapture(41));
        // The stale suppression must not swallow the next unrelated copy either.
        Assert.True(suppressor.ShouldCapture(42));
    }

    [Fact]
    public void SuppressSequenceNumber_ReplacesAnEarlierArmedNumber()
    {
        var suppressor = new ClipboardSelfWriteSuppressor();
        suppressor.SuppressSequenceNumber(7);
        suppressor.SuppressSequenceNumber(8);

        Assert.True(suppressor.ShouldCapture(7));
    }

    [Fact]
    public void Reset_DropsAnArmedSuppression()
    {
        var suppressor = new ClipboardSelfWriteSuppressor();
        suppressor.SuppressSequenceNumber(42);

        suppressor.Reset();

        Assert.True(suppressor.ShouldCapture(42));
    }
}
