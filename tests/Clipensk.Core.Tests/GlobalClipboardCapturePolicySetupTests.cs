using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class GlobalClipboardCapturePolicySetupTests
{
    [Fact]
    public void MissingGlobalChoice_IsNotAnImplicitDeny()
    {
        Assert.Throws<ArgumentException>(() => GlobalClipboardCapturePolicySetup.Create(null, []));
    }

    [Theory]
    [InlineData(ClipboardCapturePolicyRule.Inherit)]
    [InlineData((ClipboardCapturePolicyRule)99)]
    public void UnresolvedOrUnknownRules_AreRejected(ClipboardCapturePolicyRule rule)
    {
        Assert.Throws<ArgumentException>(() => GlobalClipboardCapturePolicySetup.Create(rule, []));
        Assert.Throws<ArgumentException>(() => GlobalClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Allow, [new("Text", rule, false, null)]));
    }

    [Fact]
    public void EverySuppliedFormatRequiresAnExplicitRuleEvenWhenGlobalRuleIsDeny()
    {
        Assert.Throws<ArgumentException>(() => GlobalClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Deny, [new("Text", null, null, null)]));
    }

    [Fact]
    public void AllowedFormatRequiresAnExplicitSizeChoice()
    {
        Assert.Throws<ArgumentException>(() => GlobalClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Allow, [new("Text", ClipboardCapturePolicyRule.Allow, null, "1024")]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1,024")]
    [InlineData("1e3")]
    [InlineData("9223372036854775808")]
    public void InvalidByteLimitIsNotRoundedClampedOrReplaced(string text)
    {
        Assert.Throws<ArgumentException>(() => GlobalClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Allow, [new("Text", ClipboardCapturePolicyRule.Allow, true, text)]));
    }

    [Fact]
    public void ExplicitChoicesPreserveExactNamesAndLongByteLimits()
    {
        ClipboardCapturePolicy policy = GlobalClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Allow,
            [
                new("Text", ClipboardCapturePolicyRule.Allow, true, " 9223372036854775807 "),
                new("text", ClipboardCapturePolicyRule.Allow, false, "obsolete value"),
                new("HTML Format", ClipboardCapturePolicyRule.Deny, true, "obsolete value"),
            ]);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, policy.Capture);
        Assert.Equal(3, policy.Formats.Count);
        Assert.Equal(long.MaxValue, policy.Formats["Text"].MaxBytes);
        Assert.Null(policy.Formats["text"].MaxBytes);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Deny), policy.Formats["HTML Format"]);
    }

    [Fact]
    public void DuplicateNamesAreRejectedAndMissingNamesAreNotAdded()
    {
        Assert.Throws<ArgumentException>(() => GlobalClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Allow,
            [new("Text", ClipboardCapturePolicyRule.Deny, null, null), new("Text", ClipboardCapturePolicyRule.Allow, false, null)]));
        ClipboardCapturePolicy policy = GlobalClipboardCapturePolicySetup.Create(ClipboardCapturePolicyRule.Deny, []);
        Assert.Equal(ClipboardCapturePolicyRule.Deny, policy.Capture);
        Assert.Empty(policy.Formats);
    }
}
