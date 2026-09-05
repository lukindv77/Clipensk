namespace Clipensk.Core.Clipboard;

public interface IClipboardHtmlSearchTextConverter
{
    string? TryConvert(string clipboardHtml);
}
