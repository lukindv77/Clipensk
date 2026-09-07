using Clipensk.Core.Application;
using Clipensk.Core.History;
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
        Assert.NotNull(services.HistoryRepository);
        Assert.NotNull(services.UnifiedHistoryRepository);
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

    [Theory]
    [InlineData("caller")]
    [InlineData("lock")]
    [InlineData("dispose")]
    public async Task HistoryRepository_RejectsRevokedAccessWithoutOpeningDatabase(string cause)
    {
        string root = CreateTemporaryDirectory();
        var lifecycle = CreateUnlockedLifecycle();
        using var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            root,
            Guid.NewGuid(),
            new MasterKeyLease(Enumerable.Repeat((byte)0x99, 32).ToArray()));
        var factory = new ThrowingConnectionFactory();
        var extensionProvider = new ThrowingExtensionProvider();
        ProtectedClipboardHistoryServices services = ProtectedClipboardHistoryServices.Create(
            session,
            extensionProvider,
            factory);
        using var cancellation = new CancellationTokenSource();

        if (cause == "caller") cancellation.Cancel();
        if (cause == "lock") Assert.True(lifecycle.TryBeginLock());
        if (cause == "dispose") session.Dispose();

        var period = new JournalDateRange(new DateOnly(2026, 9, 6), new DateOnly(2026, 9, 6));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await services.HistoryRepository.ReadAsync(period, 1, cancellation.Token));
        Assert.Equal(0, factory.OpenCallCount);
        Assert.Equal(0, extensionProvider.CallCount);
        Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("lock")]
    [InlineData("dispose")]
    public async Task UnifiedHistoryRepository_RejectsRevokedAccessWithoutOpeningDatabase(string cause)
    {
        string root = CreateTemporaryDirectory();
        var lifecycle = CreateUnlockedLifecycle();
        using var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            root,
            Guid.NewGuid(),
            new MasterKeyLease(Enumerable.Repeat((byte)0x9A, 32).ToArray()));
        var factory = new ThrowingConnectionFactory();
        var extensionProvider = new ThrowingExtensionProvider();
        ProtectedClipboardHistoryServices services = ProtectedClipboardHistoryServices.Create(
            session,
            extensionProvider,
            factory);
        using var cancellation = new CancellationTokenSource();

        if (cause == "caller") cancellation.Cancel();
        if (cause == "lock") Assert.True(lifecycle.TryBeginLock());
        if (cause == "dispose") session.Dispose();

        var period = new JournalDateRange(new DateOnly(2026, 9, 6), new DateOnly(2026, 9, 6));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await services.UnifiedHistoryRepository.ReadAsync(period, 1, cancellation.Token));
        Assert.Equal(0, factory.OpenCallCount);
        Assert.Equal(0, extensionProvider.CallCount);
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
