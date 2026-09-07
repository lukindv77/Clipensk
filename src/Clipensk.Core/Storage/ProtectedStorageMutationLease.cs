namespace Clipensk.Core.Storage;

public sealed class ProtectedStorageMutationLease : IDisposable
{
    private SemaphoreSlim? _gate;

    internal ProtectedStorageMutationLease(SemaphoreSlim gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public void Dispose()
    {
        SemaphoreSlim? gate = Interlocked.Exchange(ref _gate, null);
        if (gate is null)
        {
            return;
        }

        gate.Release();
        GC.SuppressFinalize(this);
    }
}
