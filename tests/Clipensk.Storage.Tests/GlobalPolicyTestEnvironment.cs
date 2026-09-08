using System.Globalization;
using Clipensk.Core.Application;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

internal sealed class GlobalPolicyTestEnvironment : IDisposable
{
    private GlobalPolicyTestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "Clipensk.Storage.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Service = new ProtectedStorageDatabaseService(Factory);
        ReopenSession();
    }

    public string Root { get; }
    public Guid StorageId { get; } = Guid.NewGuid();
    public byte[] Key { get; } = Enumerable.Repeat((byte)0x37, 32).ToArray();
    public TestConnectionFactory Factory { get; } = new();
    public ProtectedStorageDatabaseService Service { get; }
    public ProtectedApplicationLifecycle Lifecycle { get; private set; } = null!;
    public ProtectedStorageSessionLease Session { get; private set; } = null!;
    public string CurrentPath => Path.Combine(Root, "Current", "current.db");
    public string CatalogPath => Path.Combine(Root, "Current", "storage-catalog.db");
    public SqliteGlobalClipboardCapturePolicyRepository Repository => new(Session, Factory);
    public SqliteCustomBinaryFormatConfigurationRepository CustomBinaryConfigurations =>
        new(Session, Factory);

    public static async Task<GlobalPolicyTestEnvironment> CreateAsync()
    {
        var environment = new GlobalPolicyTestEnvironment();
        try
        {
            Assert.True((await environment.Service.InitializeOrValidateAsync(
                environment.Root, environment.StorageId, environment.Key, allowInitialize: true)).IsSuccess);
            return environment;
        }
        catch
        {
            environment.Dispose();
            throw;
        }
    }

    public void ReopenSession()
    {
        Session?.Dispose();
        Lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);
        Assert.True(Lifecycle.TryBeginUnlock());
        Lifecycle.CompleteUnlock();
        Session = ProtectedStorageSessionLease.Create(Lifecycle, Root, StorageId, new MasterKeyLease(Key.ToArray()));
    }

    public void Execute(string sql, bool catalog = false)
    {
        using SqliteConnection connection = Factory.Open(catalog ? CatalogPath : CurrentPath, Key, SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public long Scalar(string sql, bool catalog = false)
    {
        using SqliteConnection connection = Factory.Open(catalog ? CatalogPath : CurrentPath, Key, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void DowngradeToV4() => Execute("""
        DROP TABLE GlobalCapturePolicyMaintenance;
        DROP TABLE CustomBinaryFormatConfiguration;
        DROP TABLE GlobalFormatCapturePolicy;
        DROP TABLE GlobalCapturePolicy;
        UPDATE DatabaseIdentity SET SchemaVersion = 4;
        PRAGMA user_version = 4;
        """);

    public void DowngradeToV5() => Execute("""
        DROP TABLE GlobalCapturePolicyMaintenance;
        DROP TABLE CustomBinaryFormatConfiguration;
        UPDATE DatabaseIdentity SET SchemaVersion = 5;
        PRAGMA user_version = 5;
        """);

    public void DowngradeToV6() => Execute("""
        DROP TABLE GlobalCapturePolicyMaintenance;
        UPDATE DatabaseIdentity SET SchemaVersion = 6;
        PRAGMA user_version = 6;
        """);

    public Task<ProtectedStorageDatabaseResult> ValidateAsync(CancellationToken token = default) =>
        Service.InitializeOrValidateAsync(Root, StorageId, Key, allowInitialize: false, cancellationToken: token);

    public void Dispose()
    {
        Session?.Dispose();
        Directory.Delete(Root, recursive: true);
    }

    internal sealed class TestConnectionFactory : IKeyedSqliteConnectionFactory
    {
        static TestConnectionFactory() => SQLitePCL.Batteries.Init();
        public Action<SqliteConnection, SqliteOpenMode>? OnOpen { get; set; }
        public List<SqliteOpenMode> Modes { get; } = new();
        public SqliteConnection? LastConnection { get; private set; }

        public SqliteConnection Open(string databasePath, ReadOnlyMemory<byte> masterKey, SqliteOpenMode mode)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath), Mode = mode, Pooling = false,
            }.ToString());
            connection.Open();
            Modes.Add(mode);
            LastConnection = connection;
            OnOpen?.Invoke(connection, mode);
            return connection;
        }
    }
}
