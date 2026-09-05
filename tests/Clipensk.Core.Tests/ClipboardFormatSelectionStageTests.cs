using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardFormatSelectionStageTests
{
    [Fact]
    public void Select_IncludesOnlyExplicitlyAllowedAvailableFormats()
    {
        ClipboardFormatSnapshot snapshot = CreateSnapshot(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, 1024),
                ["HTML Format"] = new(ClipboardCapturePolicyRule.Inherit, 2048),
                ["Custom.Format"] = new(ClipboardCapturePolicyRule.Deny, 4096),
                ["Unavailable"] = new(ClipboardCapturePolicyRule.Allow, 8192),
            },
            "Text",
            "HTML Format",
            "Custom.Format",
            "Unknown.Format",
            "Text");
        var stage = new ClipboardFormatSelectionStage();

        ClipboardFormatSelection result = stage.Select(snapshot);

        Assert.Same(snapshot, result.Snapshot);
        Assert.Single(result.Formats);
        Assert.Equal("Text", result.Formats[0].FormatName);
        Assert.Equal(1024, result.Formats[0].MaxBytes);
    }

    [Theory]
    [InlineData(ClipboardCapturePolicyRule.Deny)]
    [InlineData(ClipboardCapturePolicyRule.Inherit)]
    public void Select_NonAllowedCaptureReturnsNoFormats(ClipboardCapturePolicyRule captureRule)
    {
        ClipboardFormatSnapshot snapshot = CreateSnapshot(
            captureRule,
            new Dictionary<string, ClipboardFormatCapturePolicy>
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow),
            },
            "Text");
        var stage = new ClipboardFormatSelectionStage();

        ClipboardFormatSelection result = stage.Select(snapshot);

        Assert.Empty(result.Formats);
    }

    [Fact]
    public void Select_WithoutContentSnapshotReturnsNoFormats()
    {
        ClipboardCapturePolicyContext policyContext = CreatePolicyContext(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow),
            });
        var snapshot = new ClipboardFormatSnapshot(policyContext, contentSnapshot: null);
        var stage = new ClipboardFormatSelectionStage();

        ClipboardFormatSelection result = stage.Select(snapshot);

        Assert.Empty(result.Formats);
    }

    private static ClipboardFormatSnapshot CreateSnapshot(
        ClipboardCapturePolicyRule captureRule,
        IReadOnlyDictionary<string, ClipboardFormatCapturePolicy> formatPolicies,
        params string[] availableFormats)
    {
        ClipboardCapturePolicyContext policyContext = CreatePolicyContext(captureRule, formatPolicies);
        return new ClipboardFormatSnapshot(policyContext, new StubContentSnapshot(availableFormats));
    }

    private static ClipboardCapturePolicyContext CreatePolicyContext(
        ClipboardCapturePolicyRule captureRule,
        IReadOnlyDictionary<string, ClipboardFormatCapturePolicy> formatPolicies)
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
            new ClipboardCapturePolicy(captureRule, formatPolicies));
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
