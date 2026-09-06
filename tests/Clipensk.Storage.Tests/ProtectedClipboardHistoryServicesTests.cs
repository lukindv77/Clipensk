using Clipensk.Core.Application;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedClipboardHistoryServicesTests
{
    [Fact]
    public void Create_ComposesHistoryServicesWithoutEagerDatabaseAccess()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = Enumerable.Repeat((byte)0x77, 32).ToArray();
        var lifecycle = CreateUnlockedLifecycle();
        using var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            root,
            Guid.NewGuid(),
            new MasterKeyLease(key));
        var factory = new ThrowingConnectionFactory();
        var extensionProvider = new ThrowingExtensionProvider();

        ProtectedClipboardHistoryServices services = ProtectedClipboardHistoryServices.Create(
            session,
            extensionProvider,
            factory);

        Assert.NotNull(services.AddressIndex);
        Assert.NotNull(services.ExternalPayloadResolver);
        Assert.NotNull(services.HistorySink);
        Assert.Equal(0, factory.OpenCallCount);
        Assert.Equal(0, extensionProvider.CallCount);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Create_AfterProtectedAccessRevocationFailsClosed()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = Enumerable.Repeat((byte)0x88, 32).ToArray();
        var lifecycle = CreateUnlockedLifecycle();
        using var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            root,
            Guid.NewGuid(),
            new MasterKeyLease(key));
        Assert.True(lifecycle.TryBeginLock());

        Assert.Throws<InvalidOperationException>(() =>
            ProtectedClipboardHistoryServices.Create(
                session,
                new ThrowingExtensionProvider(),
                new ThrowingConnectionFactory()));

        Directory.Delete(root, recursive: true);
    }

    private static ProtectedApplicationLifecycle CreateUnlockedLifecycle()
    {
        var lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);
        Assert.True(lifecycle.TryBeginUnlock());
        lifecycle.CompleteUnlock();
        return lifecycle;
    }

    private static string CreateTemporaryDirectory()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "Clipensk.Storage.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class ThrowingExtensionProvider : IClipboardCustomBinaryFileExtensionProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<string> GetExtensionAsync(
            string formatName,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException(
                "Composition must not request a custom binary extension eagerly.");
        }
    }

    private sealed class ThrowingConnectionFactory : IKeyedSqliteConnectionFactory
    {
        public int OpenCallCount { get; private set; }

        public SqliteConnection Open(
            string databasePath,
            ReadOnlyMemory<byte> masterKey,
            SqliteOpenMode mode)
        {
            OpenCallCount++;
            throw new InvalidOperationException("Composition must not open a database eagerly.");
        }
    }
}
