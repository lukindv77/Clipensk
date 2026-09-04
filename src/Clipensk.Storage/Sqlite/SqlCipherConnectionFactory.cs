using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Clipensk.Storage.Sqlite;

public sealed class SqlCipherConnectionFactory : IKeyedSqliteConnectionFactory
{
    public static readonly Version MinimumSqlCipherVersion = new(4, 12, 0);

    private static readonly object ProviderGate = new();
    private static bool _providerInitialized;

    public SqliteConnection Open(
        string databasePath,
        ReadOnlyMemory<byte> masterKey,
        SqliteOpenMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (masterKey.Length != 32)
        {
            throw new ArgumentException("SQLCipher требует 32-байтовый MasterKey.", nameof(masterKey));
        }

        EnsureProvider();

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = mode,
            Pooling = false,
        }.ToString());

        try
        {
            connection.Open();
            ApplyRawKey(connection, masterKey.Span);

            ExecuteNonQuery(connection, "PRAGMA cipher_compatibility = 4;");
            ExecuteNonQuery(connection, "PRAGMA cipher_memory_security = ON;");

            string? versionText = ExecuteScalarString(connection, "PRAGMA cipher_version;");
            if (!TryParseSqlCipherVersion(versionText, out Version? version) ||
                version < MinimumSqlCipherVersion)
            {
                throw new ProtectedStorageEncryptionUnavailableException(
                    $"Требуется SQLCipher {MinimumSqlCipherVersion} или новее.");
            }

            long cipherStatus = ExecuteScalarInt64(connection, "PRAGMA cipher_status;");
            if (cipherStatus != 1)
            {
                throw new ProtectedStorageEncryptionUnavailableException(
                    "SQLCipher handle не подтвердил активное шифрование.");
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void EnsureProvider()
    {
        if (_providerInitialized)
        {
            return;
        }

        lock (ProviderGate)
        {
            if (_providerInitialized)
            {
                return;
            }

            try
            {
                raw.SetProvider(new SQLite3Provider_sqlcipher());
                _providerInitialized = true;
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or
                EntryPointNotFoundException or
                TypeInitializationException or
                BadImageFormatException)
            {
                throw new ProtectedStorageEncryptionUnavailableException(
                    "Native SQLCipher provider недоступен для текущей платформы.",
                    exception);
            }
        }
    }

    private static void ApplyRawKey(SqliteConnection connection, ReadOnlySpan<byte> masterKey)
    {
        byte[] sqlCipherRawKey = BuildSqlCipherRawKey(masterKey);
        try
        {
            int result = raw.sqlite3_key(connection.Handle, sqlCipherRawKey);
            if (result != raw.SQLITE_OK)
            {
                throw new ProtectedStorageEncryptionUnavailableException(
                    $"SQLCipher отклонил MasterKey, SQLite result={result}.");
            }
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new ProtectedStorageEncryptionUnavailableException(
                "Загруженная SQLite library не предоставляет SQLCipher key API.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sqlCipherRawKey);
        }
    }

    private static byte[] BuildSqlCipherRawKey(ReadOnlySpan<byte> masterKey)
    {
        const string hex = "0123456789ABCDEF";
        byte[] result = new byte[67];
        result[0] = (byte)'x';
        result[1] = (byte)'\'';

        int destination = 2;
        foreach (byte value in masterKey)
        {
            result[destination++] = (byte)hex[value >> 4];
            result[destination++] = (byte)hex[value & 0x0F];
        }

        result[destination] = (byte)'\'';
        return result;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static string? ExecuteScalarString(SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        return command.ExecuteScalar() as string;
    }

    private static long ExecuteScalarInt64(SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private static bool TryParseSqlCipherVersion(string? versionText, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return false;
        }

        string numeric = versionText.Trim().Split(' ', '-', '+')[0];
        return Version.TryParse(numeric, out version);
    }
}
