using Clipensk.Core.Application;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.History;
using Clipensk.Windows;
using Clipensk.Windows.Clipboard;

namespace Clipensk.App;

public partial class App
{
    /// <summary>
    /// Republishes one stored history entry to the clipboard.
    ///
    /// Capture is deliberately not suspended. A rotation or a policy change mutates durable
    /// storage and must not race the capture worker, but restoring only reads verified files and
    /// writes to the clipboard, and the one update that write raises is suppressed by sequence
    /// number. Suspending the runtime would stop capturing everything else for no benefit.
    ///
    /// Returns <c>null</c> when there is no current protected session to restore from. A genuine
    /// failure propagates so the caller can distinguish it from "not available".
    /// </summary>
    internal async Task<ClipboardRestorePlan?> TryRestoreToClipboardAsync(
        ProtectedStorageSessionLease session,
        ClipboardHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);

        JournalWindow? window = _window;
        ResidentWindowsHost? host = _residentWindowsHost;
        ProtectedApplicationLifecycle? lifecycle = _lifecycle;
        if (window is null || host is null || lifecycle is null ||
            !IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            return null;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;

        // Verification reads and hashes external files, so it stays off the UI thread. The publish
        // itself must run here, on the thread that owns the resident message window.
        ClipboardRestorePlan plan = await Task.Run(
            async () =>
            {
                RestorableClipboardEntry prepared =
                    await new ProtectedClipboardHistoryRestoreService(session)
                        .PrepareAsync(entry, token)
                        .ConfigureAwait(false);
                return ClipboardRestorePlanFactory.Create(
                    prepared,
                    WindowsClipboardRestoreWriter.PlainTextFormatName);
            },
            token);

        if (!IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            return null;
        }

        await host.RestoreWriter.WriteAsync(plan, token);
        return plan;
    }
}
