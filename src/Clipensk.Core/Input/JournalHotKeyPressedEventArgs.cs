namespace Clipensk.Core.Input;

public sealed class JournalHotKeyPressedEventArgs : EventArgs
{
    public JournalHotKeyPressedEventArgs(InvocationApplication? invocationApplication)
    {
        InvocationApplication = invocationApplication;
    }

    public InvocationApplication? InvocationApplication { get; }
}
