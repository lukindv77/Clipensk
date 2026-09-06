using Clipensk.Core.Storage;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
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
}
