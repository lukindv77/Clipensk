using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveRotationStartServiceTests
{
    private static readonly DateOnly Today = new(2026, 3, 1);

    [Fact]
    public async Task StartAsync_CommitsReadyToPublishMarkerWithoutTouchingCurrentOrArchive()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20));
        SeedDay(environment, new DateOnly(2026, 2, 21));

        var service = new ProtectedArchiveRotationStartService(environment.Session, environment.Factory);
        ArchiveRotationStartResult result = await service.StartAsync(
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            Today);

        Assert.True(result.Started);
        Assert.Equal(2, result.Targets.Count);

        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        PendingArchiveRotationOperation? pending = await repository.ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(result.OperationId, pending!.OperationId);
        Assert.Equal(ArchiveRotationPhase.ReadyToPublish, pending.Phase);
        Assert.Equal(result.Targets, pending.Targets);

        // Current is untouched and nothing was published into the canonical Archive set.
        Assert.Equal(2, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Empty(Directory.GetFiles(
            Path.Combine(environment.Root, "Archive"),
            "archive_*.db"));
        foreach (PendingArchiveRotationTarget target in result.Targets)
        {
            Assert.True(File.Exists(Path.Combine(
                environment.Root,
                "Archive",
                $".clipensk-archive-rotation-{result.OperationId:D}",
                target.FileName.FileName)));
        }
    }

    [Fact]
    public async Task StartAsync_IsNoOpAndCommitsNoMarkerWhenNoRangeIsReady()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20));

        var service = new ProtectedArchiveRotationStartService(environment.Session, environment.Factory);
        ArchiveRotationStartResult result = await service.StartAsync(
            new ArchiveRotationSettings { MaxCalendarDays = 5 },
            Today);

        Assert.False(result.Started);
        Assert.Empty(result.Targets);
        Assert.Equal(
            new JournalDateRange(new DateOnly(2026, 2, 20), new DateOnly(2026, 2, 20)),
            result.OpenTail);

        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        Assert.Null(await repository.ReadAsync());
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Empty(Directory.GetDirectories(
            Path.Combine(environment.Root, "Archive"),
            ".clipensk-archive-rotation-*"));
    }

    [Fact]
    public async Task StartAsync_IsNoOpWithoutClosedDays()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, Today);

        var service = new ProtectedArchiveRotationStartService(environment.Session, environment.Factory);
        ArchiveRotationStartResult result = await service.StartAsync(
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            Today);

        Assert.False(result.Started);
        Assert.Null(result.OpenTail);
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        Assert.Null(await repository.ReadAsync());
    }

    [Fact]
    public async Task StartAsync_IsBlockedByAnExistingPendingRotation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20));
        SeedDay(environment, new DateOnly(2026, 2, 21));

        var service = new ProtectedArchiveRotationStartService(environment.Session, environment.Factory);
        _ = await service.StartAsync(new ArchiveRotationSettings { MaxCalendarDays = 1 }, Today);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.StartAsync(new ArchiveRotationSettings { MaxCalendarDays = 1 }, Today));
    }

    [Fact]
    public async Task StartAsync_RejectsUnconfiguredSettingsBeforeAnyWork()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveRotationStartService(environment.Session, environment.Factory);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.StartAsync(new ArchiveRotationSettings(), Today));
        Assert.Empty(Directory.GetDirectories(
            Path.Combine(environment.Root, "Archive"),
            ".clipensk-archive-rotation-*"));
    }

    [Fact]
    public async Task StartAsync_ThenRecoveryCompletesTheWholeRotation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20));
        SeedDay(environment, new DateOnly(2026, 2, 21));
        SeedDay(environment, new DateOnly(2026, 2, 22));

        var start = new ProtectedArchiveRotationStartService(environment.Session, environment.Factory);
        ArchiveRotationStartResult started = await start.StartAsync(
            new ArchiveRotationSettings { MaxCalendarDays = 2 },
            Today);

        Assert.True(started.Started);
        Assert.Single(started.Targets);
        Assert.Equal(
            new JournalDateRange(new DateOnly(2026, 2, 22), new DateOnly(2026, 2, 22)),
            started.OpenTail);

        var recovery = new ProtectedArchiveRotationRecoveryService(
            environment.Session,
            environment.Factory);
        ArchiveRotationRecoveryResult completed = await recovery.RecoverAsync(Today);

        Assert.True(completed.HadPendingOperation);
        Assert.Equal(ArchiveRotationPhase.ReadyToPublish, completed.ResumedFromPhase);
        Assert.Contains(
            completed.Descriptors,
            descriptor => descriptor.FileName == started.Targets[0].FileName.FileName);

        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        Assert.Null(await repository.ReadAsync());
        // Only the open tail remains in Current.
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(
            1,
            environment.Scalar(
                "SELECT COUNT(*) FROM ClipboardHistoryEvent WHERE CalendarDate = '2026-02-22';"));
    }

    private static void SeedDay(GlobalPolicyTestEnvironment environment, DateOnly day)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            SqliteOpenMode.ReadWrite);
        Guid eventId = Guid.NewGuid();
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO ClipboardHistoryEvent (
                    EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                    SourceApplicationId, SourceProcessId, SourceExecutablePath,
                    SourceApplicationUserModelId)
                VALUES ($eventId, $eventUtc, 0, 'UTC', $calendarDate, NULL, NULL, NULL, NULL);
                """;
            insert.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
            insert.Parameters.AddWithValue("$eventUtc", $"{day:yyyy-MM-dd}T09:00:00.0000000+00:00");
            insert.Parameters.AddWithValue(
                "$calendarDate",
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        using SqliteCommand payload = connection.CreateCommand();
        payload.CommandText = """
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath,
                ExternalSizeBytes)
            VALUES ($eventId, 0, 'Text', 'Text', 4, 'text', 'text', NULL, NULL, NULL);
            """;
        payload.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        payload.ExecuteNonQuery();
    }
}
