using Clipensk.Core.Application;
using Clipensk.Core.Security;

namespace Clipensk.Core.Storage;

public sealed class ProtectedStorageSessionLease : IDisposable
{
    private readonly object _gate = new();
    private readonly ProtectedDataAccessLease _accessLease;
    private readonly CancellationTokenRegistration _accessRevokedRegistration;
    private MasterKeyLease? _masterKeyLease;
    private bool _disposed;

    private ProtectedStorageSessionLease(
        string dataRootPath,
        Guid storageId,
        MasterKeyLease masterKeyLease,
        ProtectedDataAccessLease accessLease)
    {
        DataRootPath = dataRootPath;
        StorageId = storageId;
        _masterKeyLease = masterKeyLease;
        _accessLease = accessLease;
        _accessRevokedRegistration = _accessLease.CancellationToken.Register(
            static state => ((ProtectedStorageSessionLease)state!).RevokeMasterKey(),
            this);
    }

    public string DataRootPath { get; }

    public Guid StorageId { get; }

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return !_disposed && _masterKeyLease is not null;
            }
        }
    }

    public CancellationToken CancellationToken => _accessLease.CancellationToken;

    public static ProtectedStorageSessionLease Create(
        ProtectedApplicationLifecycle lifecycle,
        string dataRootPath,
        Guid storageId,
        MasterKeyLease masterKeyLease)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        ArgumentNullException.ThrowIfNull(masterKeyLease);
        if (storageId == Guid.Empty)
        {
            throw new ArgumentException("StorageId не может быть пустым.", nameof(storageId));
        }

        ProtectedDataAccessLease? accessLease = null;
        try
        {
            if (!ProtectedDataAccessLease.TryAcquire(lifecycle, out accessLease) || accessLease is null)
            {
                throw new InvalidOperationException(
                    "Protected storage session можно создать только при доступе к защищённым данным.");
            }

            var session = new ProtectedStorageSessionLease(
                dataRootPath,
                storageId,
                masterKeyLease,
                accessLease);
            accessLease = null;

            if (!session.HasMasterKey())
            {
                session.Dispose();
                throw new InvalidOperationException(
                    "Protected data access был отозван во время создания storage session.");
            }

            return session;
        }
        catch
        {
            accessLease?.Dispose();
            masterKeyLease.Dispose();
            throw;
        }
    }

    public ReadOnlyMemory<byte> DangerousGetMasterKeyMemory()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _masterKeyLease?.DangerousGetMemory()
                ?? throw new InvalidOperationException(
                    "MasterKey уже отозван вместе с protected data access.");
        }
    }

    public void Dispose()
    {
        MasterKeyLease? masterKeyLease;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            masterKeyLease = _masterKeyLease;
            _masterKeyLease = null;
        }

        try
        {
            _accessRevokedRegistration.Dispose();
            _accessLease.Dispose();
        }
        finally
        {
            masterKeyLease?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private bool HasMasterKey()
    {
        lock (_gate)
        {
            return !_disposed && _masterKeyLease is not null;
        }
    }

    private void RevokeMasterKey()
    {
        MasterKeyLease? masterKeyLease;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            masterKeyLease = _masterKeyLease;
            _masterKeyLease = null;
        }

        masterKeyLease?.Dispose();
    }
}
