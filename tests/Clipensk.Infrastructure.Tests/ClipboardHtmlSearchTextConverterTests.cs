using System.Text;
using Clipensk.Infrastructure.Clipboard;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

public sealed class ClipboardHtmlSearchTextConverterTests
{
    [Fact]
    public void TryConvert_ExtractsVisibleTextFromMarkerDelimitedFragment()
    {
        var converter = new HtmlAgilityPackClipboardHtmlSearchTextConverter();
        const string ClipboardHtml = """
            Version:1.0
            StartHTML:-1
            EndHTML:-1
            StartFragment:-1
            EndFragment:-1
            <html><body><!-- StartFragment -->
            <div>Привет&nbsp;<b>мир</b></div>
            <p>Вторая <span>строка</span></p>
            <script>secret-script</script><style>.secret-style{display:none}</style>
            <!-- EndFragment --></body></html>
            """;

        string? result = converter.TryConvert(ClipboardHtml);

        Assert.Equal("Привет мир Вторая строка", result);
    }

    [Fact]
    public void TryConvert_UsesUtf8ByteOffsetsWhenMarkersAreMissing()
    {
        var converter = new HtmlAgilityPackClipboardHtmlSearchTextConverter();
        string clipboardHtml = CreateOffsetOnlyClipboardHtml(
            "<div>До 😀</div><p>после &amp; ещё</p>");

        string? result = converter.TryConvert(clipboardHtml);

        Assert.Equal("До 😀 после & ещё", result);
    }

    [Fact]
    public void TryConvert_InvalidFragmentMetadataFailsClosed()
    {
        var converter = new HtmlAgilityPackClipboardHtmlSearchTextConverter();
        const string ClipboardHtml = """
            Version:1.0
            StartHTML:0000000100
            EndHTML:0000000200
            StartFragment:0000009999
            EndFragment:0000010000
            <html><body><p>value</p></body></html>
            """;

        string? result = converter.TryConvert(ClipboardHtml);

        Assert.Null(result);
    }

    [Fact]
    public void TryConvert_PreservesWordBoundariesAcrossStructuralElements()
    {
        var converter = new HtmlAgilityPackClipboardHtmlSearchTextConverter();
        const string ClipboardHtml = """
            <!--StartFragment--><table><tr><td>A</td><td>B</td></tr></table><ul><li>C</li><li>D</li></ul><!--EndFragment-->
            """;

        string? result = converter.TryConvert(ClipboardHtml);

        Assert.Equal("A B C D", result);
    }

    private static string CreateOffsetOnlyClipboardHtml(string fragment)
    {
        const string Prefix = "<html><body>";
        const string Suffix = "</body></html>";
        const string HeaderTemplate = """
            Version:1.0
            StartHTML:{0:D10}
            EndHTML:{1:D10}
            StartFragment:{2:D10}
            EndFragment:{3:D10}
            """;

        string placeholderHeader = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HeaderTemplate,
            0,
            0,
            0,
            0);
        int startHtml = Encoding.UTF8.GetByteCount(placeholderHeader);
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(Prefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(Suffix);

        string header = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HeaderTemplate,
            startHtml,
            endHtml,
            startFragment,
            endFragment);
        Assert.Equal(startHtml, Encoding.UTF8.GetByteCount(header));
        return header + Prefix + fragment + Suffix;
    }
}
