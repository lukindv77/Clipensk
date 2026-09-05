using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardContentReaderRouterTests
{
    [Theory]
    [InlineData("Text", ClipboardContentReaderKind.Text)]
    [InlineData("Bitmap", ClipboardContentReaderKind.PngImage)]
    [InlineData("WebLink", ClipboardContentReaderKind.Link)]
    [InlineData("StorageItems", ClipboardContentReaderKind.StorageItems)]
    public void TryRoute_RoutesToExactlyOneSupportingReader(
        string formatName,
        ClipboardContentReaderKind expectedKind)
    {
        ClipboardContentReaderRouter router = CreateRouter(formatName, expectedKind);
        var selectedFormat = new ClipboardSelectedFormat(formatName, 4096);

        ClipboardContentReaderRoute? route = router.TryRoute(selectedFormat);

        Assert.True(route.HasValue);
        Assert.Equal(expectedKind, route.Value.ReaderKind);
        Assert.Equal(selectedFormat, route.Value.SelectedFormat);
    }

    [Fact]
    public void TryRoute_UnsupportedFormatReturnsNull()
    {
        ClipboardContentReaderRouter router = CreateRouter();

        ClipboardContentReaderRoute? route = router.TryRoute(
            new ClipboardSelectedFormat("Custom.Unknown", 8192));

        Assert.Null(route);
    }

    [Fact]
    public void TryRoute_AmbiguousReaderSupportFailsClosed()
    {
        const string FormatName = "Ambiguous";
        var router = new ClipboardContentReaderRouter(
            new StubTextReader(FormatName),
            new StubPngImageReader(),
            new StubLinkReader(FormatName),
            new StubStorageItemsReader());

        Assert.Throws<InvalidOperationException>(() =>
            router.TryRoute(new ClipboardSelectedFormat(FormatName, null)));
    }

    private static ClipboardContentReaderRouter CreateRouter(
        string? formatName = null,
        ClipboardContentReaderKind? readerKind = null)
    {
        string? textFormat = readerKind == ClipboardContentReaderKind.Text ? formatName : null;
        string? imageFormat = readerKind == ClipboardContentReaderKind.PngImage ? formatName : null;
        string? linkFormat = readerKind == ClipboardContentReaderKind.Link ? formatName : null;
        string? storageFormat = readerKind == ClipboardContentReaderKind.StorageItems ? formatName : null;

        return new ClipboardContentReaderRouter(
            new StubTextReader(textFormat),
            new StubPngImageReader(imageFormat),
            new StubLinkReader(linkFormat),
            new StubStorageItemsReader(storageFormat));
    }

    private abstract class StubReaderBase
    {
        private readonly string? _supportedFormat;

        protected StubReaderBase(string? supportedFormat)
        {
            _supportedFormat = supportedFormat;
        }

        protected bool Supports(string formatName)
        {
            return string.Equals(formatName, _supportedFormat, StringComparison.Ordinal);
        }
    }

    private sealed class StubTextReader : StubReaderBase, IClipboardTextContentReader
    {
        public StubTextReader(string? supportedFormat = null)
            : base(supportedFormat)
        {
        }

        public bool SupportsFormat(string formatName) => Supports(formatName);

        public ValueTask<string> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName) => throw new NotSupportedException();
    }

    private sealed class StubPngImageReader : StubReaderBase, IClipboardPngImageContentReader
    {
        public StubPngImageReader(string? supportedFormat = null)
            : base(supportedFormat)
        {
        }

        public bool SupportsFormat(string formatName) => Supports(formatName);

        public ValueTask<byte[]> ReadNormalizedPngAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName) => throw new NotSupportedException();
    }

    private sealed class StubLinkReader : StubReaderBase, IClipboardLinkContentReader
    {
        public StubLinkReader(string? supportedFormat = null)
            : base(supportedFormat)
        {
        }

        public bool SupportsFormat(string formatName) => Supports(formatName);

        public ValueTask<Uri> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName) => throw new NotSupportedException();
    }

    private sealed class StubStorageItemsReader : StubReaderBase, IClipboardStorageItemsContentReader
    {
        public StubStorageItemsReader(string? supportedFormat = null)
            : base(supportedFormat)
        {
        }

        public bool SupportsFormat(string formatName) => Supports(formatName);

        public ValueTask<IReadOnlyList<ClipboardStorageItemMetadata>> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName) => throw new NotSupportedException();
    }
}
