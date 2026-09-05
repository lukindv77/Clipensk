using System.Globalization;
using System.Security.Cryptography;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.History;

public sealed class SqliteClipboardHistorySink : IClipboardAcceptedCaptureSink
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IClipboardExternalPayloadAddressResolver _externalPayloadResolver;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;
    private readonly string _filesRootPath;

    public SqliteClipboardHistorySink(
        ProtectedStorageSessionLease session,
        IClipboardExternalPayloadAddressResolver externalPayloadResolver,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _externalPayloadResolver = externalPayloadResolver ??
            throw new ArgumentNullException(nameof(externalPayloadResolver));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();

        string dataRootPath = Path.GetFullPath(session.DataRootPath);
        _currentDatabasePath = Path.Combine(dataRootPath, "Current", "current.db");
        _filesRootPath = Path.Combine(dataRootPath, "Files");
    }

    public async ValueTask StoreAsync(
        ClipboardAcceptedCapture capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _session.CancellationToken,
                cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        PreparedPayload[] preparedPayloads = await PreparePayloadsAsync(capture, token)
            .ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenValidatedCurrent(token);
        using SqliteTransaction transaction = connection.BeginTransaction();

        Guid eventId = Guid.NewGuid();
        InsertEvent(connection, transaction, eventId, capture.CaptureContext);
        for (int index = 0; index < preparedPayloads.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            InsertPayload(connection, transaction, eventId, index, preparedPayloads[index]);
        }

        // This is the last cancellation boundary. Once COMMIT succeeds, the durable
        // write has happened and StoreAsync must report success rather than a late
        // cancellation that would make the caller believe the event was not stored.
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private async ValueTask<PreparedPayload[]> PreparePayloadsAsync(
        ClipboardAcceptedCapture capture,
        CancellationToken cancellationToken)
    {
        var result = new PreparedPayload[capture.Content.Count];
        DateOnly eventCalendarDate = capture.CaptureContext.Request.EventTime.CalendarDate;

        for (int index = 0; index < capture.Content.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClipboardCapturedContent content = capture.Content[index];
            long canonicalByteCount = content.CanonicalByteCount ??
                throw new InvalidDataException(
                    "Accepted clipboard payload must have a canonical byte count before persistence.");
            if (canonicalByteCount < 0)
            {
                throw new InvalidDataException("Canonical clipboard payload byte count cannot be negative.");
            }

            string formatName = content.SelectedFormat.FormatName;
            if (string.IsNullOrWhiteSpace(formatName))
            {
                throw new InvalidDataException("Accepted clipboard payload must retain its format name.");
            }

            result[index] = content switch
            {
                ClipboardCapturedTextContent text => new PreparedPayload(
                    formatName,
                    "Text",
                    canonicalByteCount,
                    text.Value,
                    text.SearchText,
                    null),

                ClipboardCapturedLinkContent link => new PreparedPayload(
                    formatName,
                    "Link",
                    canonicalByteCount,
                    link.Value.OriginalString,
                    null,
                    null),

                ClipboardCapturedStorageItemsContent storageItems => new PreparedPayload(
                    formatName,
                    "StorageItems",
                    canonicalByteCount,
                    storageItems.CanonicalRepresentation,
                    null,
                    null),

                ClipboardCapturedPngImageContent png => new PreparedPayload(
                    formatName,
                    "PngImage",
                    canonicalByteCount,
                    null,
                    null,
                    ValidateExternalAddress(
                        await _externalPayloadResolver.ResolveNormalizedPngAsync(
                            eventCalendarDate,
                            png.PngBytes,
                            cancellationToken).ConfigureAwait(false),
                        png.PngBytes.Span,
                        canonicalByteCount)),

                ClipboardCapturedCustomBinaryContent binary => new PreparedPayload(
                    formatName,
                    "CustomBinary",
                    canonicalByteCount,
                    null,
                    null,
                    ValidateExternalAddress(
                        await _externalPayloadResolver.ResolveCustomBinaryAsync(
                            eventCalendarDate,
                            formatName,
                            binary.Bytes,
                            cancellationToken).ConfigureAwait(false),
                        binary.Bytes.Span,
                        canonicalByteCount)),

                _ => throw new InvalidDataException(
                    $"Unsupported accepted clipboard payload type: {content.GetType().FullName}.")
            };
        }

        return result;
    }

    private ExternalPayloadAddress ValidateExternalAddress(
        ExternalPayloadAddress address,
        ReadOnlySpan<byte> payloadBytes,
        long canonicalByteCount)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.SizeBytes != canonicalByteCount || address.SizeBytes != payloadBytes.Length)
        {
            throw new InvalidDataException(
                "External payload address size does not match the accepted canonical payload size.");
        }

        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        if (!string.Equals(address.Sha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "External payload address SHA-256 does not match the accepted payload bytes.");
        }

        if (string.IsNullOrWhiteSpace(address.RelativePath) ||
            Path.IsPathRooted(address.RelativePath))
        {
            throw new InvalidDataException("External payload address must use a relative Files path.");
        }

        string filesRoot = Path.GetFullPath(_filesRootPath);
        string candidate = Path.GetFullPath(Path.Combine(filesRoot, address.RelativePath));
        string rootWithSeparator = filesRoot.EndsWith(Path.DirectorySeparatorChar)
            ? filesRoot
            : filesRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("External payload address escapes the configured Files root.");
        }

        return address;
    }

    private SqliteConnection OpenValidatedCurrent(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        try
        {
            EnableForeignKeys(connection);
            ValidateCurrentDatabase(connection);
            ClipboardHistorySqlSchema.ValidateTables(connection);
            cancellationToken.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void ValidateCurrentDatabase(SqliteConnection connection)
    {
        using SqliteCommand identity = connection.CreateCommand();
        identity.CommandText = """
            SELECT StorageId, DatabaseRole, SchemaVersion
            FROM DatabaseIdentity
            WHERE SingletonId = 1;
            """;
        using SqliteDataReader reader = identity.ExecuteReader();
        if (!reader.Read() ||
            !Guid.TryParse(reader.GetString(0), out Guid storageId) ||
            storageId != _session.StorageId ||
            !string.Equals(reader.GetString(1), DatabaseRole.Current.ToString(), StringComparison.Ordinal) ||
            reader.GetInt32(2) < ClipboardHistorySqlSchema.RequiredCurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "Clipboard history sink requires the expected Current database at schema v4 or later.");
        }

        int schemaVersion = reader.GetInt32(2);
        reader.Close();

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                "Current database user_version does not match DatabaseIdentity for history persistence.");
        }
    }

    private static void InsertEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid eventId,
        ClipboardCaptureContext captureContext)
    {
        EventTimeContext eventTime = captureContext.Request.EventTime ??
            throw new InvalidDataException("Clipboard capture is missing its EventTimeContext.");
        int offsetMinutes = checked((int)eventTime.Offset.TotalMinutes);
        ClipboardSourceApplication? source = captureContext.SourceApplication;

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ClipboardHistoryEvent (
                EventId,
                EventUtc,
                LocalOffsetMinutes,
                WindowsTimeZoneId,
                CalendarDate,
                SourceApplicationId,
                SourceProcessId,
                SourceExecutablePath,
                SourceApplicationUserModelId)
            VALUES (
                $eventId,
                $eventUtc,
                $localOffsetMinutes,
                $windowsTimeZoneId,
                $calendarDate,
                $sourceApplicationId,
                $sourceProcessId,
                $sourceExecutablePath,
                $sourceApplicationUserModelId);
            """;
        command.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        command.Parameters.AddWithValue(
            "$eventUtc",
            eventTime.UtcTimestamp.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$localOffsetMinutes", offsetMinutes);
        command.Parameters.AddWithValue("$windowsTimeZoneId", eventTime.WindowsTimeZoneId);
        command.Parameters.AddWithValue(
            "$calendarDate",
            eventTime.CalendarDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$sourceApplicationId",
            captureContext.SourceApplicationId is null
                ? DBNull.Value
                : captureContext.SourceApplicationId.ToString());
        command.Parameters.AddWithValue(
            "$sourceProcessId",
            source is null ? DBNull.Value : (long)source.Value.ProcessId);
        command.Parameters.AddWithValue(
            "$sourceExecutablePath",
            source?.ExecutablePath is null ? DBNull.Value : source.Value.ExecutablePath);
        command.Parameters.AddWithValue(
            "$sourceApplicationUserModelId",
            source?.ApplicationUserModelId is null ? DBNull.Value : source.Value.ApplicationUserModelId);
        command.ExecuteNonQuery();
    }

    private static void InsertPayload(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid eventId,
        int payloadOrder,
        PreparedPayload payload)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ClipboardHistoryPayload (
                EventId,
                PayloadOrder,
                FormatName,
                PayloadKind,
                CanonicalByteCount,
                InlineCanonicalText,
                SearchText,
                ExternalSha256,
                ExternalRelativePath,
                ExternalSizeBytes)
            VALUES (
                $eventId,
                $payloadOrder,
                $formatName,
                $payloadKind,
                $canonicalByteCount,
                $inlineCanonicalText,
                $searchText,
                $externalSha256,
                $externalRelativePath,
                $externalSizeBytes);
            """;
        command.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        command.Parameters.AddWithValue("$payloadOrder", payloadOrder);
        command.Parameters.AddWithValue("$formatName", payload.FormatName);
        command.Parameters.AddWithValue("$payloadKind", payload.PayloadKind);
        command.Parameters.AddWithValue("$canonicalByteCount", payload.CanonicalByteCount);
        command.Parameters.AddWithValue(
            "$inlineCanonicalText",
            payload.InlineCanonicalText is null ? DBNull.Value : payload.InlineCanonicalText);
        command.Parameters.AddWithValue(
            "$searchText",
            payload.SearchText is null ? DBNull.Value : payload.SearchText);
        command.Parameters.AddWithValue(
            "$externalSha256",
            payload.ExternalAddress is null ? DBNull.Value : payload.ExternalAddress.Sha256);
        command.Parameters.AddWithValue(
            "$externalRelativePath",
            payload.ExternalAddress is null ? DBNull.Value : payload.ExternalAddress.RelativePath);
        command.Parameters.AddWithValue(
            "$externalSizeBytes",
            payload.ExternalAddress is null ? DBNull.Value : payload.ExternalAddress.SizeBytes);
        command.ExecuteNonQuery();
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private sealed record PreparedPayload(
        string FormatName,
        string PayloadKind,
        long CanonicalByteCount,
        string? InlineCanonicalText,
        string? SearchText,
        ExternalPayloadAddress? ExternalAddress);
}
