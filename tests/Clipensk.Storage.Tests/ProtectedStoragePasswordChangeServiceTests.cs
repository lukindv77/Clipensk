using System.Buffers.Binary;
using Clipensk.Core.Security;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

/// <summary>
/// The password change (docs/PASSWORD_CHANGE_PROTOCOL.md). Plain SQLite stands in for SQLCipher:
/// the key a database accepts is recorded as a fingerprint in its header (PRAGMA application_id),
/// which travels with the file through copies and renames the way SQLCipher's encryption does, and
/// a wrong key is refused with SQLITE_NOTADB.
/// </summary>
public sealed class ProtectedStoragePasswordChangeServiceTests
{
    private static readonly DateOnly Today = new(2026, 3, 1);

    [Fact]
    public async Task EveryDatabase_IsReEncrypted_AndTheQuarantineIsDeleted()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: true);
        string quarantine = environment.SeedQuarantine();
        int quarantined = Directory.GetFiles(Path.GetDirectoryName(quarantine)!).Length;
        var progress = new List<PasswordChangeProgress>();

        PasswordChangeResult result = await environment.Service.ChangeAsync(
            environment.Root,
            environment.StorageId,
            environment.OldKey,
            environment.NewKey,
            new SynchronousProgress(progress.Add));

        Assert.Equal(3, result.DatabaseCount);
        Assert.Equal(quarantined, result.QuarantineCopiesDeleted);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(quarantine)!));
        Assert.Equal([1, 2, 3], progress.Select(static update => update.DatabasesReEncrypted));
        foreach (string database in environment.Databases)
        {
            Assert.True(environment.Opens(database, environment.NewKey));
            Assert.False(environment.Opens(database, environment.OldKey));
        }

        Assert.Empty(StorageDatabaseFiles.EnumeratePasswordChangeCopies(environment.Root));
        Assert.True((await new ProtectedStorageDatabaseService(environment.Factory).InitializeOrValidateAsync(
            environment.Root, environment.StorageId, environment.NewKey, allowInitialize: false)).IsSuccess);
    }

    [Fact]
    public async Task CancelledWhileCopying_LeavesEveryDatabaseOnTheOldKey()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: true);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => environment.Service.ChangeAsync(
            environment.Root,
            environment.StorageId,
            environment.OldKey,
            environment.NewKey,
            new SynchronousProgress(_ => cancellation.Cancel()),
            cancellation.Token));

        Assert.Empty(StorageDatabaseFiles.EnumeratePasswordChangeCopies(environment.Root));
        Assert.All(environment.Databases, database => Assert.True(environment.Opens(database, environment.OldKey)));
    }

    [Fact]
    public async Task AWrongCurrentKey_IsRefused_AndLeavesNothingBehind()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: false);

        PasswordChangeRefusedException refusal = await Assert.ThrowsAsync<PasswordChangeRefusedException>(() =>
            environment.Service.ChangeAsync(
                environment.Root, environment.StorageId, environment.NewKey, environment.OtherKey));

        Assert.Equal(PasswordChangeRefusal.DatabaseRefused, refusal.Refusal);
        Assert.Empty(StorageDatabaseFiles.EnumeratePasswordChangeCopies(environment.Root));
        Assert.All(environment.Databases, database => Assert.True(environment.Opens(database, environment.OldKey)));
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("copy")]
    [InlineData("journal")]
    public async Task AnUnfinishedOperation_IsRefusedBeforeAnyWrite(string trace)
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: false);
        string path = trace switch
        {
            "staging" => Path.Combine(environment.Root, "Archive", ".clipensk-archive-rotation-1"),
            "copy" => environment.CurrentPath + StorageDatabaseFiles.PasswordChangeCopySuffix,
            _ => environment.CatalogPath + "-journal",
        };
        if (trace == "staging")
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            File.WriteAllBytes(path, [1]);
        }

        PasswordChangeRefusedException refusal = await Assert.ThrowsAsync<PasswordChangeRefusedException>(() =>
            environment.Service.ChangeAsync(environment.Root, environment.StorageId, environment.OldKey, environment.NewKey));

        Assert.Equal(PasswordChangeRefusal.PendingOperation, refusal.Refusal);
        Assert.Empty(StorageDatabaseFiles.EnumeratePasswordChangeCopies(environment.Root).Where(copy => copy != path));

        // The planted trace is not a real journal; remove it before SQLite would replay it.
        if (trace == "journal")
        {
            File.Delete(path);
        }

        Assert.All(environment.Databases, database => Assert.True(environment.Opens(database, environment.OldKey)));
    }

    [Fact]
    public async Task LeftoversOfAbandonedOperations_AreRemoved_AndTheChangeProceeds()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: false);
        string restart = Path.Combine(environment.Root, "Current", ".clipensk-current-restart-1.tmp");
        string initialization = Path.Combine(environment.Root, ".clipensk-storage-init-1");
        File.Copy(environment.CurrentPath, restart);
        Directory.CreateDirectory(Path.Combine(initialization, "Current"));
        File.Copy(environment.CatalogPath, Path.Combine(initialization, "Current", "storage-catalog.db"));

        await environment.Service.ChangeAsync(environment.Root, environment.StorageId, environment.OldKey, environment.NewKey);

        Assert.False(File.Exists(restart));
        Assert.False(Directory.Exists(initialization));
        Assert.All(environment.Databases, database => Assert.True(environment.Opens(database, environment.NewKey)));
    }

    [Fact]
    public async Task AnUnknownLeftover_IsRefused()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: false);
        File.WriteAllBytes(Path.Combine(environment.Root, "Current", ".clipensk-something-else"), [1]);

        PasswordChangeRefusedException refusal = await Assert.ThrowsAsync<PasswordChangeRefusedException>(() =>
            environment.Service.ChangeAsync(environment.Root, environment.StorageId, environment.OldKey, environment.NewKey));

        Assert.Equal(PasswordChangeRefusal.PendingOperation, refusal.Refusal);
    }

    [Fact]
    public async Task ADatabasePublishedWhileCopying_UndoesTheCopies()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: false);
        string late = Path.Combine(environment.Root, "Archive", "archive_000099.db");
        Directory.CreateDirectory(Path.GetDirectoryName(late)!);
        bool published = false;

        PasswordChangeRefusedException refusal = await Assert.ThrowsAsync<PasswordChangeRefusedException>(() =>
            environment.Service.ChangeAsync(
                environment.Root,
                environment.StorageId,
                environment.OldKey,
                environment.NewKey,
                new SynchronousProgress(_ =>
                {
                    if (!published)
                    {
                        // An operation finishing after the lock renames a new archive into place.
                        File.WriteAllBytes(late, [1, 2, 3]);
                        published = true;
                    }
                })));

        Assert.Equal(PasswordChangeRefusal.DatabaseBusy, refusal.Refusal);
        Assert.Empty(StorageDatabaseFiles.EnumeratePasswordChangeCopies(environment.Root));
        Assert.True(environment.Opens(environment.CurrentPath, environment.OldKey));
        Assert.True(environment.Opens(environment.CatalogPath, environment.OldKey));
    }

    [Fact]
    public async Task SameKey_InsufficientSpace_AndABusyDatabase_AreRefused()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: false);

        Assert.Equal(PasswordChangeRefusal.SameKey, (await Assert.ThrowsAsync<PasswordChangeRefusedException>(() =>
            environment.Service.ChangeAsync(environment.Root, environment.StorageId, environment.OldKey, environment.OldKey))).Refusal);

        var cramped = new ProtectedStoragePasswordChangeService(environment.Factory, _ => 1024);
        Assert.Equal(PasswordChangeRefusal.InsufficientSpace, (await Assert.ThrowsAsync<PasswordChangeRefusedException>(() =>
            cramped.ChangeAsync(environment.Root, environment.StorageId, environment.OldKey, environment.NewKey))).Refusal);

        using (new FileStream(environment.CatalogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(PasswordChangeRefusal.DatabaseBusy, (await Assert.ThrowsAsync<PasswordChangeRefusedException>(() =>
                environment.Service.ChangeAsync(environment.Root, environment.StorageId, environment.OldKey, environment.NewKey))).Refusal);
        }

        Assert.Empty(StorageDatabaseFiles.EnumeratePasswordChangeCopies(environment.Root));
    }

    [Fact]
    public async Task InterruptedCopying_TheOldPassword_RollsBack()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: true);
        environment.CopyWithKey(environment.CurrentPath, environment.NewKey);

        PasswordChangeRecoveryOutcome outcome =
            await environment.Service.ResolvePendingAsync(environment.Root, environment.OldKey);

        Assert.Equal(PasswordChangeRecoveryOutcome.RolledBack, outcome);
        Assert.Empty(StorageDatabaseFiles.EnumeratePasswordChangeCopies(environment.Root));
        Assert.All(environment.Databases, database => Assert.True(environment.Opens(database, environment.OldKey)));
    }

    [Fact]
    public async Task ACopyWhoseDatabaseIsGone_IsNeverDeleted()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: false);
        environment.CopyWithKey(environment.CurrentPath, environment.NewKey);
        string orphan = Path.Combine(environment.Root, "Archive", "archive_000050.db") + StorageDatabaseFiles.PasswordChangeCopySuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.Copy(environment.CatalogPath, orphan);

        Assert.Equal(
            PasswordChangeRecoveryOutcome.RolledBack,
            await environment.Service.ResolvePendingAsync(environment.Root, environment.OldKey));

        Assert.False(File.Exists(environment.CurrentPath + StorageDatabaseFiles.PasswordChangeCopySuffix));
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public async Task InterruptedCopying_TheNewPassword_IsNotYetValid_AndTouchesNothing()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: true);
        environment.CopyWithKey(environment.CurrentPath, environment.NewKey);
        environment.CopyWithKey(environment.CatalogPath, environment.NewKey);

        PasswordChangeRecoveryOutcome outcome =
            await environment.Service.ResolvePendingAsync(environment.Root, environment.NewKey);

        Assert.Equal(PasswordChangeRecoveryOutcome.OldPasswordStillValid, outcome);
        Assert.Equal(2, StorageDatabaseFiles.EnumeratePasswordChangeCopies(environment.Root).Count);
        Assert.All(environment.Databases, database => Assert.True(environment.Opens(database, environment.OldKey)));
    }

    [Fact]
    public async Task EveryCopyReady_TheNewPassword_FinishesTheChange()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: true);
        foreach (string database in environment.Databases)
        {
            environment.CopyWithKey(database, environment.NewKey);
        }

        PasswordChangeRecoveryOutcome outcome =
            await environment.Service.ResolvePendingAsync(environment.Root, environment.NewKey);

        Assert.Equal(PasswordChangeRecoveryOutcome.Completed, outcome);
        Assert.Empty(StorageDatabaseFiles.EnumeratePasswordChangeCopies(environment.Root));
        Assert.All(environment.Databases, database => Assert.True(environment.Opens(database, environment.NewKey)));
    }

    [Fact]
    public async Task InterruptedSwitch_FinishesWithTheNewPassword_AndRefusesTheOldOne()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: true);
        string archive = environment.Databases.Single(static path => path.Contains("Archive", StringComparison.Ordinal));
        environment.CopyWithKey(archive, environment.NewKey);
        File.Move(archive + StorageDatabaseFiles.PasswordChangeCopySuffix, archive, overwrite: true);
        environment.CopyWithKey(environment.CatalogPath, environment.NewKey);
        environment.CopyWithKey(environment.CurrentPath, environment.NewKey);
        string quarantine = environment.SeedQuarantine();

        Assert.Equal(
            PasswordChangeRecoveryOutcome.NewPasswordRequired,
            await environment.Service.ResolvePendingAsync(environment.Root, environment.OldKey));
        Assert.Equal(2, StorageDatabaseFiles.EnumeratePasswordChangeCopies(environment.Root).Count);

        Assert.Equal(
            PasswordChangeRecoveryOutcome.Completed,
            await environment.Service.ResolvePendingAsync(environment.Root, environment.NewKey));
        Assert.Empty(StorageDatabaseFiles.EnumeratePasswordChangeCopies(environment.Root));
        Assert.False(File.Exists(quarantine));
        Assert.All(environment.Databases, database => Assert.True(environment.Opens(database, environment.NewKey)));
    }

    [Fact]
    public async Task AHalfWrittenCopy_IsNotTakenForAFinishedOne()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: false);
        environment.CopyWithKey(environment.CurrentPath, environment.NewKey);
        // Cut short after its first page: the header (and the key) is there, the rest is not.
        environment.CopyWithKey(environment.CatalogPath, environment.NewKey);
        using (var copy = new FileStream(
                   environment.CatalogPath + StorageDatabaseFiles.PasswordChangeCopySuffix,
                   FileMode.Open,
                   FileAccess.ReadWrite))
        {
            copy.SetLength(4096);
        }

        Assert.Equal(
            PasswordChangeRecoveryOutcome.OldPasswordStillValid,
            await environment.Service.ResolvePendingAsync(environment.Root, environment.NewKey));
        Assert.Equal(
            PasswordChangeRecoveryOutcome.RolledBack,
            await environment.Service.ResolvePendingAsync(environment.Root, environment.OldKey));
        Assert.Empty(StorageDatabaseFiles.EnumeratePasswordChangeCopies(environment.Root));
    }

    [Fact]
    public async Task NoCopies_NothingIsPending()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: false);

        Assert.Equal(
            PasswordChangeRecoveryOutcome.NothingPending,
            await environment.Service.ResolvePendingAsync(environment.Root, environment.NewKey));
    }

    [Fact]
    public async Task TheCopiesOfAnInterruptedChange_LetTheNewPasswordIdentifyTheStorage()
    {
        using PasswordEnvironment environment = await PasswordEnvironment.CreateAsync(withArchive: false);
        foreach (string database in environment.Databases)
        {
            environment.CopyWithKey(database, environment.NewKey);
        }

        ProtectedStorageIdentityResult identity = await new ProtectedStorageDatabaseService(environment.Factory)
            .IdentifyAsync(environment.Root, environment.NewKey);

        Assert.True(identity.IsIdentified);
        Assert.Equal(environment.StorageId, identity.StorageId);
    }

    private sealed class PasswordEnvironment : IDisposable
    {
        private static readonly byte[] Salt = "SQLite format 3\0"u8.ToArray();

        private readonly GlobalPolicyTestEnvironment _inner;

        private PasswordEnvironment(GlobalPolicyTestEnvironment inner)
        {
            _inner = inner;
            Service = new ProtectedStoragePasswordChangeService(Factory, _ => long.MaxValue);
        }

        public FingerprintConnectionFactory Factory { get; } = new();

        public ProtectedStoragePasswordChangeService Service { get; }

        public string Root => _inner.Root;

        public Guid StorageId => _inner.StorageId;

        public string CurrentPath => _inner.CurrentPath;

        public string CatalogPath => _inner.CatalogPath;

        public byte[] OldKey { get; } = Key(0x11);

        public byte[] NewKey { get; } = Key(0x22);

        public byte[] OtherKey { get; } = Key(0x33);

        public IReadOnlyList<string> Databases => StorageDatabaseFiles.EnumerateExisting(Root);

        public static async Task<PasswordEnvironment> CreateAsync(bool withArchive)
        {
            GlobalPolicyTestEnvironment inner = await GlobalPolicyTestEnvironment.CreateAsync();
            if (withArchive)
            {
                SeedDay(inner, new DateOnly(2026, 2, 20));
                SeedDay(inner, new DateOnly(2026, 2, 21));
                ProtectedStorageStartupResult rotation = await new ProtectedStorageStartupRecoveryCoordinator(
                        inner.Session,
                        inner.Factory)
                    .RunAsync(Today, new ArchiveRotationSettings { MaxCalendarDays = 2 }, trashRetentionDays: null);
                Assert.Single(rotation.StartedRotation!.Targets);
            }

            inner.Session.Dispose();
            var environment = new PasswordEnvironment(inner);
            foreach (string database in environment.Databases)
            {
                environment.SetKey(database, environment.OldKey);
            }

            return environment;
        }

        public bool Opens(string path, byte[] key)
        {
            try
            {
                using SqliteConnection connection = Factory.Open(path, key, SqliteOpenMode.ReadOnly);
                return true;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 26)
            {
                return false;
            }
        }

        /// <summary>A copy of <paramref name="database"/> re-encrypted with <paramref name="key"/>, as phase 1 leaves it.</summary>
        public void CopyWithKey(string database, byte[] key)
        {
            string copy = database + StorageDatabaseFiles.PasswordChangeCopySuffix;
            File.Copy(database, copy);
            SetKey(copy, key);
        }

        public string SeedQuarantine()
        {
            string directory = Path.Combine(Root, "Current", CatalogQuarantineFileName.DirectoryName);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, CatalogQuarantineFileName.Create(DateTimeOffset.UtcNow, Guid.NewGuid()));
            File.WriteAllBytes(path, [9, 9, 9]);
            return path;
        }

        public void Dispose() => _inner.Dispose();

        private void SetKey(string path, byte[] key)
        {
            using SqliteConnection connection = Factory.OpenWithoutKeyCheck(path);
            Factory.Rekey(connection, key);
        }

        private static byte[] Key(byte fill) =>
            StorageKeyMaterial.Compose(Enumerable.Repeat(fill, StorageKeyMaterial.MasterKeyLengthBytes).ToArray(), Salt);

        private static void SeedDay(GlobalPolicyTestEnvironment environment, DateOnly day)
        {
            string eventId = Guid.NewGuid().ToString("D");
            environment.Execute($"""
                INSERT INTO ClipboardHistoryEvent (
                    EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                    SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
                VALUES ('{eventId}', '{day:yyyy-MM-dd}T09:00:00.0000000+00:00', 0, 'UTC', '{day:yyyy-MM-dd}',
                        NULL, NULL, NULL, NULL);
                INSERT INTO ClipboardHistoryPayload (
                    EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                    InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath, ExternalSizeBytes)
                VALUES ('{eventId}', 0, 'Text', 'Text', 4, 'text', 'text', NULL, NULL, NULL);
                """);
        }
    }

    /// <summary>
    /// Plain SQLite whose "encryption key" is a fingerprint in the header: a database with a
    /// fingerprint opens only with the key it came from.
    /// </summary>
    private sealed class FingerprintConnectionFactory : IKeyedSqliteConnectionFactory
    {
        private readonly GlobalPolicyTestEnvironment.TestConnectionFactory _plain = new();

        public SqliteConnection Open(string databasePath, ReadOnlyMemory<byte> masterKey, SqliteOpenMode mode)
        {
            SqliteConnection connection = _plain.Open(databasePath, masterKey, mode);
            try
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "PRAGMA application_id;";
                int fingerprint = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                if (fingerprint != 0 && fingerprint != Fingerprint(masterKey.Span))
                {
                    throw new SqliteException("file is not a database", 26);
                }

                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public SqliteConnection OpenWithoutKeyCheck(string databasePath) =>
            _plain.Open(databasePath, ReadOnlyMemory<byte>.Empty, SqliteOpenMode.ReadWrite);

        public void Rekey(SqliteConnection connection, ReadOnlyMemory<byte> newStorageKey)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA application_id = {Fingerprint(newStorageKey.Span)};";
            command.ExecuteNonQuery();
        }

        private static int Fingerprint(ReadOnlySpan<byte> storageKey) =>
            BinaryPrimitives.ReadInt32LittleEndian(StorageKeyMaterial.GetMasterKey(storageKey)) | 1;
    }

    private sealed class SynchronousProgress(Action<PasswordChangeProgress> report) : IProgress<PasswordChangeProgress>
    {
        public void Report(PasswordChangeProgress value) => report(value);
    }
}
