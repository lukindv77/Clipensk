namespace Clipensk.Core.Clipboard;

public interface IClipboardContentSnapshot
{
    IReadOnlyList<string> AvailableFormats { get; }
}
