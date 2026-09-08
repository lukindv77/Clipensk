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
    private long _clipboardRuntimeSuspensionOwner;
    private long _clipboardRuntimeSuspensionSequence;

    private bool IsClipboardRuntimeSuspended =>
        Volatile.Read(ref _clipboardRuntimeSuspensionOwner) != 0;

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
    /// return a suspension owner token until the exact previous worker task has completed.
    /// The returned token uniquely owns this in-memory suspension and must be supplied to resume.
    /// </summary>
    private async Task<long?> TryQuiesceClipboardRuntimeAsync(
        JournalWindow window,
        ResidentWindowsHost host,
        ProtectedApplicationLifecycle lifecycle,
        ProtectedStorageSessionLease session,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            return null;
        }

        long suspensionOwner = NextClipboardRuntimeSuspensionOwner();
        if (Interlocked.CompareExchange(
                ref _clipboardRuntimeSuspensionOwner,
                suspensionOwner,
                comparand: 0) != 0)
        {
            return null;
        }

        if (!IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            TryReleaseClipboardRuntimeSuspension(suspensionOwner);
            return null;
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
            // Once suspension owns the runtime, cancellation cannot safely make this method return
            // before the old worker is known to be finished. The worker itself has already been
            // cancelled; drain completion is therefore an unconditional safety boundary.
            await workerCompletion.ConfigureAwait(false);
        }
        catch
        {
            // A faulted previous worker is still completed and therefore drained. Maintenance may
            // proceed if the same protected session still owns the runtime boundary.
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Maintenance has not started yet because quiesce has not returned. Releasing this
            // exact owner and requesting fresh composition is therefore safe on caller cancel.
            if (TryReleaseClipboardRuntimeSuspension(suspensionOwner) &&
                IsCurrentProtectedStorageSession(host, window, lifecycle, session))
            {
                RequestClipboardDeliveryComposition(window, host);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            TryReleaseClipboardRuntimeSuspension(suspensionOwner);
            return null;
        }

        return suspensionOwner;
    }

    /// <summary>
    /// Releases only the exact maintenance suspension owner and requests a fresh composition from
    /// persisted state when the original protected session is still current. No old graph is reused.
    /// </summary>
    private bool TryResumeClipboardRuntimeAfterMaintenance(
        JournalWindow window,
        ResidentWindowsHost host,
        ProtectedApplicationLifecycle lifecycle,
        ProtectedStorageSessionLease session,
        long suspensionOwner)
    {
        if (!TryReleaseClipboardRuntimeSuspension(suspensionOwner))
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

    private long NextClipboardRuntimeSuspensionOwner()
    {
        long owner;
        do
        {
            owner = Interlocked.Increment(ref _clipboardRuntimeSuspensionSequence);
        }
        while (owner == 0);

        return owner;
    }

    private bool TryReleaseClipboardRuntimeSuspension(long suspensionOwner)
    {
        return suspensionOwner != 0 &&
            Interlocked.CompareExchange(
                ref _clipboardRuntimeSuspensionOwner,
                value: 0,
                comparand: suspensionOwner) == suspensionOwner;
    }

    private void ResetClipboardRuntimeSuspension()
    {
        Interlocked.Exchange(ref _clipboardRuntimeSuspensionOwner, 0);
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
