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
