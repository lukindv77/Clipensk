using Clipensk.Core.Application;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedClipboardCaptureServicesTests
{
    [Fact]
    public async Task Create_ComposesIdentityAndPolicyServicesWithoutEagerDatabaseAccess()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = Enumerable.Repeat((byte)0x55, 32).ToArray();
        var lifecycle = CreateUnlockedLifecycle();
        using var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            root,
            Guid.NewGuid(),
            new MasterKeyLease(key));
        var globalPolicy = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny);
        var factory = new ThrowingConnectionFactory();

        ProtectedClipboardCaptureServices services = ProtectedClipboardCaptureServices.Create(
            session,
            globalPolicy,
            factory);

        Assert.NotNull(services.ApplicationIdentityRegistry);
        Assert.NotNull(services.PolicyRepository);
        Assert.NotNull(services.PolicyProvider);
        Assert.Equal(0, factory.OpenCallCount);

        ClipboardCapturePolicySet policies = await services.PolicyProvider.GetPoliciesAsync(
            new ClipboardCaptureContext(
                new ClipboardCaptureRequest(
                    new EventTimeContext(
                        new DateTimeOffset(2026, 9, 6, 1, 30, 0, TimeSpan.FromHours(7)),
                        "Test/Zone")),
                SourceApplication: null));

        Assert.Same(globalPolicy, policies.GlobalPolicy);
        Assert.Null(policies.ApplicationPolicy);
        Assert.Equal(0, factory.OpenCallCount);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Create_AfterProtectedAccessRevocationFailsClosed()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = Enumerable.Repeat((byte)0x66, 32).ToArray();
        var lifecycle = CreateUnlockedLifecycle();
        using var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            root,
            Guid.NewGuid(),
            new MasterKeyLease(key));
        Assert.True(lifecycle.TryBeginLock());

        Assert.Throws<InvalidOperationException>(() =>
            ProtectedClipboardCaptureServices.Create(
                session,
                new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny),
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
