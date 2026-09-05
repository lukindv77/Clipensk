using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardContentReadExecutionCancellationTests
{
    [Fact]
    public async Task ExecuteAsync_PassesCancellationTokenIntoReaderAndDoesNotPublishCancelledPayload()
    {
        using var cancellation = new CancellationTokenSource();
        var textReader = new CancellingTextReader(cancellation);
        var stage = new ClipboardContentReadExecutionStage(
            textReader,
            new UnsupportedPngReader(),
            new UnsupportedLinkReader(),
            new UnsupportedStorageItemsReader());
        ClipboardContentReadPlan plan = CreateTextPlan();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            stage.ExecuteAsync(plan, cancellation.Token).AsTask());

        Assert.Equal(1, textReader.ReadCount);
        Assert.Equal(cancellation.Token, textReader.ReceivedToken);
    }

    private static ClipboardContentReadPlan CreateTextPlan()
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
            new StubContentSnapshot(["Text"]));
        var selected = new ClipboardSelectedFormat("Text", null);
        var selection = new ClipboardFormatSelection(snapshot, [selected]);
        var route = new ClipboardContentReaderRoute(selected, ClipboardContentReaderKind.Text);

        return new ClipboardContentReadPlan(selection, [route], []);
    }

    private sealed class StubContentSnapshot : IClipboardContentSnapshot
    {
        public StubContentSnapshot(IReadOnlyList<string> availableFormats)
        {
            AvailableFormats = availableFormats;
        }

        public IReadOnlyList<string> AvailableFormats { get; }
    }

    private sealed class CancellingTextReader : IClipboardTextContentReader
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingTextReader(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public int ReadCount { get; private set; }

        public CancellationToken ReceivedToken { get; private set; }

        public bool SupportsFormat(string formatName) =>
            string.Equals(formatName, "Text", StringComparison.Ordinal);

        public ValueTask<string> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            ReceivedToken = cancellationToken;
            _cancellation.Cancel();
            return ValueTask.FromResult("must-not-be-published");
        }
    }

    private sealed class UnsupportedPngReader : IClipboardPngImageContentReader
    {
        public bool SupportsFormat(string formatName) => false;

        public ValueTask<byte[]> ReadNormalizedPngAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnsupportedLinkReader : IClipboardLinkContentReader
    {
        public bool SupportsFormat(string formatName) => false;

        public ValueTask<Uri> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnsupportedStorageItemsReader : IClipboardStorageItemsContentReader
    {
        public bool SupportsFormat(string formatName) => false;

        public ValueTask<IReadOnlyList<ClipboardStorageItemMetadata>> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
