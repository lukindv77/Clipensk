using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardAcceptedCaptureStageTests
{
    [Fact]
    public void Create_ProjectsOnlyAcceptedPayloadAndPreservesCaptureContext()
    {
        ClipboardCaptureContext captureContext = CreateCaptureContext();
        ClipboardSelectedFormat acceptedFormat = new("Text", 128);
        ClipboardSelectedFormat rejectedFormat = new("Html", 16);
        ClipboardSelectedFormat deferredFormat = new("StorageItems", 1024);
        ClipboardContentReaderRoute route = new(
            acceptedFormat,
            ClipboardContentReaderKind.Text);
        var acceptedContent = new ClipboardCapturedTextContent(
            route,
            "accepted",
            canonicalByteCount: 8);
        ClipboardContentReadExecution execution = CreateExecution(
            captureContext,
            [route],
            [acceptedContent],
            [rejectedFormat],
            [deferredFormat]);
        var stage = new ClipboardAcceptedCaptureStage();

        ClipboardAcceptedCapture? result = stage.Create(execution);

        Assert.NotNull(result);
        Assert.Equal(captureContext, result.CaptureContext);
        Assert.Single(result.Content);
        Assert.Same(acceptedContent, result.Content[0]);
    }

    [Fact]
    public void Create_ReturnsNullWhenNothingWasAccepted()
    {
        ClipboardCaptureContext captureContext = CreateCaptureContext();
        ClipboardSelectedFormat rejectedFormat = new("Text", 1);
        ClipboardContentReadExecution execution = CreateExecution(
            captureContext,
            routes: [],
            capturedContent: [],
            sizeRejectedFormats: [rejectedFormat],
            deferredFormats: []);
        var stage = new ClipboardAcceptedCaptureStage();

        ClipboardAcceptedCapture? result = stage.Create(execution);

        Assert.Null(result);
    }

    [Fact]
    public void AcceptedCapture_RejectsEmptyPayloadCollection()
    {
        Assert.Throws<ArgumentException>(() =>
            new ClipboardAcceptedCapture(CreateCaptureContext(), []));
    }

    private static ClipboardCaptureContext CreateCaptureContext()
    {
        var request = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 30, 0, TimeSpan.FromHours(3)),
                "Test/Zone"));

        return new ClipboardCaptureContext(
            request,
            new ClipboardSourceApplication(42, @"C:\\Apps\\Source.exe"));
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
}
