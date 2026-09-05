namespace Clipensk.Core.Input;

public interface IGlobalHotKeyService : IDisposable
{
    event EventHandler<JournalHotKeyPressedEventArgs>? Pressed;

    bool IsRegistered { get; }

    HotKeyGesture? CurrentGesture { get; }

    void Register(HotKeyGesture gesture);

    void Unregister();
}
