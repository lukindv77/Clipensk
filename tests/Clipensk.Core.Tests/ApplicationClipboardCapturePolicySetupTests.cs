using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ApplicationClipboardCapturePolicySetupTests
{
    [Fact]
    public void Create_AllowsApplicationInheritanceAndPositiveLimitOverride()
    {
        ClipboardCapturePolicy policy = ApplicationClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Inherit,
            [
                new("UnicodeText", ClipboardCapturePolicyRule.Inherit, true, "4096"),
                new("HTML Format", ClipboardCapturePolicyRule.Allow, false, null),
                new("Rich Text Format", ClipboardCapturePolicyRule.Deny, false, null),
            ]);

        Assert.Equal(ClipboardCapturePolicyRule.Inherit, policy.Capture);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Inherit, 4096), policy.Formats["UnicodeText"]);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Allow), policy.Formats["HTML Format"]);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Deny), policy.Formats["Rich Text Format"]);
    }

    [Fact]
    public void NeutralEditedOverrideIsRemovedWhileUneditedFormatsArePreserved()
    {
        var preserved = new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
        {
            ["UnicodeText"] = new(ClipboardCapturePolicyRule.Deny, 1024),
            ["Custom.Format"] = new(ClipboardCapturePolicyRule.Allow, 2048),
        };

        ClipboardCapturePolicy policy = ApplicationClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Allow,
            [new("UnicodeText", ClipboardCapturePolicyRule.Inherit, false, "obsolete")],
            preserved);

        Assert.Equal(ClipboardCapturePolicyRule.Allow, policy.Capture);
        Assert.False(policy.Formats.ContainsKey("UnicodeText"));
        Assert.Equal(
            new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Allow, 2048),
            policy.Formats["Custom.Format"]);
    }

    [Fact]
    public void EditedFormatsReplaceMatchingRowsButPreserveCaseDistinctNames()
    {
        var preserved = new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
        {
            ["Text"] = new(ClipboardCapturePolicyRule.Deny, 100),
            ["text"] = new(ClipboardCapturePolicyRule.Allow, 200),
        };

        ClipboardCapturePolicy policy = ApplicationClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Deny,
            [new("Text", ClipboardCapturePolicyRule.Allow, true, " 9223372036854775807 ")],
            preserved);

        Assert.Equal(long.MaxValue, policy.Formats["Text"].MaxBytes);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, policy.Formats["Text"].Capture);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Allow, 200), policy.Formats["text"]);
    }

    [Theory]
    [InlineData((ClipboardCapturePolicyRule)99)]
    public void UnknownApplicationRuleIsRejected(ClipboardCapturePolicyRule rule)
    {
        Assert.Throws<ArgumentException>(() => ApplicationClipboardCapturePolicySetup.Create(rule, []));
        Assert.Throws<ArgumentException>(() => ApplicationClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Inherit,
            [new("Text", rule, false, null)]));
    }

    [Fact]
    public void MissingFormatRuleIsRejected()
    {
        Assert.Throws<ArgumentException>(() => ApplicationClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Inherit,
            [new("Text", null, false, null)]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1,024")]
    [InlineData("1e3")]
    [InlineData("9223372036854775808")]
    public void InvalidByteOverrideIsRejected(string text)
    {
        Assert.Throws<ArgumentException>(() => ApplicationClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Inherit,
            [new("Text", ClipboardCapturePolicyRule.Allow, true, text)]));
    }

    [Fact]
    public void DuplicateEditedNamesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ApplicationClipboardCapturePolicySetup.Create(
            ClipboardCapturePolicyRule.Inherit,
            [
                new("Text", ClipboardCapturePolicyRule.Allow, false, null),
                new("Text", ClipboardCapturePolicyRule.Deny, false, null),
            ]));
    }
}
