using System.Security.Cryptography;
using Clipensk.Core.Application;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class CatalogClipboardExternalPayloadAddressResolverTests
{
    [Fact]
    public async Task ResolveNormalizedPngAsync_DuplicateKeepsFirstStoredDateAndOneFile()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        var provider = new ThrowingExtensionProvider();
        CatalogClipboardExternalPayloadAddressResolver resolver = environment.CreateResolver(provider);
        byte[] bytes = [1, 2, 3, 4, 5];

        ExternalPayloadAddress first = await resolver.ResolveNormalizedPngAsync(
            new DateOnly(2026, 9, 1),
            bytes);
        ExternalPayloadAddress duplicate = await resolver.ResolveNormalizedPngAsync(
            new DateOnly(2026, 9, 6),
            bytes);

        Assert.Equal(first, duplicate);
        Assert.Contains("2026-09-01", first.RelativePath, StringComparison.Ordinal);
        Assert.Single(environment.GetPayloadFiles());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(environment.ResolveFilesPath(first)));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ResolveCustomBinaryAsync_ExistingAddressDoesNotRequireExtensionProvider()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        byte[] bytes = [9, 8, 7, 6];
        ExternalPayloadAddress oldAddress = ExternalPayloadAddressFactory.ForCustomBinary(
            new DateOnly(2026, 8, 31),
            bytes,
            ".dat");
        await environment.AddressIndex.GetOrAddAsync(oldAddress);
        var provider = new ThrowingExtensionProvider();
        CatalogClipboardExternalPayloadAddressResolver resolver = environment.CreateResolver(provider);

        ExternalPayloadAddress resolved = await resolver.ResolveCustomBinaryAsync(
            new DateOnly(2026, 9, 6),
            "Contoso.Private.Binary",
            bytes);

        Assert.Equal(oldAddress, resolved);
        Assert.Equal(0, provider.Calls);
        Assert.True(File.Exists(environment.ResolveFilesPath(oldAddress)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(environment.ResolveFilesPath(oldAddress)));
    }

    [Fact]
    public async Task ResolveCustomBinaryAsync_NewPayloadUsesExplicitProviderExtension()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        byte[] bytes = [11, 12, 13];
        var provider = new FixedExtensionProvider(".clip");
        CatalogClipboardExternalPayloadAddressResolver resolver = environment.CreateResolver(provider);

        ExternalPayloadAddress resolved = await resolver.ResolveCustomBinaryAsync(
            new DateOnly(2026, 9, 6),
            "Contoso.Private.Binary",
            bytes);

        Assert.Equal(1, provider.Calls);
        Assert.EndsWith(".clip", resolved.RelativePath, StringComparison.Ordinal);
        Assert.Equal(resolved, await environment.AddressIndex.FindAsync(resolved.Sha256));
        Assert.True(File.Exists(environment.ResolveFilesPath(resolved)));
    }

    [Fact]
    public async Task ResolveCustomBinaryAsync_BlankProviderExtensionDoesNotReserveAddress()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        byte[] bytes = [21, 22, 23];
        var provider = new FixedExtensionProvider("   ");
        CatalogClipboardExternalPayloadAddressResolver resolver = environment.CreateResolver(provider);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await resolver.ResolveCustomBinaryAsync(
                new DateOnly(2026, 9, 6),
                "Contoso.Private.Binary",
                bytes));

        Assert.Equal(1, provider.Calls);
        Assert.Null(await environment.AddressIndex.FindAsync(sha256));
        Assert.Empty(environment.GetPayloadFiles());
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly byte[] _key;
        private readonly ProtectedApplicationLifecycle _lifecycle;
        private bool _disposed;

        private TestEnvironment(
            string root,
            byte[] key,
            PlainSqliteConnectionFactory factory,
            ProtectedApplicationLifecycle lifecycle,
            ProtectedStorageSessionLease session,
            SqliteExternalPayloadAddressIndex addressIndex)
        {
            Root = root;
            _key = key;
            Factory = factory;
            _lifecycle = lifecycle;
            Session = session;
            AddressIndex = addressIndex;
        }

        public string Root { get; }
        public PlainSqliteConnectionFactory Factory { get; }
        public ProtectedStorageSessionLease Session { get; }
        public SqliteExternalPayloadAddressIndex AddressIndex { get; }
        public string FilesRoot => Path.Combine(Root, "Files");

        public static async Task<TestEnvironment> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "Clipensk.Storage.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            byte[] key = RandomNumberGenerator.GetBytes(32);
            Guid storageId = Guid.NewGuid();
            var factory = new PlainSqliteConnectionFactory();

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
            var addressIndex = new SqliteExternalPayloadAddressIndex(session, factory);

            return new TestEnvironment(root, key, factory, lifecycle, session, addressIndex);
        }

        public CatalogClipboardExternalPayloadAddressResolver CreateResolver(
            IClipboardCustomBinaryFileExtensionProvider provider)
        {
            return new CatalogClipboardExternalPayloadAddressResolver(
                AddressIndex,
                new ExternalPayloadStore(FilesRoot),
                provider);
        }

        public string ResolveFilesPath(ExternalPayloadAddress address) =>
            Path.Combine(FilesRoot, address.RelativePath);

        public string[] GetPayloadFiles() =>
            Directory.Exists(FilesRoot)
                ? Directory.GetFiles(FilesRoot, "*", SearchOption.AllDirectories)
                : [];

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Session.Dispose();
            if (_lifecycle.CanAccessProtectedData)
            {
                _lifecycle.TryBeginLock();
            }
            Assert.All(_key, value => Assert.Equal((byte)0, value));
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FixedExtensionProvider(string extension) :
        IClipboardCustomBinaryFileExtensionProvider
    {
        public int Calls { get; private set; }

        public ValueTask<string> GetExtensionAsync(
            string formatName,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(extension);
        }
    }

    private sealed class ThrowingExtensionProvider : IClipboardCustomBinaryFileExtensionProvider
    {
        public int Calls { get; private set; }

        public ValueTask<string> GetExtensionAsync(
            string formatName,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException(
                "Extension provider should not be used for this payload.");
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
