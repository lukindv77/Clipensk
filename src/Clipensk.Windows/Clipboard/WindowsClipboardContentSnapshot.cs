using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardContentSnapshot : IClipboardContentSnapshot
{
    public WindowsClipboardContentSnapshot(DataPackageView content)
    {
        ArgumentNullException.ThrowIfNull(content);

        Content = content;
        AvailableFormats = Array.AsReadOnly(content.AvailableFormats.ToArray());
    }

    public IReadOnlyList<string> AvailableFormats { get; }

    internal DataPackageView Content { get; }
}
