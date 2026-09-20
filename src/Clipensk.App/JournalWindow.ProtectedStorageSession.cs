using Clipensk.Core.Application;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    internal event EventHandler? GlobalCapturePolicyInitialized;

    internal bool HasActiveProtectedStorageSession =>
        _protectedStorageSession?.IsActive == true;

    /// <summary>
    /// The persisted Archive rotation thresholds, read on the UI thread like every other
    /// <see cref="ApplicationSettings"/> access in this window. Rotation stays opt-in: no
    /// configured thresholds means startup runs recovery only.
    /// </summary>
    internal ArchiveRotationSettings? ArchiveRotationSettings => _settings.ArchiveRotation;

    /// <summary>
    /// How long an expired external payload stays in Trash before it is permanently deleted.
    /// </summary>
    internal int TrashRetentionDays => _settings.TrashRetentionDays;

    internal bool AutoLockEnabled => _settings.AutoLockEnabled;

    internal int? AutoLockAfterMinutes => _settings.AutoLockAfterMinutes;

    /// <summary>
    /// Locks the protected storage right now, if it is currently unlocked. This is the single
    /// implementation both the manual "Lock now" action and the auto-lock timer call.
    ///
    /// It reuses the existing two-phase <see cref="ProtectedApplicationLifecycle.TryBeginLock"/> /
    /// <see cref="ProtectedApplicationLifecycle.CompleteLock"/> transition, which is what already
    /// revokes the master key: <c>ProtectedDataAccessLease</c> cancels its token the moment
    /// <c>CanAccessProtectedData</c> turns false, and every <c>ProtectedStorageSessionLease</c> is
    /// wired to that same token to zero its key material. Clipboard-runtime teardown and the
    /// lock-screen UI already subscribe to <c>ProtectedDataAccessChanged</c> and react to any
    /// revocation, whatever triggered it, so nothing else needs to happen here.
    ///
    /// Must be called on this window's dispatcher thread.
    /// </summary>
    internal bool TryLockNow()
    {
        if (!_lifecycle.TryBeginLock())
        {
            return false;
        }

        _lifecycle.CompleteLock();
        _protectedStorageSession?.Dispose();
        _protectedStorageSession = null;
        return true;
    }

    internal bool TryGetActiveProtectedStorageSession(
        out ProtectedStorageSessionLease? session)
    {
        session = _protectedStorageSession;
        if (session?.IsActive == true)
        {
            return true;
        }

        session = null;
        return false;
    }

    private void NotifyGlobalCapturePolicyInitialized()
    {
        try
        {
            GlobalCapturePolicyInitialized?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // The policy commit already succeeded. Runtime composition notification is
            // best-effort and must never change the durable operation result.
        }
    }
}
