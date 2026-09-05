namespace Clipensk.Core.Application;

public sealed class ProtectedDataAccessLease : IDisposable
{
    private readonly object _gate = new();
    private readonly ProtectedApplicationLifecycle _lifecycle;
    private CancellationTokenSource? _cancellation = new();
    private bool _protectedAccessRevoked;
    private bool _disposed;

    private ProtectedDataAccessLease(ProtectedApplicationLifecycle lifecycle)
    {
        _lifecycle = lifecycle;
        _lifecycle.ProtectedDataAccessChanged += OnProtectedDataAccessChanged;

        lock (_gate)
        {
            if (!_lifecycle.CanAccessProtectedData)
            {
                RevokeProtectedAccessUnderLock();
            }
        }
    }

    public CancellationToken CancellationToken
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _cancellation!.Token;
            }
        }
    }

    public static bool TryAcquire(
        ProtectedApplicationLifecycle lifecycle,
        out ProtectedDataAccessLease? lease)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        var candidate = new ProtectedDataAccessLease(lifecycle);
        if (!candidate.HasActiveProtectedAccess())
        {
            candidate.Dispose();
            lease = null;
            return false;
        }

        lease = candidate;
        return true;
    }

    public void Dispose()
    {
        _lifecycle.ProtectedDataAccessChanged -= OnProtectedDataAccessChanged;

        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _cancellation;
            _cancellation = null;
        }

        if (cancellation is not null)
        {
            try
            {
                cancellation.Cancel();
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }

    private bool HasActiveProtectedAccess()
    {
        lock (_gate)
        {
            return !_disposed &&
                !_protectedAccessRevoked &&
                _lifecycle.CanAccessProtectedData;
        }
    }

    private void OnProtectedDataAccessChanged(bool canAccessProtectedData)
    {
        if (canAccessProtectedData)
        {
            return;
        }

        lock (_gate)
        {
            if (!_disposed)
            {
                RevokeProtectedAccessUnderLock();
            }
        }
    }

    private void RevokeProtectedAccessUnderLock()
    {
        _protectedAccessRevoked = true;
        _cancellation!.Cancel();
    }
}
