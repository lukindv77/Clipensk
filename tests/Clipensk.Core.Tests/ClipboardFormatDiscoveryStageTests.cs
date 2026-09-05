using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardFormatDiscoveryStageTests
{
    [Fact]
    public void Discover_AllowedCaptureReadsAvailableFormats()
    {
        var reader = new StubFormatSnapshotReader(new[] { "Text", "HTML Format" });
        var stage = new ClipboardFormatDiscoveryStage(reader);
        ClipboardCapturePolicyContext policyContext = CreatePolicyContext(ClipboardCapturePolicyRule.Allow);

        ClipboardFormatSnapshot snapshot = stage.Discover(policyContext);

        Assert.Equal(policyContext, snapshot.PolicyContext);
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
        var reader = new StubFormatSnapshotReader(new[] { "Text" });
        var stage = new ClipboardFormatDiscoveryStage(reader);
        ClipboardCapturePolicyContext policyContext = CreatePolicyContext(rule);

        ClipboardFormatSnapshot snapshot = stage.Discover(policyContext);

        Assert.Empty(snapshot.AvailableFormats);
        Assert.Equal(0, reader.CallCount);
    }

    [Fact]
    public void Snapshot_CopiesAvailableFormatList()
    {
        var formats = new List<string> { "Text" };
        ClipboardCapturePolicyContext policyContext = CreatePolicyContext(ClipboardCapturePolicyRule.Allow);
        var snapshot = new ClipboardFormatSnapshot(policyContext, formats);

        formats.Add("HTML Format");

        Assert.Single(snapshot.AvailableFormats);
        Assert.Equal("Text", snapshot.AvailableFormats[0]);
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

    private sealed class StubFormatSnapshotReader : IClipboardFormatSnapshotReader
    {
        private readonly IReadOnlyList<string> _formats;

        public StubFormatSnapshotReader(IReadOnlyList<string> formats)
        {
            _formats = formats;
        }

        public int CallCount { get; private set; }

        public IReadOnlyList<string> ReadAvailableFormats()
        {
            CallCount++;
            return _formats;
        }
    }
}
