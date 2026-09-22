using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardHistoryPurgeRuleTests
{
    [Fact]
    public void FromEffectivePolicy_RetainsOnlyExplicitlyAllowedExactFormatNames()
    {
        var global = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, 10),
                ["HTML Format"] = new(ClipboardCapturePolicyRule.Allow),
                ["PNG"] = new(ClipboardCapturePolicyRule.Deny),
            });
        var application = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Inherit,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["HTML Format"] = new(ClipboardCapturePolicyRule.Deny),
            });

        ClipboardHistoryPurgeRule rule = ClipboardHistoryPurgeRule.FromEffectivePolicy(
            new ClipboardCapturePolicyEvaluator().Merge(global, application));

        Assert.True(rule.Retains("Text"));
        Assert.False(rule.Retains("text"));
        Assert.False(rule.Retains("HTML Format"));
        Assert.False(rule.Retains("PNG"));
        Assert.False(rule.Retains("Unlisted"));
        Assert.Equal(["Text"], rule.AllowedFormats);
    }

    [Fact]
    public void DenyAll_RetainsNothingEvenForListedFormats()
    {
        var rule = new ClipboardHistoryPurgeRule(ClipboardCapturePolicyRule.Deny, ["Text"]);

        Assert.False(rule.Retains("Text"));
    }

    [Fact]
    public void Constructor_RejectsAnUnresolvedRuleAndDuplicateFormats()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClipboardHistoryPurgeRule(ClipboardCapturePolicyRule.Inherit, []));
        Assert.Throws<ArgumentException>(() =>
            new ClipboardHistoryPurgeRule(ClipboardCapturePolicyRule.Allow, ["Text", "Text"]));
    }
}
