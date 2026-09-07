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
        CancellationToken workerToken;
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
            workerToken = cancellation.Token;

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
                    workerToken,
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
        CancellationToken workerToken,
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
                    workerToken,
                    generation))
            {
                return;
            }

            // The new generation is now the only queue reader. Start the Win32 listener on
            // the window dispatcher only after this point so no new request can wait behind
            // a previous session and later resolve source application metadata too late.
            window.DispatcherQueue.TryEnqueue(() =>
            {
                if (IsCurrentClipboardWorker(
                        window,
                        host,
                        lifecycle,
                        session,
                        services,
                        cancellation,
                        workerToken,
                        generation))
                {
                    TryStartClipboardMonitoringForSession(
                        host,
                        window,
                        lifecycle,
                        session);
                }
            });

            var worker = new ClipboardAcceptedCaptureWorker(services.Delivery);
            await worker.RunAsync(workerToken).ConfigureAwait(false);
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
        CancellationToken workerToken,
        long generation)
    {
        if (generation != Volatile.Read(ref _clipboardWorkerGeneration) ||
            !ReferenceEquals(_window, window) ||
            !ReferenceEquals(_residentWindowsHost, host) ||
            !ReferenceEquals(_lifecycle, lifecycle) ||
            !lifecycle.CanAccessProtectedData ||
            !session.IsActive ||
            workerToken.IsCancellationRequested ||
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
