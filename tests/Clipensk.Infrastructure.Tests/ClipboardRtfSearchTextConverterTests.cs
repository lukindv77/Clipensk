using Clipensk.Infrastructure.Clipboard;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

public sealed class ClipboardRtfSearchTextConverterTests
{
    [Fact]
    public void TryConvert_ExtractsVisibleTextAndStructuralSeparators()
    {
        var converter = new ManagedClipboardRtfSearchTextConverter();
        const string Rtf = """{\rtf1\ansi Hello \b world\b0\par Next\tab item}""";

        string? result = converter.TryConvert(Rtf);

        Assert.Equal("Hello world Next item", result);
    }

    [Fact]
    public void TryConvert_DecodesUnicodeAndSkipsFallbackCharacters()
    {
        var converter = new ManagedClipboardRtfSearchTextConverter();
        const string Rtf = """{\rtf1\ansi\uc1 \u1055?\u1088?\u1080?\u1074?\u1077?\u1090? \u1040\'c0}""";

        string? result = converter.TryConvert(Rtf);

        Assert.Equal("Привет А", result);
    }

    [Fact]
    public void TryConvert_SkipsMetadataHiddenTextAndFieldInstructions()
    {
        var converter = new ManagedClipboardRtfSearchTextConverter();
        const string Rtf = """{\rtf1\ansi{\fonttbl{\f0 Arial;}}Visible {\*\generator Secret;}\v hidden\v0 {\field{\*\fldinst HYPERLINK "https://secret"}{\fldrslt Link}}}""";

        string? result = converter.TryConvert(Rtf);

        Assert.Equal("Visible Link", result);
    }

    [Fact]
    public void TryConvert_MalformedStructureFailsClosed()
    {
        var converter = new ManagedClipboardRtfSearchTextConverter();

        string? result = converter.TryConvert("""{\rtf1\ansi value""");

        Assert.Null(result);
    }

    [Fact]
    public void TryConvert_BinaryPayloadFailsClosed()
    {
        var converter = new ManagedClipboardRtfSearchTextConverter();
        const string Rtf = """{\rtf1\ansi before {\pict\bin3 abc} after}""";

        string? result = converter.TryConvert(Rtf);

        Assert.Null(result);
    }

    [Fact]
    public void TryConvert_VisibleNonAsciiLegacyHexWithoutUnicodeFailsClosed()
    {
        var converter = new ManagedClipboardRtfSearchTextConverter();
        const string Rtf = """{\rtf1\ansi \'cf}""";

        string? result = converter.TryConvert(Rtf);

        Assert.Null(result);
    }

    [Fact]
    public void TryConvert_NonRtfInputFailsClosed()
    {
        var converter = new ManagedClipboardRtfSearchTextConverter();

        string? result = converter.TryConvert("plain text");

        Assert.Null(result);
    }
}
