using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardContentReadExecutionStageTests
{
    [Fact]
    public async Task ExecuteAsync_ReadsCanonicalPayloadsAndPreservesUnsupportedFormats()
    {
        var textReader = new StubTextReader("Text", "AЖ😀");
        var pngReader = new StubPngImageReader("Bitmap", [0x89, 0x50, 0x4E, 0x47]);
        var linkReader = new StubLinkReader(
            "WebLink",
            new Uri("https://example.test/путь", UriKind.Absolute));
        var storageReader = new StubStorageItemsReader(
            "StorageItems",
            [new ClipboardStorageItemMetadata(
                "C:\\Temp\\a.txt",
                "a.txt",
                ".txt",
                IsDirectory: false,
                Order: 0,
                ClipboardPreferredFileOperation.Copy)]);
        var stage = new ClipboardContentReadExecutionStage(
            textReader,
            pngReader,
            linkReader,
            storageReader);
        ClipboardContentReadPlan plan = CreatePlan(
            new[]
            {
                new ClipboardContentReaderRoute(
                    new ClipboardSelectedFormat("Text", 7),
                    ClipboardContentReaderKind.Text),
                new ClipboardContentReaderRoute(
                    new ClipboardSelectedFormat("Bitmap", 4),
                    ClipboardContentReaderKind.PngImage),
                new ClipboardContentReaderRoute(
                    new ClipboardSelectedFormat("WebLink", 1024),
                    ClipboardContentReaderKind.Link),
                new ClipboardContentReaderRoute(
                    new ClipboardSelectedFormat("StorageItems", null),
                    ClipboardContentReaderKind.StorageItems),
            },
            unsupportedFormats: [new ClipboardSelectedFormat("Custom.Binary", null)]);

        ClipboardContentReadExecution result = await stage.ExecuteAsync(plan);

        Assert.Equal(4, result.CapturedContent.Count);
        Assert.IsType<ClipboardCapturedTextContent>(result.CapturedContent[0]);
        Assert.IsType<ClipboardCapturedPngImageContent>(result.CapturedContent[1]);
        Assert.IsType<ClipboardCapturedLinkContent>(result.CapturedContent[2]);
        Assert.IsType<ClipboardCapturedStorageItemsContent>(result.CapturedContent[3]);
        Assert.Empty(result.SizeRejectedFormats);
        Assert.Empty(result.DeferredFormats);
        Assert.Single(result.UnsupportedFormats);
        Assert.Equal("Custom.Binary", result.UnsupportedFormats[0].FormatName);
        Assert.Equal(1, textReader.ReadCount);
        Assert.Equal(1, pngReader.ReadCount);
        Assert.Equal(1, linkReader.ReadCount);
        Assert.Equal(1, storageReader.ReadCount);
    }

    [Fact]
    public async Task ExecuteAsync_DropsPayloadThatExceedsConfiguredCanonicalLimit()
    {
        var textReader = new StubTextReader("Text", "ЖЖ");
        var stage = new ClipboardContentReadExecutionStage(
            textReader,
            new StubPngImageReader(),
            new StubLinkReader(),
            new StubStorageItemsReader());
        ClipboardSelectedFormat selected = new("Text", 3);
        ClipboardContentReadPlan plan = CreatePlan(
            [new ClipboardContentReaderRoute(selected, ClipboardContentReaderKind.Text)]);

        ClipboardContentReadExecution result = await stage.ExecuteAsync(plan);

        Assert.Empty(result.CapturedContent);
        Assert.Single(result.SizeRejectedFormats);
        Assert.Equal(selected, result.SizeRejectedFormats[0]);
        Assert.Equal(1, textReader.ReadCount);
    }

    [Fact]
    public async Task ExecuteAsync_DefersStorageItemsWithConfiguredLimitWithoutReadingPayload()
    {
        var storageReader = new StubStorageItemsReader("StorageItems", []);
        var stage = new ClipboardContentReadExecutionStage(
            new StubTextReader(),
            new StubPngImageReader(),
            new StubLinkReader(),
            storageReader);
        ClipboardSelectedFormat selected = new("StorageItems", 1024);
        ClipboardContentReadPlan plan = CreatePlan(
            [new ClipboardContentReaderRoute(selected, ClipboardContentReaderKind.StorageItems)]);

        ClipboardContentReadExecution result = await stage.ExecuteAsync(plan);

        Assert.Empty(result.CapturedContent);
        Assert.Empty(result.SizeRejectedFormats);
        Assert.Single(result.DeferredFormats);
        Assert.Equal(selected, result.DeferredFormats[0]);
        Assert.Equal(0, storageReader.ReadCount);
    }

    [Fact]
    public async Task ExecuteAsync_RequiresRetainedSnapshotWhenRoutesExist()
    {
        var stage = new ClipboardContentReadExecutionStage(
            new StubTextReader("Text", "value"),
            new StubPngImageReader(),
            new StubLinkReader(),
            new StubStorageItemsReader());
        ClipboardContentReadPlan plan = CreatePlan(
            [new ClipboardContentReaderRoute(
                new ClipboardSelectedFormat("Text", null),
                ClipboardContentReaderKind.Text)],
            retainSnapshot: false);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await stage.ExecuteAsync(plan));
    }

    [Fact]
    public async Task ExecuteAsync_HonorsCancellationBeforeReadingNextRoute()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var textReader = new StubTextReader("Text", "value");
        var stage = new ClipboardContentReadExecutionStage(
            textReader,
            new StubPngImageReader(),
            new StubLinkReader(),
            new StubStorageItemsReader());
        ClipboardContentReadPlan plan = CreatePlan(
            [new ClipboardContentReaderRoute(
                new ClipboardSelectedFormat("Text", null),
                ClipboardContentReaderKind.Text)]);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await stage.ExecuteAsync(plan, cts.Token));
        Assert.Equal(0, textReader.ReadCount);
    }

    private static ClipboardContentReadPlan CreatePlan(
        IEnumerable<ClipboardContentReaderRoute> routes,
        IEnumerable<ClipboardSelectedFormat>? unsupportedFormats = null,
        bool retainSnapshot = true)
    {
        ClipboardContentReaderRoute[] routeArray = routes.ToArray();
        ClipboardSelectedFormat[] unsupportedArray = unsupportedFormats?.ToArray() ?? [];
        var request = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 30, 0, TimeSpan.FromHours(3)),
                "Test/Zone"));
        var captureContext = new ClipboardCaptureContext(request, SourceApplication: null);
        var policyContext = new ClipboardCapturePolicyContext(
            captureContext,
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        IClipboardContentSnapshot? contentSnapshot = retainSnapshot
            ? new StubContentSnapshot(routeArray.Select(route => route.SelectedFormat.FormatName))
            : null;
        var snapshot = new ClipboardFormatSnapshot(policyContext, contentSnapshot);
        var selection = new ClipboardFormatSelection(
            snapshot,
            routeArray.Select(route => route.SelectedFormat).Concat(unsupportedArray));

        return new ClipboardContentReadPlan(selection, routeArray, unsupportedArray);
    }

    private sealed class StubContentSnapshot : IClipboardContentSnapshot
    {
        public StubContentSnapshot(IEnumerable<string> formats)
        {
            AvailableFormats = Array.AsReadOnly(formats.ToArray());
        }

        public IReadOnlyList<string> AvailableFormats { get; }
    }

    private sealed class StubTextReader : IClipboardTextContentReader
    {
        private readonly string? _format;
        private readonly string _value;

        public StubTextReader(string? format = null, string value = "")
        {
            _format = format;
            _value = value;
        }

        public int ReadCount { get; private set; }

        public bool SupportsFormat(string formatName) =>
            string.Equals(formatName, _format, StringComparison.Ordinal);

        public ValueTask<string> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName)
        {
            ReadCount++;
            return ValueTask.FromResult(_value);
        }
    }

    private sealed class StubPngImageReader : IClipboardPngImageContentReader
    {
        private readonly string? _format;
        private readonly byte[] _value;

        public StubPngImageReader(string? format = null, byte[]? value = null)
        {
            _format = format;
            _value = value ?? [];
        }

        public int ReadCount { get; private set; }

        public bool SupportsFormat(string formatName) =>
            string.Equals(formatName, _format, StringComparison.Ordinal);

        public ValueTask<byte[]> ReadNormalizedPngAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName)
        {
            ReadCount++;
            return ValueTask.FromResult(_value);
        }
    }

    private sealed class StubLinkReader : IClipboardLinkContentReader
    {
        private readonly string? _format;
        private readonly Uri _value;

        public StubLinkReader(string? format = null, Uri? value = null)
        {
            _format = format;
            _value = value ?? new Uri("https://example.test/", UriKind.Absolute);
        }

        public int ReadCount { get; private set; }

        public bool SupportsFormat(string formatName) =>
            string.Equals(formatName, _format, StringComparison.Ordinal);

        public ValueTask<Uri> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName)
        {
            ReadCount++;
            return ValueTask.FromResult(_value);
        }
    }

    private sealed class StubStorageItemsReader : IClipboardStorageItemsContentReader
    {
        private readonly string? _format;
        private readonly IReadOnlyList<ClipboardStorageItemMetadata> _items;

        public StubStorageItemsReader(
            string? format = null,
            IReadOnlyList<ClipboardStorageItemMetadata>? items = null)
        {
            _format = format;
            _items = items ?? Array.Empty<ClipboardStorageItemMetadata>();
        }

        public int ReadCount { get; private set; }

        public bool SupportsFormat(string formatName) =>
            string.Equals(formatName, _format, StringComparison.Ordinal);

        public ValueTask<IReadOnlyList<ClipboardStorageItemMetadata>> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName)
        {
            ReadCount++;
            return ValueTask.FromResult(_items);
        }
    }
}
