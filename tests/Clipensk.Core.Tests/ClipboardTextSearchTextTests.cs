using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardTextSearchTextTests
{
    [Fact]
    public async Task ExecuteAsync_CarriesDerivedSearchTextWithoutChangingCanonicalSize()
    {
        const string RawHtml = "<b>Ж</b>";
        var textReader = new StubTextReader("Html", RawHtml);
        var extractor = new StubSearchTextExtractor("Html", "Ж");
        var stage = CreateStage(textReader, extractor);
        long rawByteCount = ClipboardCanonicalPayloadSize.MeasureUtf8Text(RawHtml);
        ClipboardContentReadPlan plan = CreatePlan(
            new ClipboardSelectedFormat("Html", rawByteCount));

        ClipboardContentReadExecution result = await stage.ExecuteAsync(plan);

        ClipboardCapturedTextContent captured = Assert.IsType<ClipboardCapturedTextContent>(
            Assert.Single(result.CapturedContent));
        Assert.Equal(RawHtml, captured.Value);
        Assert.Equal("Ж", captured.SearchText);
        Assert.Equal(rawByteCount, captured.CanonicalByteCount);
        Assert.Equal(1, extractor.ExtractCount);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotExtractSearchTextWhenRawPayloadExceedsLimit()
    {
        const string RawHtml = "<b>Ж</b>";
        var textReader = new StubTextReader("Html", RawHtml);
        var extractor = new StubSearchTextExtractor("Html", "Ж");
        var stage = CreateStage(textReader, extractor);
        ClipboardSelectedFormat selected = new("Html", 1);
        ClipboardContentReadPlan plan = CreatePlan(selected);

        ClipboardContentReadExecution result = await stage.ExecuteAsync(plan);

        Assert.Empty(result.CapturedContent);
        Assert.Equal(selected, Assert.Single(result.SizeRejectedFormats));
        Assert.Equal(0, extractor.ExtractCount);
    }

    [Fact]
    public async Task ExecuteAsync_AllowsExtractorToLeaveUnsupportedTextFormatWithoutSearchProjection()
    {
        const string Rtf = "{\\rtf1 test}";
        var textReader = new StubTextReader("Rtf", Rtf);
        var extractor = new StubSearchTextExtractor("Html", "unused");
        var stage = CreateStage(textReader, extractor);
        ClipboardContentReadPlan plan = CreatePlan(
            new ClipboardSelectedFormat("Rtf", null));

        ClipboardContentReadExecution result = await stage.ExecuteAsync(plan);

        ClipboardCapturedTextContent captured = Assert.IsType<ClipboardCapturedTextContent>(
            Assert.Single(result.CapturedContent));
        Assert.Equal(Rtf, captured.Value);
        Assert.Null(captured.SearchText);
        Assert.Equal(1, extractor.ExtractCount);
    }

    private static ClipboardContentReadExecutionStage CreateStage(
        IClipboardTextContentReader textReader,
        IClipboardTextSearchTextExtractor extractor)
    {
        return new ClipboardContentReadExecutionStage(
            textReader,
            new StubPngImageReader(),
            new StubLinkReader(),
            new StubStorageItemsReader(),
            customBinaryReader: null,
            textSearchTextExtractor: extractor);
    }

    private static ClipboardContentReadPlan CreatePlan(ClipboardSelectedFormat selected)
    {
        var request = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 30, 0, TimeSpan.FromHours(3)),
                "Test/Zone"));
        var captureContext = new ClipboardCaptureContext(request, SourceApplication: null);
        var policyContext = new ClipboardCapturePolicyContext(
            captureContext,
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        var snapshot = new ClipboardFormatSnapshot(
            policyContext,
            new StubContentSnapshot([selected.FormatName]));
        var selection = new ClipboardFormatSelection(snapshot, [selected]);
        var route = new ClipboardContentReaderRoute(selected, ClipboardContentReaderKind.Text);
        return new ClipboardContentReadPlan(selection, [route], unsupportedFormats: []);
    }

    private sealed class StubContentSnapshot : IClipboardContentSnapshot
    {
        public StubContentSnapshot(IReadOnlyList<string> formats)
        {
            AvailableFormats = formats;
        }

        public IReadOnlyList<string> AvailableFormats { get; }
    }

    private sealed class StubTextReader : IClipboardTextContentReader
    {
        private readonly string _format;
        private readonly string _value;

        public StubTextReader(string format, string value)
        {
            _format = format;
            _value = value;
        }

        public bool SupportsFormat(string formatName) =>
            string.Equals(formatName, _format, StringComparison.Ordinal);

        public ValueTask<string> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_value);
        }
    }

    private sealed class StubSearchTextExtractor : IClipboardTextSearchTextExtractor
    {
        private readonly string _supportedFormat;
        private readonly string _searchText;

        public StubSearchTextExtractor(string supportedFormat, string searchText)
        {
            _supportedFormat = supportedFormat;
            _searchText = searchText;
        }

        public int ExtractCount { get; private set; }

        public ValueTask<string?> TryExtractAsync(
            string formatName,
            string value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractCount++;
            return ValueTask.FromResult<string?>(
                string.Equals(formatName, _supportedFormat, StringComparison.Ordinal)
                    ? _searchText
                    : null);
        }
    }

    private sealed class StubPngImageReader : IClipboardPngImageContentReader
    {
        public bool SupportsFormat(string formatName) => false;

        public ValueTask<byte[]> ReadNormalizedPngAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubLinkReader : IClipboardLinkContentReader
    {
        public bool SupportsFormat(string formatName) => false;

        public ValueTask<Uri> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubStorageItemsReader : IClipboardStorageItemsContentReader
    {
        public bool SupportsFormat(string formatName) => false;

        public ValueTask<IReadOnlyList<ClipboardStorageItemMetadata>> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
