using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardFormatDiscoveryStageTests
{
    [Fact]
    public void Discover_AllowedCaptureRetainsContentSnapshot()
    {
        var contentSnapshot = new StubContentSnapshot(new[] { "Text", "HTML Format" });
        var reader = new StubFormatSnapshotReader(contentSnapshot);
        var stage = new ClipboardFormatDiscoveryStage(reader);
        ClipboardCapturePolicyContext policyContext = CreatePolicyContext(ClipboardCapturePolicyRule.Allow);

        ClipboardFormatSnapshot snapshot = stage.Discover(policyContext);

        Assert.Equal(policyContext, snapshot.PolicyContext);
        Assert.Same(contentSnapshot, snapshot.ContentSnapshot);
        Assert.Equal(2, snapshot.AvailableFormats.Count);
        Assert.Equal("Text", snapshot.AvailableFormats[0]);
        Assert.Equal("HTML Format", snapshot.AvailableFormats[1]);
        Assert.Equal(1, reader.CallCount);
    }

    [Theory]
    [InlineData(ClipboardCapturePolicyRule.Deny)]
    [InlineData(ClipboardCapturePolicyRule.Inherit)]
    public void Discover_NonAllowedCaptureDoesNotTouchClipboard(ClipboardCapturePolicyRule rule)
    {
        var contentSnapshot = new StubContentSnapshot(new[] { "Text" });
        var reader = new StubFormatSnapshotReader(contentSnapshot);
        var stage = new ClipboardFormatDiscoveryStage(reader);
        ClipboardCapturePolicyContext policyContext = CreatePolicyContext(rule);

        ClipboardFormatSnapshot snapshot = stage.Discover(policyContext);

        Assert.Null(snapshot.ContentSnapshot);
        Assert.Empty(snapshot.AvailableFormats);
        Assert.Equal(0, reader.CallCount);
    }

    private static ClipboardCapturePolicyContext CreatePolicyContext(ClipboardCapturePolicyRule rule)
    {
        var request = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 15, 30, TimeSpan.FromHours(3)),
                "Test/Zone"));
        var captureContext = new ClipboardCaptureContext(
            request,
            new ClipboardSourceApplication(4242, @"C:\Apps\Source.exe"));

        return new ClipboardCapturePolicyContext(
            captureContext,
            new ClipboardCapturePolicy(rule));
    }

    private sealed class StubContentSnapshot : IClipboardContentSnapshot
    {
        public StubContentSnapshot(IReadOnlyList<string> availableFormats)
        {
            AvailableFormats = availableFormats;
        }

        public IReadOnlyList<string> AvailableFormats { get; }
    }

    private sealed class StubFormatSnapshotReader : IClipboardFormatSnapshotReader
    {
        private readonly IClipboardContentSnapshot _snapshot;

        public StubFormatSnapshotReader(IClipboardContentSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public int CallCount { get; private set; }

        public IClipboardContentSnapshot ReadSnapshot()
        {
            CallCount++;
            return _snapshot;
        }
    }
}
