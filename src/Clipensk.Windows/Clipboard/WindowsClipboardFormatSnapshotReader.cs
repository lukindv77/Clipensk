using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;
using WindowsClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardFormatSnapshotReader : IClipboardFormatSnapshotReader
{
    private readonly ClipboardStaThread _clipboardThread;

    public WindowsClipboardFormatSnapshotReader(ClipboardStaThread clipboardThread)
    {
        _clipboardThread = clipboardThread ?? throw new ArgumentNullException(nameof(clipboardThread));
    }

    /// <summary>
    /// Takes the snapshot on the clipboard thread: the view it holds is bound to that thread's
    /// apartment, and the content readers read it there too.
    /// </summary>
    public IClipboardContentSnapshot ReadSnapshot() =>
        _clipboardThread.Run(() =>
        {
            DataPackageView content = WindowsClipboard.GetContent();
            return (IClipboardContentSnapshot)new WindowsClipboardContentSnapshot(content);
        });
}
