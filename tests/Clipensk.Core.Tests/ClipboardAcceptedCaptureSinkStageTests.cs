using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardAcceptedCaptureSinkStageTests
{
    [Fact]
    public async Task StoreAsync_DeliversOnlyAcceptedPayloadToSink()
    {
        ClipboardCaptureContext captureContext = CreateCaptureContext();
        ClipboardSelectedFormat selected = new("Text", 128);
        var route = new ClipboardContentReaderRoute(
            selected,
            ClipboardContentReaderKind.Text);
        var accepted = new ClipboardCapturedTextContent(
            route,
            "accepted",
            canonicalByteCount: 8);
        ClipboardContentReadExecution execution = CreateExecution(
            captureContext,
            [route],
            [accepted],
            sizeRejectedFormats: [new ClipboardSelectedFormat("Html", 1)],
            deferredFormats: [new ClipboardSelectedFormat("StorageItems", 1024)]);
        var sink = new RecordingSink();
        var stage = new ClipboardAcceptedCaptureSinkStage(
            new ClipboardAcceptedCaptureStage(),
            sink);

        bool stored = await stage.StoreAsync(execution);

        Assert.True(stored);
        Assert.Single(sink.Stored);
        Assert.Equal(captureContext, sink.Stored[0].CaptureContext);
        Assert.Single(sink.Stored[0].Content);
        Assert.Same(accepted, sink.Stored[0].Content[0]);
    }

    [Fact]
    public async Task StoreAsync_DoesNotCallSinkWhenNothingWasAccepted()
    {
        ClipboardContentReadExecution execution = CreateExecution(
            CreateCaptureContext(),
            routes: [],
            capturedContent: [],
            sizeRejectedFormats: [new ClipboardSelectedFormat("Text", 1)],
            deferredFormats: [new ClipboardSelectedFormat("StorageItems", 1024)]);
        var sink = new RecordingSink();
        var stage = new ClipboardAcceptedCaptureSinkStage(
            new ClipboardAcceptedCaptureStage(),
            sink);

        bool stored = await stage.StoreAsync(execution);

        Assert.False(stored);
        Assert.Empty(sink.Stored);
    }

    [Fact]
    public async Task StoreAsync_PropagatesCancellationBeforeSinkCall()
    {
        ClipboardSelectedFormat selected = new("Text", null);
        var route = new ClipboardContentReaderRoute(selected, ClipboardContentReaderKind.Text);
        ClipboardContentReadExecution execution = CreateExecution(
            CreateCaptureContext(),
            [route],
            [new ClipboardCapturedTextContent(route, "value", 5)],
            [],
            []);
        var sink = new RecordingSink();
        var stage = new ClipboardAcceptedCaptureSinkStage(
            new ClipboardAcceptedCaptureStage(),
            sink);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            stage.StoreAsync(execution, cancellation.Token).AsTask());
        Assert.Empty(sink.Stored);
    }

    private static ClipboardCaptureContext CreateCaptureContext()
    {
        var request = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 30, 0, TimeSpan.FromHours(3)),
                "Test/Zone"));
        return new ClipboardCaptureContext(request, SourceApplication: null);
    }

    private static ClipboardContentReadExecution CreateExecution(
        ClipboardCaptureContext captureContext,
        IEnumerable<ClipboardContentReaderRoute> routes,
        IEnumerable<ClipboardCapturedContent> capturedContent,
        IEnumerable<ClipboardSelectedFormat> sizeRejectedFormats,
        IEnumerable<ClipboardSelectedFormat> deferredFormats)
    {
        ClipboardContentReaderRoute[] routeArray = routes.ToArray();
        ClipboardSelectedFormat[] selectedFormats = routeArray
            .Select(route => route.SelectedFormat)
            .Concat(sizeRejectedFormats)
            .Concat(deferredFormats)
            .ToArray();
        var policyContext = new ClipboardCapturePolicyContext(
            captureContext,
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        var snapshot = new ClipboardFormatSnapshot(
            policyContext,
            new StubContentSnapshot(selectedFormats.Select(format => format.FormatName).ToArray()));
        var selection = new ClipboardFormatSelection(snapshot, selectedFormats);
        var plan = new ClipboardContentReadPlan(selection, routeArray, unsupportedFormats: []);

        return new ClipboardContentReadExecution(
            plan,
            capturedContent,
            sizeRejectedFormats,
            deferredFormats);
    }

    private sealed class StubContentSnapshot : IClipboardContentSnapshot
    {
        public StubContentSnapshot(IReadOnlyList<string> availableFormats)
        {
            AvailableFormats = availableFormats;
        }

        public IReadOnlyList<string> AvailableFormats { get; }
    }

    private sealed class RecordingSink : IClipboardAcceptedCaptureSink
    {
        public List<ClipboardAcceptedCapture> Stored { get; } = [];

        public ValueTask StoreAsync(
            ClipboardAcceptedCapture capture,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stored.Add(capture);
            return ValueTask.CompletedTask;
        }
    }
}
