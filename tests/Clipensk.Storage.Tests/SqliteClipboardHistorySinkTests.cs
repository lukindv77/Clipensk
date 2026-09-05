using System.Globalization;
using System.Security.Cryptography;
using Clipensk.Core.Application;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

public sealed class SqliteClipboardHistorySinkTests
{
    [Fact]
    public async Task StoreAsync_PersistsInlinePayloadsWithEventTimeAndSourceMetadata()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        DurableApplicationId sourceApplicationId = DurableApplicationId.New();
        environment.InsertApplicationIdentity(sourceApplicationId);

        DateTimeOffset timestamp = new(2026, 9, 6, 1, 23, 45, TimeSpan.FromHours(7));
        ClipboardCaptureContext context = new(
            new ClipboardCaptureRequest(new EventTimeContext(timestamp, "SE Asia Standard Time")),
            new ClipboardSourceApplication(
                ProcessId: 4242,
                ExecutablePath: @"C:\Apps\Contoso.exe",
                ApplicationUserModelId: "Contoso.Sample_123!App"),
            sourceApplicationId);

        var textRoute = Route("Html", ClipboardContentReaderKind.Text);
        var linkRoute = Route("WebLink", ClipboardContentReaderKind.Link);
        var storageRoute = Route("StorageItems", ClipboardContentReaderKind.StorageItems);
        const string linkText = "https://example.test/A%20B?x=1";
        var storageItems = new ClipboardCapturedStorageItemsContent(
            storageRoute,
            [
                new ClipboardStorageItemMetadata(
                    0,
                    @"C:\Temp\a.txt",
                    "a.txt",
                    ".txt",
                    IsDirectory: false,
                    ClipboardPreferredFileOperation.Copy),
            ]);

        var capture = new ClipboardAcceptedCapture(
            context,
            [
                new ClipboardCapturedTextContent(textRoute, "<b>Hello</b>", 12, "Hello"),
                new ClipboardCapturedLinkContent(
                    linkRoute,
                    new Uri(linkText, UriKind.Absolute),
                    ClipboardCanonicalPayloadSize.MeasureUtf8Text(linkText)),
                storageItems,
            ]);

        var sink = new SqliteClipboardHistorySink(
            environment.Session,
            new RejectingExternalPayloadResolver(),
            environment.Factory);
        await sink.StoreAsync(capture);

        using SqliteConnection connection = environment.OpenCurrent(SqliteOpenMode.ReadOnly);
        using (SqliteCommand eventCommand = connection.CreateCommand())
        {
            eventCommand.CommandText = """
                SELECT EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                       SourceApplicationId, SourceProcessId, SourceExecutablePath,
                       SourceApplicationUserModelId
                FROM ClipboardHistoryEvent;
                """;
            using SqliteDataReader reader = eventCommand.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), reader.GetString(0));
            Assert.Equal(420, reader.GetInt32(1));
            Assert.Equal("SE Asia Standard Time", reader.GetString(2));
            Assert.Equal("2026-09-06", reader.GetString(3));
            Assert.Equal(sourceApplicationId.ToString(), reader.GetString(4));
            Assert.Equal(4242, reader.GetInt64(5));
            Assert.Equal(@"C:\Apps\Contoso.exe", reader.GetString(6));
            Assert.Equal("Contoso.Sample_123!App", reader.GetString(7));
            Assert.False(reader.Read());
        }

        using (SqliteCommand payloadCommand = connection.CreateCommand())
        {
            payloadCommand.CommandText = """
                SELECT PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                       InlineCanonicalText, SearchText, ExternalSha256
                FROM ClipboardHistoryPayload
                ORDER BY PayloadOrder;
                """;
            using SqliteDataReader reader = payloadCommand.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal(0, reader.GetInt32(0));
            Assert.Equal("Html", reader.GetString(1));
            Assert.Equal("Text", reader.GetString(2));
            Assert.Equal(12, reader.GetInt64(3));
            Assert.Equal("<b>Hello</b>", reader.GetString(4));
            Assert.Equal("Hello", reader.GetString(5));
            Assert.True(reader.IsDBNull(6));

            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal("WebLink", reader.GetString(1));
            Assert.Equal("Link", reader.GetString(2));
            Assert.Equal(linkText, reader.GetString(4));
            Assert.True(reader.IsDBNull(5));

            Assert.True(reader.Read());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal("StorageItems", reader.GetString(1));
            Assert.Equal("StorageItems", reader.GetString(2));
            Assert.Equal(storageItems.CanonicalRepresentation, reader.GetString(4));
            Assert.True(reader.IsDBNull(5));
            Assert.False(reader.Read());
        }
    }

    [Fact]
    public async Task StoreAsync_ExternalResolutionFailureLeavesNoHistoryRows()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        var resolver = new ThrowingExternalPayloadResolver();
        var sink = new SqliteClipboardHistorySink(environment.Session, resolver, environment.Factory);
        ClipboardAcceptedCapture capture = CreatePngCapture([1, 2, 3, 4]);

        await Assert.ThrowsAsync<IOException>(async () => await sink.StoreAsync(capture));

        Assert.Equal(1, resolver.PngCalls);
        Assert.Equal(0, environment.CountRows("ClipboardHistoryEvent"));
        Assert.Equal(0, environment.CountRows("ClipboardHistoryPayload"));
    }

    [Fact]
    public async Task StoreAsync_RejectsExternalAddressThatDoesNotMatchPayloadBeforeSqlWrite()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        var sink = new SqliteClipboardHistorySink(
            environment.Session,
            new InvalidExternalPayloadResolver(),
            environment.Factory);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await sink.StoreAsync(CreatePngCapture([1, 2, 3, 4])));

        Assert.Equal(0, environment.CountRows("ClipboardHistoryEvent"));
    }

    private static ClipboardAcceptedCapture CreatePngCapture(byte[] bytes)
    {
        return new ClipboardAcceptedCapture(
            new ClipboardCaptureContext(
                new ClipboardCaptureRequest(
                    new EventTimeContext(
                        new DateTimeOffset(2026, 9, 6, 2, 0, 0, TimeSpan.FromHours(7)),
                        "SE Asia Standard Time")),
                null),
            [new ClipboardCapturedPngImageContent(Route("Bitmap", ClipboardContentReaderKind.PngImage), bytes)]);
    }

    private static ClipboardContentReaderRoute Route(
        string formatName,
        ClipboardContentReaderKind readerKind) =>
        new(new ClipboardSelectedFormat(formatName, 4096), readerKind);

    private sealed class TestEnvironment : IDisposable
    {
        private readonly byte[] _key;
        private readonly ProtectedApplicationLifecycle _lifecycle;

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

        public SqliteConnection OpenCurrent(SqliteOpenMode mode) => Factory.Open(
            Path.Combine(Root, "Current", "current.db"),
            Session.DangerousGetMasterKeyMemory(),
            mode);

        public int CountRows(string tableName)
        {
            using SqliteConnection connection = OpenCurrent(SqliteOpenMode.ReadOnly);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        public void InsertApplicationIdentity(DurableApplicationId applicationId)
        {
            using SqliteConnection connection = OpenCurrent(SqliteOpenMode.ReadWrite);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                VALUES ($applicationId, $createdAtUtc);
                """;
            command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            command.Parameters.AddWithValue(
                "$createdAtUtc",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            Session.Dispose();
            if (_lifecycle.CanAccessProtectedData)
            {
                _lifecycle.TryBeginLock();
            }
            Assert.All(_key, value => Assert.Equal((byte)0, value));
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class RejectingExternalPayloadResolver : IClipboardExternalPayloadAddressResolver
    {
        public ValueTask<ExternalPayloadAddress> ResolveNormalizedPngAsync(
            DateOnly eventCalendarDate,
            ReadOnlyMemory<byte> pngBytes,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("External resolver should not be called for inline-only capture.");

        public ValueTask<ExternalPayloadAddress> ResolveCustomBinaryAsync(
            DateOnly eventCalendarDate,
            string formatName,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("External resolver should not be called for inline-only capture.");
    }

    private sealed class ThrowingExternalPayloadResolver : IClipboardExternalPayloadAddressResolver
    {
        public int PngCalls { get; private set; }

        public ValueTask<ExternalPayloadAddress> ResolveNormalizedPngAsync(
            DateOnly eventCalendarDate,
            ReadOnlyMemory<byte> pngBytes,
            CancellationToken cancellationToken = default)
        {
            PngCalls++;
            throw new IOException("simulated external store failure");
        }

        public ValueTask<ExternalPayloadAddress> ResolveCustomBinaryAsync(
            DateOnly eventCalendarDate,
            string formatName,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InvalidExternalPayloadResolver : IClipboardExternalPayloadAddressResolver
    {
        public ValueTask<ExternalPayloadAddress> ResolveNormalizedPngAsync(
            DateOnly eventCalendarDate,
            ReadOnlyMemory<byte> pngBytes,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalPayloadAddress(
                new string('0', 64),
                Path.Combine(
                    eventCalendarDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "wrong.png"),
                pngBytes.Length));

        public ValueTask<ExternalPayloadAddress> ResolveCustomBinaryAsync(
            DateOnly eventCalendarDate,
            string formatName,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
