using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;
using WindowsClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardFormatSnapshotReader : IClipboardFormatSnapshotReader
{
    public IReadOnlyList<string> ReadAvailableFormats()
    {
        DataPackageView content = WindowsClipboard.GetContent();
        return content.AvailableFormats.ToArray();
    }
}
