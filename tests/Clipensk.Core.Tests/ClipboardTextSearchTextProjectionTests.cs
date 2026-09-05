using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardTextSearchTextProjectionTests
{
    [Fact]
    public async Task ExecuteAsync_ProjectsSearchTextAfterCanonicalLimitAcceptance()
    {
        const string RawValue = "raw-html";
        var extractor = new StubSearchTextExtractor("HTML Format", "visible text");
        var stage = CreateStage(
            new StubTextReader("HTML Format", RawValue),
            extractor);
        long canonicalBytes = ClipboardCanonicalPayloadSize.MeasureUtf8Text(RawValue);
        ClipboardContentReadPlan plan = CreateTextPlan("HTML Format", canonicalBytes);

        ClipboardContentReadExecution result = await stage.ExecuteAsync(plan);

        ClipboardCapturedTextContent captured = Assert.IsType<ClipboardCapturedTextContent>(
            Assert.Single(result.CapturedContent));
        Assert.Equal(RawValue, captured.Value);
        Assert.Equal(canonicalBytes, captured.CanonicalByteCount);
        Assert.Equal("visible text", captured.SearchText);
        Assert.Equal(1, extractor.ExtractCount);
        Assert.Empty(result.SizeRejectedFormats);
    }

    [Fact]
    public async Task ExecuteAsync_RetainsRawTextWhenSearchProjectionIsUnavailable()
    {
        const string RawValue = "malformed-cf-html";
        var extractor = new StubSearchTextExtractor("HTML Format", searchText: null);
        var stage = CreateStage(
            new StubTextReader("HTML Format", RawValue),
            extractor);
        ClipboardContentReadPlan plan = CreateTextPlan("HTML Format", maxBytes: null);

        ClipboardContentReadExecution result = await stage.ExecuteAsync(plan);

        ClipboardCapturedTextContent captured = Assert.IsType<ClipboardCapturedTextContent>(
            Assert.Single(result.CapturedContent));
        Assert.Equal(RawValue, captured.Value);
        Assert.Null(captured.SearchText);
        Assert.Equal(1, extractor.ExtractCount);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotProjectSearchTextForOversizedRawRepresentation()
    {
        const string RawValue = "ЖЖ";
        var extractor = new StubSearchTextExtractor("HTML Format", "should not run");
        var stage = CreateStage(
            new StubTextReader("HTML Format", RawValue),
            extractor);
        ClipboardContentReadPlan plan = CreateTextPlan("HTML Format", maxBytes: 3);

        ClipboardContentReadExecution result = await stage.ExecuteAsync(plan);

        Assert.Empty(result.CapturedContent);
        Assert.Single(result.SizeRejectedFormats);
        Assert.Equal(0, extractor.ExtractCount);
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
            textSearchTextExtractor: extractor);
    }

    private static ClipboardContentReadPlan CreateTextPlan(string formatName, long? maxBytes)
    {
        var selected = new ClipboardSelectedFormat(formatName, maxBytes);
        var route = new ClipboardContentReaderRoute(selected, ClipboardContentReaderKind.Text);
        var request = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.FromHours(3)),
                "Test/Zone"));
        var captureContext = new ClipboardCaptureContext(request, SourceApplication: null);
        var policyContext = new ClipboardCapturePolicyContext(
            captureContext,
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        var snapshot = new ClipboardFormatSnapshot(
            policyContext,
            new StubContentSnapshot([formatName]));
        var selection = new ClipboardFormatSelection(snapshot, [selected]);
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

    private sealed class StubSearchTextExtractor : IClipboardTextSearchTextExtractor
    {
        private readonly string _formatName;
        private readonly string? _searchText;

        public StubSearchTextExtractor(string formatName, string? searchText)
        {
            _formatName = formatName;
            _searchText = searchText;
        }

        public int ExtractCount { get; private set; }

        public ValueTask<string?> TryExtractAsync(
            string formatName,
            string value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_formatName, formatName);
            ExtractCount++;
            return ValueTask.FromResult(_searchText);
        }
    }

    private sealed class StubTextReader : IClipboardTextContentReader
    {
        private readonly string _formatName;
        private readonly string _value;

        public StubTextReader(string formatName, string value)
        {
            _formatName = formatName;
            _value = value;
        }

        public bool SupportsFormat(string formatName) =>
            string.Equals(formatName, _formatName, StringComparison.Ordinal);

        public ValueTask<string> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_value);
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
