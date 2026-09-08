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
    private int _clipboardRuntimeSuspended;

    private bool IsClipboardRuntimeSuspended =>
        Volatile.Read(ref _clipboardRuntimeSuspended) != 0;

    private void RequestClipboardWorkerStart(
        JournalWindow window,
        ResidentWindowsHost host,
        ProtectedApplicationLifecycle lifecycle,
        ProtectedStorageSessionLease session,
        ProtectedClipboardDeliveryServices services)
    {
        if (IsClipboardRuntimeSuspended ||
            !lifecycle.CanAccessProtectedData ||
            !session.IsActive)
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
            if (IsClipboardRuntimeSuspended)
            {
                return;
            }

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
        if (IsClipboardRuntimeSuspended ||
            generation != Volatile.Read(ref _clipboardWorkerGeneration) ||
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
            return !IsClipboardRuntimeSuspended &&
                ReferenceEquals(_clipboardWorkerSession, session) &&
                ReferenceEquals(_clipboardWorkerServices, services) &&
                ReferenceEquals(_clipboardWorkerCancellation, cancellation);
        }
    }

    /// <summary>
    /// Stops accepting new clipboard updates, revokes the current worker generation and does not
    /// return a successful quiesced state until the exact previous worker task has completed.
    /// The suspended state intentionally remains active until an explicit resume or a protected
    /// lifecycle reset; future destructive maintenance can therefore fail closed after errors.
    /// </summary>
    private async Task<bool> TryQuiesceClipboardRuntimeAsync(
        JournalWindow window,
        ResidentWindowsHost host,
        ProtectedApplicationLifecycle lifecycle,
        ProtectedStorageSessionLease session,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            return false;
        }

        // Suspension has a single in-memory owner. A concurrent maintenance request must not
        // share this state because either caller could otherwise resume capture while the other
        // still assumes a quiesced runtime.
        if (Interlocked.CompareExchange(ref _clipboardRuntimeSuspended, 1, 0) != 0)
        {
            return false;
        }

        if (!IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            Interlocked.CompareExchange(ref _clipboardRuntimeSuspended, 0, 1);
            return false;
        }

        // Prevent an already-running composition from publishing after suspension. New requests
        // are rejected by the suspension gate before they can create another worker generation.
        InvalidateClipboardDeliveryComposition();

        // Stop first so the current capture epoch is invalidated before the worker drain. A Win32
        // callback racing Stop can at worst leave an old-epoch request, which the queue discards.
        TrySetClipboardMonitoring(host, start: false);

        Task workerCompletion = InvalidateClipboardWorkerAndGetCompletion();
        try
        {
            await workerCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Suspension stays asserted. A cancelled maintenance caller must not accidentally
            // reactivate capture while the old worker completion is still unknown to that caller.
            throw;
        }
        catch
        {
            // A faulted previous worker is still completed and therefore drained. Maintenance may
            // proceed if the same protected session still owns the runtime boundary.
        }

        cancellationToken.ThrowIfCancellationRequested();
        return IsCurrentProtectedStorageSession(host, window, lifecycle, session);
    }

    /// <summary>
    /// Explicitly leaves maintenance suspension and requests a fresh composition from persisted
    /// state. No old delivery graph is reused.
    /// </summary>
    private bool TryResumeClipboardRuntimeAfterMaintenance(
        JournalWindow window,
        ResidentWindowsHost host,
        ProtectedApplicationLifecycle lifecycle,
        ProtectedStorageSessionLease session)
    {
        if (!IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _clipboardRuntimeSuspended, 0, 1) != 1)
        {
            return false;
        }

        if (!IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            return false;
        }

        RequestClipboardDeliveryComposition(window, host);
        return true;
    }

    private void ResetClipboardRuntimeSuspension()
    {
        Interlocked.Exchange(ref _clipboardRuntimeSuspended, 0);
    }

    private void InvalidateClipboardWorker()
    {
        _ = InvalidateClipboardWorkerAndGetCompletion();
    }

    private Task InvalidateClipboardWorkerAndGetCompletion()
    {
        Interlocked.Increment(ref _clipboardWorkerGeneration);
        CancellationTokenSource? cancellation;
        Task workerCompletion;

        lock (_clipboardWorkerGate)
        {
            workerCompletion = _clipboardWorkerTask;
            cancellation = _clipboardWorkerCancellation;
            _clipboardWorkerCancellation = null;
            _clipboardWorkerSession = null;
            _clipboardWorkerServices = null;
        }

        TryCancelClipboardWorker(cancellation);
        return workerCompletion;
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
