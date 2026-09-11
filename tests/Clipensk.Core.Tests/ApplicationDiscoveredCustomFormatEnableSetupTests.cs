using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ApplicationDiscoveredCustomFormatEnableSetupTests
{
    private const string CustomFormat = "Vendor.Product.Binary";

    [Fact]
    public void Create_AllowsExactDiscoveredCustomFormatAndPreservesOtherOverrides()
    {
        var preserved = new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
        {
            ["Text"] = new(ClipboardCapturePolicyRule.Deny, 128),
            ["Other.Custom"] = new(ClipboardCapturePolicyRule.Allow, 256),
        };

        ApplicationDiscoveredCustomFormatEnableRequest request =
            ApplicationDiscoveredCustomFormatEnableSetup.Create(
                ClipboardCapturePolicyRule.Inherit,
                [CustomFormat, "Text"],
                ["Text", "HTML Format"],
                CustomFormat,
                " .Foo ",
                "4096",
                preserved);

        Assert.Equal(CustomFormat, request.FormatName);
        Assert.Equal(" .Foo ", request.FileExtension);
        Assert.Equal(ClipboardCapturePolicyRule.Inherit, request.Policy.Capture);
        Assert.Equal(
            new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Allow, 4096),
            request.Policy.Formats[CustomFormat]);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Deny, 128), request.Policy.Formats["Text"]);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Allow, 256), request.Policy.Formats["Other.Custom"]);
    }

    [Fact]
    public void Create_RequiresExactDiscoveredName()
    {
        Assert.Throws<ArgumentException>(() =>
            ApplicationDiscoveredCustomFormatEnableSetup.Create(
                ClipboardCapturePolicyRule.Inherit,
                [CustomFormat],
                ["Text"],
                CustomFormat.ToLowerInvariant(),
                ".foo",
                "1024"));
    }

    [Fact]
    public void Create_RejectsStandardDiscoveredFormat()
    {
        Assert.Throws<ArgumentException>(() =>
            ApplicationDiscoveredCustomFormatEnableSetup.Create(
                ClipboardCapturePolicyRule.Inherit,
                ["Text"],
                ["Text"],
                "Text",
                ".txt",
                "1024"));
    }

    [Theory]
    [InlineData("WaveAudio")]
    [InlineData("RiffAudio")]
    [InlineData("FileContents")]
    public void Create_RejectsProhibitedDiscoveredFormat(string formatName)
    {
        Assert.Throws<ArgumentException>(() =>
            ApplicationDiscoveredCustomFormatEnableSetup.Create(
                ClipboardCapturePolicyRule.Inherit,
                [formatName],
                ["Text"],
                formatName,
                ".bin",
                "1024"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("unlimited")]
    public void Create_RequiresPositiveExplicitByteLimit(string maxBytesText)
    {
        Assert.Throws<ArgumentException>(() =>
            ApplicationDiscoveredCustomFormatEnableSetup.Create(
                ClipboardCapturePolicyRule.Allow,
                [CustomFormat],
                ["Text"],
                CustomFormat,
                ".foo",
                maxBytesText));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RequiresExtensionInput(string fileExtension)
    {
        Assert.Throws<ArgumentException>(() =>
            ApplicationDiscoveredCustomFormatEnableSetup.Create(
                ClipboardCapturePolicyRule.Allow,
                [CustomFormat],
                ["Text"],
                CustomFormat,
                fileExtension,
                "1024"));
    }
}
