using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardCanonicalPayloadSizeTests
{
    [Fact]
    public void MeasureUtf8Text_CountsCanonicalUtf8Bytes()
    {
        long result = ClipboardCanonicalPayloadSize.MeasureUtf8Text("AЖ😀");

        Assert.Equal(7, result);
    }

    [Fact]
    public void MeasureLink_UsesOriginalStringUtf8Representation()
    {
        var uri = new Uri("https://example.test/путь?q=1", UriKind.Absolute);

        long result = ClipboardCanonicalPayloadSize.MeasureLink(uri);

        Assert.Equal(
            ClipboardCanonicalPayloadSize.MeasureUtf8Text(uri.OriginalString),
            result);
    }

    [Fact]
    public void MeasureBinary_UsesExactStoredByteLength()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];

        Assert.Equal(4, ClipboardCanonicalPayloadSize.MeasureBinary(payload));
    }

    [Theory]
    [InlineData(10, null, true)]
    [InlineData(10, 10, true)]
    [InlineData(10, 11, true)]
    [InlineData(10, 9, false)]
    public void IsWithinLimit_IsInclusive(
        int canonicalByteCount,
        int? maxBytes,
        bool expected)
    {
        Assert.Equal(
            expected,
            ClipboardCanonicalPayloadSize.IsWithinLimit(canonicalByteCount, maxBytes));
    }

    [Fact]
    public void IsWithinLimit_RejectsNegativeCanonicalByteCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClipboardCanonicalPayloadSize.IsWithinLimit(-1, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IsWithinLimit_RejectsNonPositiveConfiguredLimit(int maxBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClipboardCanonicalPayloadSize.IsWithinLimit(0, maxBytes));
    }
}
