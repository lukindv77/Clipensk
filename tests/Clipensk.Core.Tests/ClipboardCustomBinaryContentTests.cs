using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardCustomBinaryContentTests
{
    [Fact]
    public void Router_UsesCustomBinaryOnlyAsFallback()
    {
        var customReader = new StubCustomBinaryReader("Custom.Binary", [1, 2, 3]);
        var router = new ClipboardContentReaderRouter(
            new StubTextReader("Text"),
            new StubPngImageReader(),
            new StubLinkReader(),
            new StubStorageItemsReader(),
            customReader);

        ClipboardContentReaderRoute? textRoute = router.TryRoute(
            new ClipboardSelectedFormat("Text", null));
        ClipboardContentReaderRoute? customRoute = router.TryRoute(
            new ClipboardSelectedFormat("Custom.Binary", null));

        Assert.True(textRoute.HasValue);
        Assert.Equal(ClipboardContentReaderKind.Text, textRoute.Value.ReaderKind);
        Assert.True(customRoute.HasValue);
        Assert.Equal(ClipboardContentReaderKind.CustomBinary, customRoute.Value.ReaderKind);
    }

    [Theory]
    [InlineData("RiffAudio")]
    [InlineData("WaveAudio")]
    [InlineData("FileContents")]
    public void Router_DoesNotRouteProhibitedFormatsToCustomBinary(string formatName)
    {
        var customReader = new StubCustomBinaryReader(formatName, [1, 2, 3]);
        var router = new ClipboardContentReaderRouter(
            new StubTextReader(),
            new StubPngImageReader(),
            new StubLinkReader(),
            new StubStorageItemsReader(),
            customReader);

        ClipboardContentReaderRoute? route = router.TryRoute(
            new ClipboardSelectedFormat(formatName, null));

        Assert.Null(route);
    }

    [Theory]
    [InlineData("RiffAudio")]
    [InlineData("WaveAudio")]
    [InlineData("FileContents")]
    public void Router_RejectsProhibitedFormatBeforeStandardReaderMatching(string formatName)
    {
        var router = new ClipboardContentReaderRouter(
            new StubTextReader(formatName),
            new StubPngImageReader(),
            new StubLinkReader(),
            new StubStorageItemsReader());

        ClipboardContentReaderRoute? route = router.TryRoute(
            new ClipboardSelectedFormat(formatName, null));

        Assert.Null(route);
    }

    [Fact]
    public async Task ExecuteAsync_CapturesExactCustomBinaryBytesWithinLimit()
    {
        byte[] sourceBytes = [0x00, 0x7F, 0x80, 0xFF];
        var customReader = new StubCustomBinaryReader("Custom.Binary", sourceBytes);
        var stage = CreateExecutionStage(customReader);
        ClipboardSelectedFormat selected = new("Custom.Binary", 4);
        ClipboardContentReadPlan plan = CreatePlan(
            new ClipboardContentReaderRoute(selected, ClipboardContentReaderKind.CustomBinary));

        ClipboardContentReadExecution result = await stage.ExecuteAsync(plan);

        ClipboardCapturedCustomBinaryContent captured = Assert.IsType<ClipboardCapturedCustomBinaryContent>(
            Assert.Single(result.CapturedContent));
        Assert.Equal(sourceBytes, captured.Bytes.ToArray());
        Assert.Equal(4L, captured.CanonicalByteCount);
        Assert.Empty(result.SizeRejectedFormats);
        Assert.Equal(1, customReader.ReadCount);
        Assert.Equal(4L, customReader.ObservedMaxBytes);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsCustomBinaryPayloadDuringSizePreflight()
    {
        var customReader = new StubCustomBinaryReader("Custom.Binary", [1, 2, 3]);
        var stage = CreateExecutionStage(customReader);
        ClipboardSelectedFormat selected = new("Custom.Binary", 2);
        ClipboardContentReadPlan plan = CreatePlan(
            new ClipboardContentReaderRoute(selected, ClipboardContentReaderKind.CustomBinary));

        ClipboardContentReadExecution result = await stage.ExecuteAsync(plan);

        Assert.Empty(result.CapturedContent);
        Assert.Equal(selected, Assert.Single(result.SizeRejectedFormats));
        Assert.Equal(1, customReader.ReadCount);
        Assert.Equal(2L, customReader.ObservedMaxBytes);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsOversizedBytesEvenWhenReaderIgnoresPreflight()
    {
        var customReader = new StubCustomBinaryReader(
            "Custom.Binary",
            [1, 2, 3],
            honorLimit: false);
        var stage = CreateExecutionStage(customReader);
        ClipboardSelectedFormat selected = new("Custom.Binary", 2);
        ClipboardContentReadPlan plan = CreatePlan(
            new ClipboardContentReaderRoute(selected, ClipboardContentReaderKind.CustomBinary));

        ClipboardContentReadExecution result = await stage.ExecuteAsync(plan);

        Assert.Empty(result.CapturedContent);
        Assert.Equal(selected, Assert.Single(result.SizeRejectedFormats));
        Assert.Equal(1, customReader.ReadCount);
        Assert.Equal(2L, customReader.ObservedMaxBytes);
    }

    [Fact]
    public void CapturedCustomBinaryContent_OwnsItsByteSnapshot()
    {
        byte[] sourceBytes = [1, 2, 3];
        ClipboardContentReaderRoute route = new(
            new ClipboardSelectedFormat("Custom.Binary", null),
            ClipboardContentReaderKind.CustomBinary);
        var captured = new ClipboardCapturedCustomBinaryContent(route, sourceBytes);

        sourceBytes[0] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, captured.Bytes.ToArray());
    }

    private static ClipboardContentReadExecutionStage CreateExecutionStage(
        IClipboardCustomBinaryContentReader customReader)
    {
        return new ClipboardContentReadExecutionStage(
            new StubTextReader(),
            new StubPngImageReader(),
            new StubLinkReader(),
            new StubStorageItemsReader(),
            customReader);
    }

    private static ClipboardContentReadPlan CreatePlan(ClipboardContentReaderRoute route)
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
            new StubContentSnapshot([route.SelectedFormat.FormatName]));
        var selection = new ClipboardFormatSelection(snapshot, [route.SelectedFormat]);
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

    private sealed class StubCustomBinaryReader : IClipboardCustomBinaryContentReader
    {
        private readonly string _format;
        private readonly byte[] _bytes;
        private readonly bool _honorLimit;

        public StubCustomBinaryReader(
            string format,
            byte[] bytes,
            bool honorLimit = true)
        {
            _format = format;
            _bytes = bytes;
            _honorLimit = honorLimit;
        }

        public int ReadCount { get; private set; }

        public long? ObservedMaxBytes { get; private set; }

        public bool SupportsFormat(string formatName) =>
            string.Equals(formatName, _format, StringComparison.Ordinal);

        public ValueTask<byte[]?> ReadWithinLimitAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            long? maxBytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            ObservedMaxBytes = maxBytes;
            if (_honorLimit && maxBytes.HasValue && _bytes.LongLength > maxBytes.Value)
            {
                return ValueTask.FromResult<byte[]?>(null);
            }

            return ValueTask.FromResult<byte[]?>(_bytes);
        }
    }

    private sealed class StubTextReader : IClipboardTextContentReader
    {
        private readonly string? _format;

        public StubTextReader(string? format = null)
        {
            _format = format;
        }

        public bool SupportsFormat(string formatName) =>
            string.Equals(formatName, _format, StringComparison.Ordinal);

        public ValueTask<string> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
