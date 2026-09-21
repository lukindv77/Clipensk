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
    internal Task<ClipboardRestorePlan?> TryRestoreToClipboardAsync(
        ProtectedStorageSessionLease session,
        ClipboardHistoryEntry entry,
        CancellationToken cancellationToken = default) =>
        TryRestoreToClipboardCoreAsync(
            session,
            entry,
            prepared => ClipboardRestorePlanFactory.Create(
                prepared,
                WindowsClipboardRestoreWriter.PlainTextFormatName),
            cancellationToken);

    /// <summary>
    /// The "paste as plain text" mode from the product decision in <c>docs/OPEN_QUESTIONS.md</c>
    /// §10: republishes only the entry's plain-text representation, discarding every other captured
    /// format, regardless of what was actually saved.
    /// </summary>
    internal Task<ClipboardRestorePlan?> TryRestorePlainTextToClipboardAsync(
        ProtectedStorageSessionLease session,
        ClipboardHistoryEntry entry,
        CancellationToken cancellationToken = default) =>
        TryRestoreToClipboardCoreAsync(
            session,
            entry,
            prepared => ClipboardRestorePlanFactory.CreatePlainTextOnly(
                prepared,
                WindowsClipboardRestoreWriter.PlainTextFormatName),
            cancellationToken);

    private async Task<ClipboardRestorePlan?> TryRestoreToClipboardCoreAsync(
        ProtectedStorageSessionLease session,
        ClipboardHistoryEntry entry,
        Func<RestorableClipboardEntry, ClipboardRestorePlan> buildPlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(buildPlan);

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
                return buildPlan(prepared);
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
