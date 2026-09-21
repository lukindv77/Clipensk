using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardFormatSizeLimitTests
{
    [Theory]
    [InlineData("1", 1024L)]
    [InlineData(" 5 ", 5L * 1024)]
    [InlineData("2048", 2048L * 1024)]
    public void ParseKilobytesAsBytes_ConvertsWholeKilobytesToBytes(string text, long expectedBytes)
    {
        Assert.Equal(expectedBytes, ClipboardFormatSizeLimit.ParseKilobytesAsBytes(text, "parameter"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1,024")]
    [InlineData("1e3")]
    public void ParseKilobytesAsBytes_RejectsAnythingThatIsNotAPositiveWholeNumber(string? text)
    {
        Assert.Throws<ArgumentException>(() => ClipboardFormatSizeLimit.ParseKilobytesAsBytes(text, "parameter"));
    }

    [Fact]
    public void ParseKilobytesAsBytes_RejectsAKilobyteCountThatWouldOverflowWhenConvertedToBytes()
    {
        // long.MaxValue kilobytes cannot be expressed as bytes in a long, even though it parses fine.
        Assert.Throws<ArgumentException>(() =>
            ClipboardFormatSizeLimit.ParseKilobytesAsBytes(long.MaxValue.ToString(), "parameter"));
    }

    [Fact]
    public void ParseKilobytesAsBytes_AcceptsTheLargestKilobyteCountThatFitsInBytes()
    {
        long maxKilobytes = long.MaxValue / ClipboardFormatSizeLimit.BytesPerKilobyte;

        long bytes = ClipboardFormatSizeLimit.ParseKilobytesAsBytes(maxKilobytes.ToString(), "parameter");

        Assert.Equal(maxKilobytes * ClipboardFormatSizeLimit.BytesPerKilobyte, bytes);
    }

    [Theory]
    [InlineData(1024L, 1L)]
    [InlineData(1L, 1L)]
    [InlineData(1023L, 1L)]
    [InlineData(1025L, 2L)]
    [InlineData(2048L, 2L)]
    public void BytesToKilobytesRoundedUp_RoundsUpSoReSavingNeverTightensAnExistingLimit(
        long bytes,
        long expectedKilobytes)
    {
        Assert.Equal(expectedKilobytes, ClipboardFormatSizeLimit.BytesToKilobytesRoundedUp(bytes));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void BytesToKilobytesRoundedUp_RejectsNonPositiveByteCounts(long bytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardFormatSizeLimit.BytesToKilobytesRoundedUp(bytes));
    }

    [Fact]
    public void RoundTrip_DisplayingAndReSavingAStoredLimitNeverTightensIt()
    {
        foreach (long storedBytes in new[]
                 {
                     DefaultClipboardFormatCaptureLimits.PlainTextMaxBytes,
                     DefaultClipboardFormatCaptureLimits.HtmlMaxBytes,
                     DefaultClipboardFormatCaptureLimits.RtfMaxBytes,
                     DefaultClipboardFormatCaptureLimits.ImageMaxBytes,
                     DefaultClipboardFormatCaptureLimits.StorageItemsMaxBytes,
                 })
        {
            long displayedKilobytes = ClipboardFormatSizeLimit.BytesToKilobytesRoundedUp(storedBytes);
            long reSavedBytes = ClipboardFormatSizeLimit.ParseKilobytesAsBytes(
                displayedKilobytes.ToString(),
                "parameter");

            Assert.True(reSavedBytes >= storedBytes);
        }
    }
}
