using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardContentReadPlanStageTests
{
    [Fact]
    public void Create_SeparatesRoutableAndUnsupportedFormatsWithoutReadingPayload()
    {
        var selection = new ClipboardFormatSelection(
            CreateSnapshot(),
            new[]
            {
                new ClipboardSelectedFormat("Text", 1024),
                new ClipboardSelectedFormat("Custom.Binary", 2048),
                new ClipboardSelectedFormat("StorageItems", null),
            });
        var stage = new ClipboardContentReadPlanStage(
            new ClipboardContentReaderRouter(
                new StubTextReader("Text"),
                new StubPngImageReader(),
                new StubLinkReader(),
                new StubStorageItemsReader("StorageItems")));

        ClipboardContentReadPlan result = stage.Create(selection);

        Assert.Same(selection, result.Selection);
        Assert.Collection(
            result.Routes,
            route =>
            {
                Assert.Equal(ClipboardContentReaderKind.Text, route.ReaderKind);
                Assert.Equal(selection.Formats[0], route.SelectedFormat);
            },
            route =>
            {
                Assert.Equal(ClipboardContentReaderKind.StorageItems, route.ReaderKind);
                Assert.Equal(selection.Formats[2], route.SelectedFormat);
            });
        Assert.Single(result.UnsupportedFormats);
        Assert.Equal(selection.Formats[1], result.UnsupportedFormats[0]);
    }

    private static ClipboardFormatSnapshot CreateSnapshot()
    {
        var request = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 30, 0, TimeSpan.FromHours(3)),
                "Test/Zone"));
        var captureContext = new ClipboardCaptureContext(request, SourceApplication: null);
        var policyContext = new ClipboardCapturePolicyContext(
            captureContext,
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));

        return new ClipboardFormatSnapshot(policyContext, contentSnapshot: null);
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
            string formatName) => throw new InvalidOperationException("Read must not be called while creating a plan.");
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
            string formatName) => throw new InvalidOperationException("Read must not be called while creating a plan.");
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
            string formatName) => throw new InvalidOperationException("Read must not be called while creating a plan.");
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
            string formatName) => throw new InvalidOperationException("Read must not be called while creating a plan.");
    }
}
