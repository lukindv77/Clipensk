using System.Security.Cryptography;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Infrastructure.Security;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

string root = Path.Combine(Path.GetTempPath(), "Clipensk.SqlCipher.Smoke", Guid.NewGuid().ToString("N"));
string storageRoot = Path.Combine(root, "Storage");
string passwordRoot = Path.Combine(root, "Password");
Directory.CreateDirectory(storageRoot);
Directory.CreateDirectory(passwordRoot);

// The storage key is the MasterKey followed by the storage salt (docs/CRYPTOGRAPHY.md §3, §6).
byte[] masterKey = RandomNumberGenerator.GetBytes(StorageKeyMaterial.MasterKeyLengthBytes);
byte[] salt = StorageSalt.Create(KeyDerivationProfile.ProductionV1);
byte[] storageKey = StorageKeyMaterial.Compose(masterKey, salt);
byte[] wrongKey = StorageKeyMaterial.Compose(RandomNumberGenerator.GetBytes(StorageKeyMaterial.MasterKeyLengthBytes), salt);
byte[] otherSaltKey = StorageKeyMaterial.Compose(masterKey, StorageSalt.Create(KeyDerivationProfile.ProductionV1));
Guid storageId = Guid.NewGuid();

try
{
    var service = new ProtectedStorageDatabaseService();

    ProtectedStorageDatabaseResult initialized = await service.InitializeOrValidateAsync(
        storageRoot,
        storageId,
        storageKey,
        allowInitialize: true);
    Require(initialized.IsSuccess && initialized.WasInitialized,
        $"Initial SQLCipher storage creation failed: {initialized.Status}");

    string currentPath = Path.Combine(storageRoot, "Current", "current.db");
    string catalogPath = Path.Combine(storageRoot, "Current", "storage-catalog.db");
    RequireEncryptedHeader(currentPath);
    RequireEncryptedHeader(catalogPath);
    RequireSaltHeader(currentPath, salt);
    RequireSaltHeader(catalogPath, salt);

    ProtectedStorageDatabaseResult reopened = await service.InitializeOrValidateAsync(
        storageRoot,
        storageId,
        storageKey,
        allowInitialize: false);
    Require(reopened.IsSuccess && !reopened.WasInitialized,
        $"SQLCipher storage reopen failed: {reopened.Status}");

    ProtectedStorageDatabaseResult wrongPassword = await service.InitializeOrValidateAsync(
        storageRoot,
        storageId,
        wrongKey,
        allowInitialize: false);
    Require(!wrongPassword.IsSuccess,
        "Opening protected storage with an unrelated MasterKey unexpectedly succeeded.");

    ProtectedStorageDatabaseResult otherSalt = await service.InitializeOrValidateAsync(
        storageRoot,
        storageId,
        otherSaltKey,
        allowInitialize: false);
    Require(!otherSalt.IsSuccess,
        "Opening protected storage with the right MasterKey but another salt unexpectedly succeeded.");

    ProtectedStorageIdentityResult identified = await service.IdentifyAsync(storageRoot, storageKey);
    Require(identified.IsIdentified && identified.StorageId == storageId,
        $"The storage key did not identify the storage: {identified.Status}.");
    ProtectedStorageIdentityResult rejected = await service.IdentifyAsync(storageRoot, wrongKey);
    Require(rejected.Status == ProtectedStorageIdentityStatus.KeyRejected,
        $"SQLCipher did not refuse an unrelated MasterKey as SQLITE_NOTADB: {rejected.Status}/{rejected.FailureStatus}.");

    using (SqliteConnection vacuum = new SqlCipherConnectionFactory().Open(
        currentPath,
        storageKey,
        SqliteOpenMode.ReadWrite))
    {
        using SqliteCommand command = vacuum.CreateCommand();
        command.CommandText = "VACUUM;";
        command.ExecuteNonQuery();
    }

    RequireSaltHeader(currentPath, salt);
    ProtectedStorageDatabaseResult afterVacuum = await service.InitializeOrValidateAsync(
        storageRoot,
        storageId,
        storageKey,
        allowInitialize: false);
    Require(afterVacuum.IsSuccess, $"SQLCipher storage did not reopen after VACUUM: {afterVacuum.Status}");

    using var connection = new SqlCipherConnectionFactory().Open(
        currentPath,
        storageKey,
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

    await VerifyPasswordUnlockAsync(service, passwordRoot);

    Console.WriteLine($"SQLCipher smoke PASS. cipher_version={version}; cipher_status={status}; storageId={storageId:D}");
    return 0;
}
finally
{
    CryptographicOperations.ZeroMemory(masterKey);
    CryptographicOperations.ZeroMemory(storageKey);
    CryptographicOperations.ZeroMemory(wrongKey);
    CryptographicOperations.ZeroMemory(otherSaltKey);
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

// The production password path end to end: Argon2id with the production profile, the salt taken
// back from the database headers, the password checked by opening a database.
static async Task VerifyPasswordUnlockAsync(ProtectedStorageDatabaseService service, string dataRoot)
{
    const string password = "Clipensk smoke пароль";
    var credentials = new ProtectedStorageCredentialService(service);

    Require(await credentials.GetStateAsync(dataRoot) == ProtectedStorageCredentialState.Uninitialized,
        "An empty data root was not reported as uninitialized.");
    ProtectedStorageUnlockResult created =
        await credentials.UnlockOrInitializeAsync(dataRoot, password, allowInitialize: true);
    Require(created.IsSuccess && created.IsNewStorage, $"A new storage key was not derived: {created.Status}.");
    using (MasterKeyLease createdKey = created.MasterKey!)
    {
        ProtectedStorageDatabaseResult storage = await service.InitializeOrValidateAsync(
            dataRoot,
            created.StorageId,
            createdKey.DangerousGetMemory(),
            allowInitialize: true);
        Require(storage.IsSuccess && storage.WasInitialized, $"The password storage was not created: {storage.Status}.");
    }

    Require(await credentials.GetStateAsync(dataRoot) == ProtectedStorageCredentialState.Ready,
        "A created storage was not reported as ready.");
    string currentPath = Path.Combine(dataRoot, "Current", "current.db");
    byte[] header = new byte[StorageSalt.LengthBytes];
    Require(StorageDatabaseFiles.TryReadSalt(currentPath, header) &&
            StorageSalt.GetProfileVersion(header) == KeyDerivationProfile.ProductionV1.ProfileVersion,
        "current.db does not carry a salt of the production KDF profile.");

    ProtectedStorageUnlockResult unlocked =
        await credentials.UnlockOrInitializeAsync(dataRoot, password, allowInitialize: false);
    Require(unlocked.IsSuccess && !unlocked.IsNewStorage && unlocked.StorageId == created.StorageId,
        $"The password did not reopen the storage from its database headers: {unlocked.Status}.");
    using (MasterKeyLease unlockedKey = unlocked.MasterKey!)
    {
        ProtectedStorageDatabaseResult storage = await service.InitializeOrValidateAsync(
            dataRoot,
            unlocked.StorageId,
            unlockedKey.DangerousGetMemory(),
            allowInitialize: false);
        Require(storage.IsSuccess, $"The unlocked storage did not validate: {storage.Status}.");
    }

    ProtectedStorageUnlockResult wrong =
        await credentials.UnlockOrInitializeAsync(dataRoot, password + "!", allowInitialize: true);
    Require(wrong.Status == ProtectedStorageUnlockStatus.InvalidPassword && wrong.MasterKey is null,
        $"A wrong password was not refused: {wrong.Status}.");
}

static void RequireEncryptedHeader(string databasePath)
{
    byte[] header = ReadHeader(databasePath);
    byte[] sqliteHeader = "SQLite format 3\0"u8.ToArray();
    if (header.AsSpan().SequenceEqual(sqliteHeader))
    {
        throw new InvalidDataException($"Database {databasePath} has a plaintext SQLite header.");
    }
}

static void RequireSaltHeader(string databasePath, byte[] salt)
{
    if (!ReadHeader(databasePath).AsSpan().SequenceEqual(salt))
    {
        throw new InvalidDataException($"Database {databasePath} does not carry the storage salt in its header.");
    }
}

static byte[] ReadHeader(string databasePath)
{
    byte[] header = new byte[16];
    using FileStream stream = File.OpenRead(databasePath);
    int read = stream.Read(header, 0, header.Length);
    if (read != header.Length)
    {
        throw new InvalidDataException($"Database {databasePath} is too short.");
    }

    return header;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
