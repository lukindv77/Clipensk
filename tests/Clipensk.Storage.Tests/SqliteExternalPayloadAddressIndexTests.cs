using System.Security.Cryptography;
using Clipensk.Core.Application;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqliteExternalPayloadAddressIndexTests
{
    [Fact]
    public async Task GetOrAddAsync_DuplicateShaKeepsFirstPersistedPath()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        var index = new SqliteExternalPayloadAddressIndex(
            environment.Session,
            environment.Factory);
        string sha256 = new string('a', 64);
        var first = new ExternalPayloadAddress(
            sha256,
            Path.Combine("2026-09-01", sha256 + ".png"),
            123);
        var laterCandidate = new ExternalPayloadAddress(
            sha256,
            Path.Combine("2026-09-06", sha256 + ".png"),
            123);

        ExternalPayloadAddress storedFirst = await index.GetOrAddAsync(first);
        ExternalPayloadAddress storedDuplicate = await index.GetOrAddAsync(laterCandidate);
        ExternalPayloadAddress? found = await index.FindAsync(sha256);

        Assert.Equal(first, storedFirst);
        Assert.Equal(first, storedDuplicate);
        Assert.Equal(first, found);
        Assert.Equal(1, environment.CountCatalogRows());
    }

    [Fact]
    public async Task GetOrAddAsync_RejectsSizeConflictForExistingSha()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        var index = new SqliteExternalPayloadAddressIndex(
            environment.Session,
            environment.Factory);
        string sha256 = new string('b', 64);
        var first = new ExternalPayloadAddress(
            sha256,
            Path.Combine("2026-09-01", sha256 + ".png"),
            100);
        var conflicting = new ExternalPayloadAddress(
            sha256,
            Path.Combine("2026-09-06", sha256 + ".png"),
            101);

        await index.GetOrAddAsync(first);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await index.GetOrAddAsync(conflicting));

        Assert.Equal(first, await index.FindAsync(sha256));
        Assert.Equal(1, environment.CountCatalogRows());
    }

    [Fact]
    public async Task GetOrAddAsync_RejectsRelativePathCollisionBetweenHashes()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        var index = new SqliteExternalPayloadAddressIndex(
            environment.Session,
            environment.Factory);
        string firstSha = new string('c', 64);
        string secondSha = new string('d', 64);
        string sharedPath = Path.Combine("2026-09-01", "shared.png");

        await index.GetOrAddAsync(new ExternalPayloadAddress(firstSha, sharedPath, 10));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await index.GetOrAddAsync(
                new ExternalPayloadAddress(secondSha, sharedPath, 10)));

        Assert.Null(await index.FindAsync(secondSha));
        Assert.Equal(1, environment.CountCatalogRows());
    }

    [Fact]
    public async Task FindAsync_AfterSessionDisposeCancelsBeforeCatalogAccess()
    {
        TestEnvironment environment = await TestEnvironment.CreateAsync();
        try
        {
            var index = new SqliteExternalPayloadAddressIndex(
                environment.Session,
                environment.Factory);
            environment.DisposeSession();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await index.FindAsync(new string('e', 64)));
        }
        finally
        {
            environment.Dispose();
        }
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly byte[] _key;
        private readonly ProtectedApplicationLifecycle _lifecycle;
        private bool _sessionDisposed;
        private bool _disposed;

        private TestEnvironment(
            string root,
            byte[] key,
            PlainSqliteConnectionFactory factory,
            ProtectedApplicationLifecycle lifecycle,
            ProtectedStorageSessionLease session)
        {
            Root = root;
            _key = key;
            Factory = factory;
            _lifecycle = lifecycle;
            Session = session;
        }

        public string Root { get; }
        public PlainSqliteConnectionFactory Factory { get; }
        public ProtectedStorageSessionLease Session { get; }

        public static async Task<TestEnvironment> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "Clipensk.Storage.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            byte[] key = RandomNumberGenerator.GetBytes(32);
            var factory = new PlainSqliteConnectionFactory();
            Guid storageId = Guid.NewGuid();

            ProtectedStorageDatabaseResult result = await new ProtectedStorageDatabaseService(factory)
                .InitializeOrValidateAsync(root, storageId, key, allowInitialize: true);
            Assert.True(result.IsSuccess);

            var lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);
            Assert.True(lifecycle.TryBeginUnlock());
            lifecycle.CompleteUnlock();
            ProtectedStorageSessionLease session = ProtectedStorageSessionLease.Create(
                lifecycle,
                root,
                storageId,
                new MasterKeyLease(key));

            return new TestEnvironment(root, key, factory, lifecycle, session);
        }

        public int CountCatalogRows()
        {
            using SqliteConnection connection = Factory.Open(
                Path.Combine(Root, "Current", "storage-catalog.db"),
                Session.DangerousGetMasterKeyMemory(),
                SqliteOpenMode.ReadOnly);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM ExternalPayloadAddressIndex;";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public void DisposeSession()
        {
            if (_sessionDisposed)
            {
                return;
            }

            _sessionDisposed = true;
            Session.Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeSession();
            if (_lifecycle.CanAccessProtectedData)
            {
                _lifecycle.TryBeginLock();
            }
            Assert.All(_key, value => Assert.Equal((byte)0, value));
            Directory.Delete(Root, recursive: true);
        }
    }

    public sealed class PlainSqliteConnectionFactory : IKeyedSqliteConnectionFactory
    {
        private static readonly object ProviderGate = new();
        private static bool _initialized;

        public PlainSqliteConnectionFactory()
        {
            lock (ProviderGate)
            {
                if (!_initialized)
                {
                    SQLitePCL.Batteries.Init();
                    _initialized = true;
                }
            }
        }

        public SqliteConnection Open(
            string databasePath,
            ReadOnlyMemory<byte> masterKey,
            SqliteOpenMode mode)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = mode,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }
    }
}
