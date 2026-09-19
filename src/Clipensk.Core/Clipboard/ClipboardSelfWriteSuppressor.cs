namespace Clipensk.Core.Clipboard;

/// <summary>
/// Decides whether one observed clipboard update belongs to Clipensk's own write.
///
/// Republishing a stored entry to the clipboard raises the same <c>WM_CLIPBOARDUPDATE</c> any other
/// application's copy raises. Without this, reusing an entry would immediately capture it again as
/// a brand-new event whose source application is Clipensk itself, so every reuse would duplicate
/// history. Windows gives an exact identity for one clipboard state — the clipboard sequence number
/// — so the writer records the number its own write produced and exactly that update is skipped.
///
/// Suppression is always consumed by the next observed update, whichever it is. If another
/// application wins the race and changes the clipboard first, that update does not match, it is
/// captured normally, and the stale suppression is dropped rather than swallowing a later unrelated
/// copy. The failure direction is therefore an extra capture, never a lost one.
///
/// This type holds no Win32 dependency so the rule can be tested; the caller supplies the sequence
/// numbers.
/// </summary>
public sealed class ClipboardSelfWriteSuppressor
{
    private readonly object _gate = new();
    private uint? _suppressedSequenceNumber;

    /// <summary>Records the clipboard sequence number produced by Clipensk's own write.</summary>
    public void SuppressSequenceNumber(uint sequenceNumber)
    {
        lock (_gate)
        {
            _suppressedSequenceNumber = sequenceNumber;
        }
    }

    /// <summary>
    /// Returns false exactly once, for the update matching a recorded self-write. Any observed
    /// update clears the recorded number, so suppression can never outlive the update it was armed
    /// for.
    /// </summary>
    public bool ShouldCapture(uint sequenceNumber)
    {
        lock (_gate)
        {
            uint? suppressed = _suppressedSequenceNumber;
            _suppressedSequenceNumber = null;
            return suppressed != sequenceNumber;
        }
    }

    /// <summary>Drops any armed suppression, for example when capture is suspended or restarted.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _suppressedSequenceNumber = null;
        }
    }
}
