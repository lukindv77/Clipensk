using System.Security.Cryptography;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

string root = Path.Combine(Path.GetTempPath(), "Clipensk.SqlCipher.Smoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

byte[] masterKey = RandomNumberGenerator.GetBytes(32);
byte[] wrongKey = RandomNumberGenerator.GetBytes(32);
Guid storageId = Guid.NewGuid();

try
{
    var service = new ProtectedStorageDatabaseService();

    ProtectedStorageDatabaseResult initialized = await service.InitializeOrValidateAsync(
        root,
        storageId,
        masterKey,
        allowInitialize: true);
    Require(initialized.IsSuccess && initialized.WasInitialized,
        $"Initial SQLCipher storage creation failed: {initialized.Status}");

    string currentPath = Path.Combine(root, "Current", "current.db");
    string catalogPath = Path.Combine(root, "Current", "storage-catalog.db");
    RequireEncryptedHeader(currentPath);
    RequireEncryptedHeader(catalogPath);

    ProtectedStorageDatabaseResult reopened = await service.InitializeOrValidateAsync(
        root,
        storageId,
        masterKey,
        allowInitialize: false);
    Require(reopened.IsSuccess && !reopened.WasInitialized,
        $"SQLCipher storage reopen failed: {reopened.Status}");

    ProtectedStorageDatabaseResult wrongPassword = await service.InitializeOrValidateAsync(
        root,
        storageId,
        wrongKey,
        allowInitialize: false);
    Require(!wrongPassword.IsSuccess,
        "Opening protected storage with an unrelated MasterKey unexpectedly succeeded.");

    using var connection = new SqlCipherConnectionFactory().Open(
        currentPath,
        masterKey,
        SqliteOpenMode.ReadOnly);
    using SqliteCommand versionCommand = connection.CreateCommand();
    versionCommand.CommandText = "PRAGMA cipher_version;";
    string version = versionCommand.ExecuteScalar()?.ToString()
        ?? throw new InvalidOperationException("SQLCipher did not report cipher_version.");

    using SqliteCommand statusCommand = connection.CreateCommand();
    statusCommand.CommandText = "PRAGMA cipher_status;";
    string status = statusCommand.ExecuteScalar()?.ToString()
        ?? throw new InvalidOperationException("SQLCipher did not report cipher_status.");
    Require(string.Equals(status, "1", StringComparison.Ordinal),
        $"SQLCipher cipher_status expected 1, got {status}.");

    Console.WriteLine($"SQLCipher smoke PASS. cipher_version={version}; cipher_status={status}; storageId={storageId:D}");
    return 0;
}
finally
{
    CryptographicOperations.ZeroMemory(masterKey);
    CryptographicOperations.ZeroMemory(wrongKey);
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RequireEncryptedHeader(string databasePath)
{
    byte[] header = new byte[16];
    using FileStream stream = File.OpenRead(databasePath);
    int read = stream.Read(header, 0, header.Length);
    if (read != header.Length)
    {
        throw new InvalidDataException($"Database {databasePath} is too short.");
    }

    byte[] sqliteHeader = "SQLite format 3\0"u8.ToArray();
    if (header.AsSpan().SequenceEqual(sqliteHeader))
    {
        throw new InvalidDataException($"Database {databasePath} has a plaintext SQLite header.");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
