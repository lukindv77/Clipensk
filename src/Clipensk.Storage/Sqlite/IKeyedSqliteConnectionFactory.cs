using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Sqlite;

public interface IKeyedSqliteConnectionFactory
{
    SqliteConnection Open(
        string databasePath,
        ReadOnlyMemory<byte> masterKey,
        SqliteOpenMode mode);

    /// <summary>
    /// Re-encrypts every page of an open database with another storage key (same salt), in place
    /// (<c>docs/PASSWORD_CHANGE_PROTOCOL.md</c> §4). Only a factory that encrypts can do this.
    /// </summary>
    void Rekey(SqliteConnection connection, ReadOnlyMemory<byte> newStorageKey) =>
        throw new NotSupportedException("This connection factory cannot re-encrypt a database.");
}

public sealed class ProtectedStorageEncryptionUnavailableException : Exception
{
    public ProtectedStorageEncryptionUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
