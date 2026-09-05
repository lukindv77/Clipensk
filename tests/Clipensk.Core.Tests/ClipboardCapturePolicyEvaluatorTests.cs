using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardCapturePolicyEvaluatorTests
{
    [Fact]
    public void Merge_ApplicationOverridesRulesAndInheritsGlobalLimit()
    {
        var global = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>
            {
                ["UnicodeText"] = new(ClipboardCapturePolicyRule.Allow, 1024),
                ["HTML Format"] = new(ClipboardCapturePolicyRule.Deny, 2048),
            });
        var application = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Inherit,
            new Dictionary<string, ClipboardFormatCapturePolicy>
            {
                ["UnicodeText"] = new(ClipboardCapturePolicyRule.Deny),
                ["HTML Format"] = new(ClipboardCapturePolicyRule.Inherit, 4096),
                ["Custom.Format"] = new(ClipboardCapturePolicyRule.Allow, 512),
            });
        var evaluator = new ClipboardCapturePolicyEvaluator();

        ClipboardCapturePolicy effective = evaluator.Merge(global, application);

        Assert.Equal(ClipboardCapturePolicyRule.Allow, effective.Capture);
        Assert.Equal(ClipboardCapturePolicyRule.Deny, effective.Formats["UnicodeText"].Capture);
        Assert.Equal(1024, effective.Formats["UnicodeText"].MaxBytes);
        Assert.Equal(ClipboardCapturePolicyRule.Deny, effective.Formats["HTML Format"].Capture);
        Assert.Equal(4096, effective.Formats["HTML Format"].MaxBytes);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, effective.Formats["Custom.Format"].Capture);
        Assert.Equal(512, effective.Formats["Custom.Format"].MaxBytes);
    }

    [Fact]
    public void Merge_ApplicationCanDenyEntireCapture()
    {
        var global = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow);
        var application = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny);
        var evaluator = new ClipboardCapturePolicyEvaluator();

        ClipboardCapturePolicy effective = evaluator.Merge(global, application);

        Assert.Equal(ClipboardCapturePolicyRule.Deny, effective.Capture);
    }

    [Fact]
    public void Merge_DoesNotInventDefaultsForUnconfiguredPolicy()
    {
        var global = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Inherit);
        var evaluator = new ClipboardCapturePolicyEvaluator();

        ClipboardCapturePolicy effective = evaluator.Merge(global, applicationPolicy: null);

        Assert.Equal(ClipboardCapturePolicyRule.Inherit, effective.Capture);
        Assert.Empty(effective.Formats);
    }

    [Fact]
    public void Stage_PreservesResolvedSourceAndEventTimeContext()
    {
        var request = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 15, 30, TimeSpan.FromHours(3)),
                "Test/Zone"));
        var sourceApplication = new ClipboardSourceApplication(4242, @"C:\Apps\Source.exe");
        var captureContext = new ClipboardCaptureContext(request, sourceApplication);
        var global = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny);
        var application = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow);
        var stage = new ClipboardCapturePolicyStage(new ClipboardCapturePolicyEvaluator());

        ClipboardCapturePolicyContext result = stage.Evaluate(captureContext, global, application);

        Assert.Equal(captureContext, result.CaptureContext);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, result.Policy.Capture);
    }
}
