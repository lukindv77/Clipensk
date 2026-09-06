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

public sealed class SqliteCurrentClipboardHistoryRepositoryTests
{
    private static readonly JournalDateRange Period = new(new DateOnly(2026, 9, 6), new DateOnly(2026, 9, 6));

    [Fact]
    public async Task ReadAsync_RoundTripsSinkPayloadsAndOriginalTimeAndSource()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        var applicationId = DurableApplicationId.New();
        environment.Execute("""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ($id, '2026-09-01T00:00:00.0000000Z');
            """, ("$id", applicationId.ToString()));
        var time = new EventTimeContext(
            new DateTimeOffset(2026, 9, 6, 0, 30, 0, TimeSpan.FromHours(7)),
            "SE Asia Standard Time");
        var source = new ClipboardSourceApplication(4242, @"C:\Apps\sample.exe", "Sample!App");
        const string html = "<b>Привет</b>";
        const string link = "https://example.test/A%20B?x=1";
        const string rtf = @"{\rtf1 original}";
        var storageItems = new ClipboardCapturedStorageItemsContent(
            Route("StorageItems", ClipboardContentReaderKind.StorageItems),
            [new ClipboardStorageItemMetadata(@"C:\Temp\é.txt", "é.txt", ".txt", false, 0, ClipboardPreferredFileOperation.Copy)]);
        byte[] png = [1, 2, 3];
        byte[] custom = [4, 5, 6, 7];
        var resolver = new ExistingAddressResolver();
        var sink = new SqliteClipboardHistorySink(environment.Session, resolver, environment.Factory);
        await sink.StoreAsync(new ClipboardAcceptedCapture(
            new ClipboardCaptureContext(new ClipboardCaptureRequest(time), source, applicationId),
            [
                new ClipboardCapturedTextContent(Route("Html", ClipboardContentReaderKind.Text), html,
                    ClipboardCanonicalPayloadSize.MeasureUtf8Text(html), "Привет"),
                new ClipboardCapturedLinkContent(Route("WebLink", ClipboardContentReaderKind.Link), new Uri(link),
                    ClipboardCanonicalPayloadSize.MeasureUtf8Text(link)),
                storageItems,
                new ClipboardCapturedPngImageContent(Route("Bitmap", ClipboardContentReaderKind.PngImage), png),
                new ClipboardCapturedCustomBinaryContent(Route("Sample.Binary", ClipboardContentReaderKind.CustomBinary), custom),
                new ClipboardCapturedTextContent(Route("Rtf", ClipboardContentReaderKind.Text), rtf,
                    ClipboardCanonicalPayloadSize.MeasureUtf8Text(rtf)),
            ]));

        int opensBeforeRead = environment.Factory.Modes.Count;
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);
        Assert.Equal(opensBeforeRead, environment.Factory.Modes.Count);
        ClipboardHistoryEntry entry = Assert.Single(await repository.ReadAsync(Period, 1));

        Assert.NotEqual(Guid.Empty, entry.EventId);
        Assert.Equal(time, entry.EventTime);
        Assert.Equal(applicationId, entry.SourceApplicationId);
        Assert.Equal(source, entry.SourceApplication);
        Assert.Equal(6, entry.Payloads.Count);
        Assert.Equal(Enumerable.Range(0, 6), entry.Payloads.Select(p => p.PayloadOrder));
        Assert.Equal(html, entry.Payloads[0].InlineCanonicalText);
        Assert.Equal("Привет", entry.Payloads[0].SearchText);
        Assert.Equal(link, entry.Payloads[1].InlineCanonicalText);
        Assert.Equal(storageItems.CanonicalRepresentation, entry.Payloads[2].InlineCanonicalText);
        Assert.Equal(ClipboardHistoryPayloadKind.PngImage, entry.Payloads[3].Kind);
        Assert.Equal(ClipboardHistoryPayloadKind.CustomBinary, entry.Payloads[4].Kind);
        Assert.Equal(resolver.PngAddress!.RelativePath, entry.Payloads[3].ExternalReference!.RelativePath);
        Assert.Equal(resolver.CustomAddress!.RelativePath, entry.Payloads[4].ExternalReference!.RelativePath);
        Assert.Equal(resolver.CustomAddress.Sha256, entry.Payloads[4].ExternalReference!.Sha256);
        Assert.Equal(custom.Length, entry.Payloads[4].ExternalReference!.SizeBytes);
        Assert.Equal(rtf, entry.Payloads[5].InlineCanonicalText);
        Assert.Null(entry.Payloads[5].SearchText);
        Assert.False(File.Exists(Path.Combine(environment.Root, "Files", resolver.PngAddress.RelativePath)));
        Assert.Equal(opensBeforeRead + 1, environment.Factory.Modes.Count);
        Assert.Equal(SqliteOpenMode.ReadOnly, environment.Factory.Modes[^1]);

        // Returned data does not depend on an open DB connection or a live session.
        environment.Session.Dispose();
        Assert.Equal(html, entry.Payloads[0].InlineCanonicalText);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await repository.ReadAsync(Period, 1));
    }

    [Fact]
    public async Task ReadAsync_FiltersCalendarBeforeLimitingWholeEventsAndUsesStableTieOrder()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        Guid low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid high = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var localTime = new DateTimeOffset(2026, 9, 6, 0, 15, 0, TimeSpan.FromHours(7));
        environment.InsertTextEvent(low, localTime, "low");
        environment.InsertTextEvent(high, localTime, "first", "second", "third");
        environment.InsertTextEvent(Guid.NewGuid(), localTime.AddDays(1), "outside-newer");
        environment.InsertTextEvent(Guid.NewGuid(), localTime.AddDays(-1), "outside-older");
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);

        ClipboardHistoryEntry first = Assert.Single(await repository.ReadAsync(Period, 1));
        Assert.Equal(high, first.EventId);
        Assert.Equal(new[] { "first", "second", "third" }, first.Payloads.Select(p => p.InlineCanonicalText));
        Assert.Null(first.SourceApplicationId);
        Assert.Null(first.SourceApplication);
        IReadOnlyList<ClipboardHistoryEntry> both = await repository.ReadAsync(Period, 2);
        Assert.Equal(new[] { high, low }, both.Select(e => e.EventId));
        Assert.Empty(await repository.ReadAsync(new JournalDateRange(new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 1)), 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReadAsync_RejectsNonPositiveLimitBeforeOpeningDatabase(int limit)
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);
        int opens = environment.Factory.Modes.Count;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await repository.ReadAsync(Period, limit));
        Assert.Equal(opens, environment.Factory.Modes.Count);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("lock")]
    [InlineData("dispose")]
    public async Task ReadAsync_RejectsCancelledAccessBeforeOpeningDatabase(string cause)
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);
        using var cancellation = new CancellationTokenSource();
        if (cause == "caller") cancellation.Cancel();
        if (cause == "lock") Assert.True(environment.Lifecycle.TryBeginLock());
        if (cause == "dispose") environment.Session.Dispose();
        int opens = environment.Factory.Modes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repository.ReadAsync(Period, 1, cancellation.Token));
        Assert.Equal(opens, environment.Factory.Modes.Count);
    }

    [Fact]
    public async Task ReadAsync_CancellationDuringOpenDisposesConnectionAndReturnsNoData()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        environment.Factory.AfterOpen = cancellation.Cancel;
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repository.ReadAsync(Period, 1, cancellation.Token));
        Assert.Equal(System.Data.ConnectionState.Closed, environment.Factory.LastConnection!.State);
    }

    [Theory]
    [InlineData("UPDATE DatabaseIdentity SET StorageId = '00000000-0000-0000-0000-000000000099';")]
    [InlineData("UPDATE DatabaseIdentity SET DatabaseRole = 'StorageCatalog';")]
    [InlineData("UPDATE DatabaseIdentity SET SchemaVersion = 3; PRAGMA user_version = 3;")]
    [InlineData("PRAGMA user_version = 3;")]
    [InlineData("DROP INDEX IX_ClipboardHistoryPayload_FormatName;")]
    public async Task ReadAsync_RejectsWrongDatabaseIdentityOrSchema(string mutation)
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        environment.Execute(mutation);
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.ReadAsync(Period, 1));
        Assert.Equal(System.Data.ConnectionState.Closed, environment.Factory.LastConnection!.State);
    }

    [Theory]
    [InlineData("UPDATE ClipboardHistoryEvent SET EventUtc = 'invalid';")]
    [InlineData("UPDATE ClipboardHistoryEvent SET LocalOffsetMinutes = 900;")]
    [InlineData("UPDATE ClipboardHistoryEvent SET EventUtc = '2026-09-04T00:00:00.0000000Z';")]
    [InlineData("UPDATE ClipboardHistoryPayload SET CanonicalByteCount = 999;")]
    [InlineData("UPDATE ClipboardHistoryPayload SET PayloadOrder = 2;")]
    public async Task ReadAsync_RejectsMalformedPersistedDataWithoutReturningPartialResults(string mutation)
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        environment.InsertTextEvent(Guid.NewGuid(), new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero), "value");
        environment.Execute(mutation);
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.ReadAsync(Period, 1));
        Assert.Equal(System.Data.ConnectionState.Closed, environment.Factory.LastConnection!.State);
    }

    private static ClipboardContentReaderRoute Route(string name, ClipboardContentReaderKind kind) =>
        new(new ClipboardSelectedFormat(name, null), kind);

    private sealed class ExistingAddressResolver : IClipboardExternalPayloadAddressResolver
    {
        public ExternalPayloadAddress? PngAddress { get; private set; }
        public ExternalPayloadAddress? CustomAddress { get; private set; }

        public ValueTask<ExternalPayloadAddress> ResolveNormalizedPngAsync(
            DateOnly eventCalendarDate, ReadOnlyMemory<byte> pngBytes, CancellationToken cancellationToken = default)
        {
            PngAddress = ExternalPayloadAddressFactory.ForNormalizedPng(new DateOnly(2026, 8, 1), pngBytes.Span);
            return ValueTask.FromResult(PngAddress);
        }

        public ValueTask<ExternalPayloadAddress> ResolveCustomBinaryAsync(
            DateOnly eventCalendarDate, string formatName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            CustomAddress = ExternalPayloadAddressFactory.ForCustomBinary(new DateOnly(2026, 8, 2), bytes.Span, ".sample");
            return ValueTask.FromResult(CustomAddress);
        }
    }

    private sealed class TestEnvironment : IDisposable
    {
        private TestEnvironment(string root, RecordingConnectionFactory factory,
            ProtectedApplicationLifecycle lifecycle, ProtectedStorageSessionLease session)
        {
            Root = root;
            Factory = factory;
            Lifecycle = lifecycle;
            Session = session;
        }

        public string Root { get; }
        public RecordingConnectionFactory Factory { get; }
        public ProtectedApplicationLifecycle Lifecycle { get; }
        public ProtectedStorageSessionLease Session { get; }

        public static async Task<TestEnvironment> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "Clipensk.Storage.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var factory = new RecordingConnectionFactory();
            byte[] key = RandomNumberGenerator.GetBytes(32);
            Guid storageId = Guid.NewGuid();
            ProtectedStorageDatabaseResult result = await new ProtectedStorageDatabaseService(factory)
                .InitializeOrValidateAsync(root, storageId, key, allowInitialize: true);
            Assert.True(result.IsSuccess);
            var lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);
            Assert.True(lifecycle.TryBeginUnlock());
            lifecycle.CompleteUnlock();
            var session = ProtectedStorageSessionLease.Create(lifecycle, root, storageId, new MasterKeyLease(key));
            return new TestEnvironment(root, factory, lifecycle, session);
        }

        public void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using SqliteConnection connection = Factory.Open(Path.Combine(Root, "Current", "current.db"),
                Session.DangerousGetMasterKeyMemory(), SqliteOpenMode.ReadWrite);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            command.ExecuteNonQuery();
        }

        public void InsertTextEvent(Guid id, DateTimeOffset timestamp, params string[] values)
        {
            Execute("""
                INSERT INTO ClipboardHistoryEvent (EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate)
                VALUES ($id, $utc, $offset, 'Recorded Zone', $date);
                """, ("$id", id.ToString("D")), ("$utc", timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                ("$offset", (int)timestamp.Offset.TotalMinutes),
                ("$date", DateOnly.FromDateTime(timestamp.DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            for (int i = 0; i < values.Length; i++)
            {
                Execute("""
                    INSERT INTO ClipboardHistoryPayload (EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount, InlineCanonicalText)
                    VALUES ($id, $order, 'Text', 'Text', $count, $text);
                    """, ("$id", id.ToString("D")), ("$order", i),
                    ("$count", ClipboardCanonicalPayloadSize.MeasureUtf8Text(values[i])), ("$text", values[i]));
            }
        }

        public void Dispose()
        {
            Session.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class RecordingConnectionFactory : IKeyedSqliteConnectionFactory
    {
        private readonly SqliteClipboardHistorySinkTests.PlainSqliteConnectionFactory _inner = new();
        public List<SqliteOpenMode> Modes { get; } = new();
        public Action? AfterOpen { get; set; }
        public SqliteConnection? LastConnection { get; private set; }

        public SqliteConnection Open(string databasePath, ReadOnlyMemory<byte> masterKey, SqliteOpenMode mode)
        {
            Modes.Add(mode);
            LastConnection = _inner.Open(databasePath, masterKey, mode);
            AfterOpen?.Invoke();
            return LastConnection;
        }
    }
}
