using System.Collections.Concurrent;

namespace Clipensk.Windows.Clipboard;

/// <summary>
/// The one thread Clipensk reads the clipboard on. The WinRT clipboard hands out objects bound to
/// the single-threaded apartment that asked for them, while the resident capture worker runs on the
/// thread pool (a multithreaded apartment), where every call on them fails with
/// RPC_E_WRONG_THREAD (0x8001010E) — shown on Windows by <c>tools/Clipensk.Clipboard.Probe</c>.
/// Work posted here runs on a dedicated STA thread, and its awaits resume there, so a whole read
/// (the snapshot, then the content of each format) stays in one apartment. It is not the UI thread:
/// reading a large image does not stall the window.
/// </summary>
internal sealed class ClipboardStaThread : IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public ClipboardStaThread()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Clipensk clipboard",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Runs <paramref name="work"/> on the clipboard thread; its awaits resume there.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (Thread.CurrentThread == _thread)
        {
            return work();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool posted = TryPost(
            async _ =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(await work());
                }
                catch (OperationCanceledException exception)
                {
                    completion.TrySetCanceled(exception.CancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            },
            null);

        return posted
            ? completion.Task
            : Task.FromException<T>(new ObjectDisposedException(nameof(ClipboardStaThread)));
    }

    /// <summary>Runs synchronous <paramref name="work"/> on the clipboard thread and waits for it.</summary>
    public T Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(() => Task.FromResult(work())).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private bool TryPost(SendOrPostCallback callback, object? state)
    {
        try
        {
            _queue.Add((callback, state));
            return true;
        }
        catch (InvalidOperationException)
        {
            // Disposed: the queue no longer accepts work.
            return false;
        }
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new ClipboardSynchronizationContext(this));
        foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
        {
            try
            {
                callback(state);
            }
            catch
            {
                // Posted work reports its own outcome through its task; nothing may stop the loop.
            }
        }
    }

    private sealed class ClipboardSynchronizationContext(ClipboardStaThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!owner.TryPost(d, state))
            {
                // A continuation that outlived the thread still has to run somewhere.
                ThreadPool.QueueUserWorkItem(static item => item.Callback(item.State), (Callback: d, State: state), preferLocal: false);
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Thread.CurrentThread != owner._thread)
            {
                throw new NotSupportedException("The clipboard thread does not run work synchronously for other threads.");
            }

            d(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
