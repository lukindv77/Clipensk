using Clipensk.Core.Storage;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    internal event EventHandler? GlobalCapturePolicyMutationStarting;
    internal event EventHandler? GlobalCapturePolicyRefreshRequested;

    internal bool HasActiveProtectedStorageSession =>
        _protectedStorageSession?.IsActive == true;

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

    private void NotifyGlobalCapturePolicyMutationStarting()
    {
        // Cleanup must not begin until the App has had a synchronous opportunity to
        // suspend listener/worker/composition for the current protected runtime.
        GlobalCapturePolicyMutationStarting?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyGlobalCapturePolicyRefreshRequested()
    {
        try
        {
            GlobalCapturePolicyRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // The durable policy operation has already reached its result. Runtime
            // recomposition is best-effort and must not change that result.
        }
    }
}