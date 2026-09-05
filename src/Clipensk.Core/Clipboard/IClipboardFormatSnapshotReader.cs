namespace Clipensk.Core.Clipboard;

public interface IClipboardFormatSnapshotReader
{
    IReadOnlyList<string> ReadAvailableFormats();
}
