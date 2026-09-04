using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Sqlite;

public interface IKeyedSqliteConnectionFactory
{
    SqliteConnection Open(
        string databasePath,
        ReadOnlyMemory<byte> masterKey,
        SqliteOpenMode mode);
}

public sealed class ProtectedStorageEncryptionUnavailableException : Exception
{
    public ProtectedStorageEncryptionUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
