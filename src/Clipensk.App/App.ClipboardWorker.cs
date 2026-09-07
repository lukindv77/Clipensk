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
    private CancellationTokenSource? _clipboardWorkerCancellation;
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
        CancellationTokenSource? previousCancellation;
        CancellationTokenSource cancellation;
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
            previousCancellation = _clipboardWorkerCancellation;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                session.CancellationToken);

            _clipboardWorkerSession = session;
            _clipboardWorkerServices = services;
            _clipboardWorkerCancellation = cancellation;
            _clipboardWorkerTask = Task.Run(
                () => RunClipboardWorkerAsync(
                    previousTask,
                    window,
                    host,
                    lifecycle,
                    session,
                    services,
                    cancellation,
                    generation),
                CancellationToken.None);
        }

        TryCancelClipboardWorker(previousCancellation);
    }

    private async Task RunClipboardWorkerAsync(
        Task previousTask,
        JournalWindow window,
        ResidentWindowsHost host,
        ProtectedApplicationLifecycle lifecycle,
        ProtectedStorageSessionLease session,
        ProtectedClipboardDeliveryServices services,
        CancellationTokenSource cancellation,
        long generation)
    {
        try
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
                    cancellation,
                    generation))
            {
                return;
            }

            var worker = new ClipboardAcceptedCaptureWorker(services.Delivery);
            await worker.RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_clipboardWorkerGate)
            {
                if (ReferenceEquals(_clipboardWorkerCancellation, cancellation))
                {
                    _clipboardWorkerCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private bool IsCurrentClipboardWorker(
        JournalWindow window,
        ResidentWindowsHost host,
        ProtectedApplicationLifecycle lifecycle,
        ProtectedStorageSessionLease session,
        ProtectedClipboardDeliveryServices services,
        CancellationTokenSource cancellation,
        long generation)
    {
        if (generation != Volatile.Read(ref _clipboardWorkerGeneration) ||
            !ReferenceEquals(_window, window) ||
            !ReferenceEquals(_residentWindowsHost, host) ||
            !ReferenceEquals(_lifecycle, lifecycle) ||
            !lifecycle.CanAccessProtectedData ||
            !session.IsActive ||
            cancellation.IsCancellationRequested ||
            !window.TryGetActiveProtectedStorageSession(out ProtectedStorageSessionLease? currentSession) ||
            !ReferenceEquals(currentSession, session))
        {
            return false;
        }

        lock (_clipboardWorkerGate)
        {
            return ReferenceEquals(_clipboardWorkerSession, session) &&
                ReferenceEquals(_clipboardWorkerServices, services) &&
                ReferenceEquals(_clipboardWorkerCancellation, cancellation);
        }
    }

    private void InvalidateClipboardWorker()
    {
        Interlocked.Increment(ref _clipboardWorkerGeneration);
        CancellationTokenSource? cancellation;

        lock (_clipboardWorkerGate)
        {
            cancellation = _clipboardWorkerCancellation;
            _clipboardWorkerCancellation = null;
            _clipboardWorkerSession = null;
            _clipboardWorkerServices = null;
        }

        TryCancelClipboardWorker(cancellation);
    }

    private static void TryCancelClipboardWorker(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Worker completion may win the race and dispose its private CTS first.
        }
        catch (AggregateException)
        {
            // Cancellation callbacks belong to worker dependencies. Their failure must not
            // break lock/close invalidation or prevent a later generation from starting.
        }
    }
}
