using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.Data.Sqlite;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

/// <summary>
/// Seeds identities, group memberships, archives and history rows for the application-group and
/// history-purge tests. An <see langword="null"/> archive means Current.
/// </summary>
internal static class ApplicationGroupTestData
{
    public const string EventUtc = "2026-01-10T12:00:00.0000000Z";

    public static readonly JournalDateRange ArchiveCoverage =
        new(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

    public static DurableApplicationId InsertIdentity(GlobalPolicyTestEnvironment environment)
    {
        DurableApplicationId applicationId = DurableApplicationId.New();
        environment.Execute($"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{applicationId}', '2026-09-10T00:00:00.0000000+00:00');
            """);
        return applicationId;
    }

    public static void AddMember(
        GlobalPolicyTestEnvironment environment,
        DurableApplicationId member,
        DurableApplicationId root) =>
        environment.Execute($"""
            INSERT INTO ApplicationGroupMember VALUES (
                '{member}', '{root}', NULL, '2026-09-22T10:00:00.0000000+00:00');
            """);

    public static async Task<ArchiveFileName> CreateArchiveAsync(
        GlobalPolicyTestEnvironment environment,
        int baseNumber = 31)
    {
        var fileName = new ArchiveFileName(baseNumber, ArchiveFileName.NoSplit);
        await new ProtectedArchiveDatabaseService(environment.Session, environment.Factory)
            .CreateAsync(fileName, ArchiveCoverage);
        return fileName;
    }

    public static void InsertArchiveIdentity(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName archive,
        DurableApplicationId applicationId) =>
        Execute(environment, archive, $"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{applicationId}', '2026-09-10T00:00:00.0000000+00:00');
            """);

    public static Guid InsertEvent(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName? archive,
        DurableApplicationId? source,
        params (string FormatName, string PayloadKind)[] payloads) =>
        InsertEvent(environment, archive, source, payloads, secretText: "text");

    public static Guid InsertEvent(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName? archive,
        DurableApplicationId? source,
        (string FormatName, string PayloadKind) payload,
        string secretText) =>
        InsertEvent(environment, archive, source, [payload], secretText);

    public static Guid InsertEvent(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName? archive,
        DurableApplicationId? source,
        (string FormatName, string PayloadKind)[] payloads,
        string secretText,
        Guid? eventId = null)
    {
        Guid id = eventId ?? Guid.NewGuid();
        string sourceSql = source is null ? "NULL" : $"'{source}'";
        var sql = new StringBuilder($"""
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES ('{id:D}', '{EventUtc}', 0, 'UTC', '2026-01-10',
                {sourceSql}, NULL, NULL, NULL);
            """);
        for (int order = 0; order < payloads.Length; order++)
        {
            int byteCount = Encoding.UTF8.GetByteCount(secretText);
            sql.Append($"""
                INSERT INTO ClipboardHistoryPayload (
                    EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                    InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath, ExternalSizeBytes)
                VALUES ('{id:D}', {order}, '{payloads[order].FormatName}', '{payloads[order].PayloadKind}', {byteCount},
                    '{secretText}', '{secretText}', NULL, NULL, NULL);
                """);
        }

        Execute(environment, archive, sql.ToString());
        return id;
    }

    /// <summary>
    /// Adds an archived PNG record backed by a real external file; returns the paths the file has
    /// now and would have in Trash after collection on <paramref name="deletionDate"/>.
    /// </summary>
    public static (Guid EventId, string SourcePath, string TrashPath) InsertArchivedImage(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName archive,
        DurableApplicationId source,
        DateOnly deletionDate)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("png-bytes-for-history-purge");
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string relativePath = "2026-01-10/" + sha + ".png";
        string sourcePath = Path.Combine(environment.Root, "Files", "2026-01-10", sha + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllBytes(sourcePath, bytes);

        Guid eventId = Guid.NewGuid();
        Execute(environment, archive, $"""
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES ('{eventId:D}', '{EventUtc}', 0, 'UTC', '2026-01-10',
                '{source}', NULL, NULL, NULL);
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath, ExternalSizeBytes)
            VALUES ('{eventId:D}', 0, 'PNG', 'PngImage', {bytes.Length},
                NULL, NULL, '{sha}', '{relativePath}', {bytes.Length});
            """);

        string trashPath = Path.Combine(
            environment.Root,
            "Trash",
            deletionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "2026-01-10",
            sha + ".png");
        return (eventId, sourcePath, trashPath);
    }

    public static void Execute(GlobalPolicyTestEnvironment environment, ArchiveFileName? archive, string sql)
    {
        if (archive is null)
        {
            environment.Execute(sql);
            return;
        }

        using SqliteConnection connection = environment.Factory.Open(
            ArchivePath(environment, archive.Value),
            environment.Key,
            SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static long Scalar(GlobalPolicyTestEnvironment environment, ArchiveFileName? archive, string sql)
    {
        if (archive is null)
        {
            return environment.Scalar(sql);
        }

        using SqliteConnection connection = environment.Factory.Open(
            ArchivePath(environment, archive.Value),
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static long PayloadCount(GlobalPolicyTestEnvironment environment, ArchiveFileName? archive, Guid eventId) =>
        Scalar(environment, archive, $"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';");

    public static long EventCount(GlobalPolicyTestEnvironment environment, ArchiveFileName? archive, Guid eventId) =>
        Scalar(environment, archive, $"SELECT COUNT(*) FROM ClipboardHistoryEvent WHERE EventId = '{eventId:D}';");

    public static string ArchivePath(GlobalPolicyTestEnvironment environment, ArchiveFileName archive) =>
        Path.Combine(environment.Root, "Archive", archive.FileName);
}
