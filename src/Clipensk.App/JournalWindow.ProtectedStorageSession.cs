namespace Clipensk.App;

public sealed partial class JournalWindow
{
    internal bool HasActiveProtectedStorageSession =>
        _protectedStorageSession?.IsActive == true;
}
