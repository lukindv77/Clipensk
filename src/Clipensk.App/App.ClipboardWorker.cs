using Clipensk.Core.Application;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Windows;

namespace Clipensk.App;

public partial class App
{
    private readonly object _clipboardWorkerGate = new();
    private Task _clipboardWorkerTask = Task.CompletedTask;
    private ProtectedStorageSessionLease? _clipboardWorkerSession;
    private ProtectedClipboardDeliveryServices? _clipboardWorkerServices;
    private long _clipboardWorkerGeneration;

    private void RequestClipboardWorkerStart(
        JournalWindow window,
        ResidentWindowsHost host,
        ProtectedApplicationLifecycle lifecycle,
        ProtectedStorageSessionLease session,
        ProtectedClipboardDeliveryServices services)
    {
        if (!lifecycle.CanAccessProtectedData || !session.IsActive)
        {
            return;
        }

        Task previousTask;
        long generation;

        lock (_clipboardWorkerGate)
        {
            if (ReferenceEquals(_clipboardWorkerSession, session) &&
                ReferenceEquals(_clipboardWorkerServices, services) &&
                !_clipboardWorkerTask.IsCompleted)
            {
                return;
            }

            generation = Interlocked.Increment(ref _clipboardWorkerGeneration);
            previousTask = _clipboardWorkerTask;
            _clipboardWorkerSession = session;
            _clipboardWorkerServices = services;
            _clipboardWorkerTask = Task.Run(
                () => RunClipboardWorkerAsync(
                    previousTask,
                    window,
                    host,
                    lifecycle,
                    session,
                    services,
                    generation),
                CancellationToken.None);
        }
    }

    private async Task RunClipboardWorkerAsync(
        Task previousTask,
        JournalWindow window,
        ResidentWindowsHost host,
        ProtectedApplicationLifecycle lifecycle,
        ProtectedStorageSessionLease session,
        ProtectedClipboardDeliveryServices services,
        long generation)
    {
        try
        {
            await previousTask.ConfigureAwait(false);
        }
        catch
        {
            // A previous generation is already revoked. Its completion must not prevent
            // a later valid protected session from becoming the single queue reader.
        }

        if (!IsCurrentClipboardWorker(
                window,
                host,
                lifecycle,
                session,
                services,
                generation))
        {
            return;
        }

        var worker = new ClipboardAcceptedCaptureWorker(services.Delivery);
        await worker.RunAsync(session.CancellationToken).ConfigureAwait(false);
    }

    private bool IsCurrentClipboardWorker(
        JournalWindow window,
        ResidentWindowsHost host,
        ProtectedApplicationLifecycle lifecycle,
        ProtectedStorageSessionLease session,
        ProtectedClipboardDeliveryServices services,
        long generation)
    {
        if (generation != Volatile.Read(ref _clipboardWorkerGeneration) ||
            !ReferenceEquals(_window, window) ||
            !ReferenceEquals(_residentWindowsHost, host) ||
            !ReferenceEquals(_lifecycle, lifecycle) ||
            !lifecycle.CanAccessProtectedData ||
            !session.IsActive ||
            !window.TryGetActiveProtectedStorageSession(out ProtectedStorageSessionLease? currentSession) ||
            !ReferenceEquals(currentSession, session))
        {
            return false;
        }

        lock (_clipboardWorkerGate)
        {
            return ReferenceEquals(_clipboardWorkerSession, session) &&
                ReferenceEquals(_clipboardWorkerServices, services);
        }
    }

    private void InvalidateClipboardWorker()
    {
        Interlocked.Increment(ref _clipboardWorkerGeneration);
        lock (_clipboardWorkerGate)
        {
            _clipboardWorkerSession = null;
            _clipboardWorkerServices = null;
        }
    }
}
