namespace Clipensk.Core.Clipboard;

public interface IClipboardSourceApplicationResolver
{
    ClipboardSourceApplication? TryResolveCurrent();
}
