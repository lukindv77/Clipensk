using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveRotationShadowBuilderTests
{
    private static readonly DateOnly Today = new(2026, 3, 1);

    [Fact]
    public async Task BuildAsync_CountThresholdProducesReadyRangesAndOpenTail()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20), 2);
        SeedDay(environment, new DateOnly(2026, 2, 21), 2);
        SeedDay(environment, new DateOnly(2026, 2, 22), 1);

        Guid operationId = Guid.NewGuid();
        var builder = new ProtectedArchiveRotationShadowBuilder(environment.Session, environment.Factory);
        ArchiveRotationShadowPlan plan = await builder.BuildAsync(
            operationId,
            new ArchiveRotationSettings { MaxRecordCount = 2 },
            Today);

        Assert.Equal(operationId, plan.OperationId);
        Assert.Equal(2, plan.Targets.Count);
        Assert.Equal(Range(2026, 2, 20, 2026, 2, 20), plan.Targets[0].Coverage);
        Assert.Equal(Range(2026, 2, 21, 2026, 2, 21), plan.Targets[1].Coverage);
        Assert.Equal([0, 1], plan.Targets.Select(target => target.SegmentOrder));
        Assert.Equal([2L, 2L], plan.Targets.Select(target => target.ExpectedRecordCount));
        Assert.All(plan.Targets, target => Assert.True(target.ShadowPhysicalSizeBytes > 0));
        Assert.Equal(
            ["archive_000001.db", "archive_000002.db"],
            plan.Targets.Select(target => target.FileName.FileName));
        Assert.Equal(Range(2026, 2, 22, 2026, 2, 22), plan.OpenTail);
    }

    [Fact]
    public async Task BuildAsync_StagedShadowHoldsExactCoverageRowsAndPlannedIdentity()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        Guid applicationId = Guid.NewGuid();
        SeedDay(environment, new DateOnly(2026, 2, 20), 2, applicationId);
        SeedDay(environment, new DateOnly(2026, 2, 21), 3, applicationId);

        Guid operationId = Guid.NewGuid();
        var builder = new ProtectedArchiveRotationShadowBuilder(environment.Session, environment.Factory);
        ArchiveRotationShadowPlan plan = await builder.BuildAsync(
            operationId,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            Today);

        Assert.Equal(2, plan.Targets.Count);
        PendingArchiveRotationTarget first = plan.Targets[0];
        string stagedPath = Path.Combine(
            environment.Root,
            "Archive",
            $".clipensk-archive-rotation-{operationId:D}",
            first.FileName.FileName);

        Assert.True(File.Exists(stagedPath));
        Assert.Equal(2, StagedScalar(environment, stagedPath, "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(
            1,
            StagedScalar(environment, stagedPath, "SELECT COUNT(*) FROM ApplicationIdentity;"));
        Assert.Equal(
            1,
            StagedScalar(
                environment,
                stagedPath,
                $"SELECT COUNT(*) FROM DatabaseIdentity WHERE DatabaseId = '{first.DatabaseId:D}' "
                    + "AND CoverageStartDate = '2026-02-20' AND CoverageEndDate = '2026-02-20' "
                    + "AND ArchiveSplitSequence IS NULL;"));
        // Current remains the durable source: building shadows must not purge anything.
        Assert.Equal(5, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(5, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryPayload;"));
    }

    [Fact]
    public async Task BuildAsync_PhysicalSizeThresholdMeasuresClosedShadowFile()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20), 1);
        SeedDay(environment, new DateOnly(2026, 2, 21), 1);
        SeedDay(environment, new DateOnly(2026, 2, 22), 1);

        Guid operationId = Guid.NewGuid();
        var builder = new ProtectedArchiveRotationShadowBuilder(environment.Session, environment.Factory);
        ArchiveRotationShadowPlan plan = await builder.BuildAsync(
            operationId,
            new ArchiveRotationSettings { MaxBytes = 1 },
            Today);

        // MaxBytes = 1 is reached by any real SQLCipher file, so every complete day closes a range.
        Assert.Equal(3, plan.Targets.Count);
        Assert.Null(plan.OpenTail);
        foreach (PendingArchiveRotationTarget target in plan.Targets)
        {
            string stagedPath = Path.Combine(
                environment.Root,
                "Archive",
                $".clipensk-archive-rotation-{operationId:D}",
                target.FileName.FileName);
            Assert.Equal(new FileInfo(stagedPath).Length, target.ShadowPhysicalSizeBytes);
        }
    }

    [Fact]
    public async Task BuildAsync_PhysicalSizeCandidateGrowsAcrossDaysAndDiscardsOpenTail()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20), 1);
        SeedDay(environment, new DateOnly(2026, 2, 21), 1);
        SeedDay(environment, new DateOnly(2026, 2, 22), 1);

        Guid operationId = Guid.NewGuid();
        var builder = new ProtectedArchiveRotationShadowBuilder(environment.Session, environment.Factory);
        ArchiveRotationShadowPlan plan = await builder.BuildAsync(
            operationId,
            new ArchiveRotationSettings
            {
                MaxBytes = long.MaxValue / 2,
                MaxCalendarDays = 2,
                ThresholdMode = ArchiveRotationThresholdMode.Any,
            },
            Today);

        Assert.Single(plan.Targets);
        Assert.Equal(Range(2026, 2, 20, 2026, 2, 21), plan.Targets[0].Coverage);
        Assert.Equal(2, plan.Targets[0].ExpectedRecordCount);
        Assert.Equal(Range(2026, 2, 22, 2026, 2, 22), plan.OpenTail);

        string stagingDirectory = Path.Combine(
            environment.Root,
            "Archive",
            $".clipensk-archive-rotation-{operationId:D}");
        Assert.Equal(
            [plan.Targets[0].FileName.FileName],
            Directory.GetFiles(stagingDirectory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task BuildAsync_IsNoOpWhenNoClosedDaysExist()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, Today, 3);

        Guid operationId = Guid.NewGuid();
        var builder = new ProtectedArchiveRotationShadowBuilder(environment.Session, environment.Factory);
        ArchiveRotationShadowPlan plan = await builder.BuildAsync(
            operationId,
            new ArchiveRotationSettings { MaxRecordCount = 1 },
            Today);

        Assert.Empty(plan.Targets);
        Assert.Null(plan.OpenTail);
        Assert.Equal(3, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task BuildAsync_AllocatesAfterExistingCanonicalArchives()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(17, ArchiveFileName.NoSplit),
            Range(2026, 1, 1, 2026, 1, 31));
        SeedDay(environment, new DateOnly(2026, 2, 20), 1);

        var builder = new ProtectedArchiveRotationShadowBuilder(environment.Session, environment.Factory);
        ArchiveRotationShadowPlan plan = await builder.BuildAsync(
            Guid.NewGuid(),
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            Today);

        Assert.Single(plan.Targets);
        Assert.Equal("archive_000018.db", plan.Targets[0].FileName.FileName);
    }

    [Fact]
    public async Task BuildAsync_RejectsEmptyOperationIdAndUnconfiguredSettings()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var builder = new ProtectedArchiveRotationShadowBuilder(environment.Session, environment.Factory);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await builder.BuildAsync(Guid.Empty, new ArchiveRotationSettings { MaxRecordCount = 1 }, Today));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await builder.BuildAsync(Guid.NewGuid(), new ArchiveRotationSettings(), Today));
    }

    [Fact]
    public async Task BuildAsync_ReplacesStaleStagingFromAnAbandonedAttempt()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20), 1);
        Guid operationId = Guid.NewGuid();
        string stagingDirectory = Path.Combine(
            environment.Root,
            "Archive",
            $".clipensk-archive-rotation-{operationId:D}");
        Directory.CreateDirectory(stagingDirectory);
        File.WriteAllText(Path.Combine(stagingDirectory, "archive_000001.db"), "stale");

        var builder = new ProtectedArchiveRotationShadowBuilder(environment.Session, environment.Factory);
        ArchiveRotationShadowPlan plan = await builder.BuildAsync(
            operationId,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            Today);

        Assert.Single(plan.Targets);
        Assert.True(plan.Targets[0].ShadowPhysicalSizeBytes > "stale".Length);
    }

    [Fact]
    public async Task ValidateShadowSet_FailsClosedWhenStagedShadowChanged()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20), 1);
        SeedDay(environment, new DateOnly(2026, 2, 21), 1);

        Guid operationId = Guid.NewGuid();
        var builder = new ProtectedArchiveRotationShadowBuilder(environment.Session, environment.Factory);
        ArchiveRotationShadowPlan plan = await builder.BuildAsync(
            operationId,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            Today);

        string stagedPath = Path.Combine(
            environment.Root,
            "Archive",
            $".clipensk-archive-rotation-{operationId:D}",
            plan.Targets[0].FileName.FileName);
        using (SqliteConnection staged = environment.Factory.Open(
                   stagedPath,
                   environment.Key,
                   SqliteOpenMode.ReadWrite))
        {
            using SqliteCommand delete = staged.CreateCommand();
            delete.CommandText = "DELETE FROM ClipboardHistoryEvent;";
            delete.ExecuteNonQuery();
        }

        using ProtectedStorageMutationLease lease =
            await environment.Session.AcquireMutationLeaseAsync();
        Assert.Throws<InvalidDataException>(() => builder.ValidateShadowSet(plan, lease));
    }

    private static long StagedScalar(
        GlobalPolicyTestEnvironment environment,
        string stagedPath,
        string sql)
    {
        using SqliteConnection connection = environment.Factory.Open(
            stagedPath,
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void SeedDay(
        GlobalPolicyTestEnvironment environment,
        DateOnly day,
        int count,
        Guid? applicationId = null)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            SqliteOpenMode.ReadWrite);

        if (applicationId is Guid appId)
        {
            using SqliteCommand identity = connection.CreateCommand();
            identity.CommandText = """
                INSERT OR IGNORE INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                VALUES ($applicationId, '2026-01-01T00:00:00.0000000+00:00');
                """;
            identity.Parameters.AddWithValue("$applicationId", appId.ToString("D"));
            identity.ExecuteNonQuery();
        }

        for (int index = 0; index < count; index++)
        {
            Guid eventId = Guid.NewGuid();
            using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO ClipboardHistoryEvent (
                        EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                        SourceApplicationId, SourceProcessId, SourceExecutablePath,
                        SourceApplicationUserModelId)
                    VALUES ($eventId, $eventUtc, 0, 'UTC', $calendarDate,
                        $sourceApplicationId, 1234, 'C:\\Apps\\Source.exe', 'Source.App');
                    """;
                insert.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
                insert.Parameters.AddWithValue(
                    "$eventUtc",
                    $"{day:yyyy-MM-dd}T{index:00}:00:00.0000000+00:00");
                insert.Parameters.AddWithValue(
                    "$calendarDate",
                    day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue(
                    "$sourceApplicationId",
                    applicationId is Guid source ? source.ToString("D") : DBNull.Value);
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

    private static JournalDateRange Range(
        int startYear, int startMonth, int startDay,
        int endYear, int endMonth, int endDay) =>
        new(new DateOnly(startYear, startMonth, startDay), new DateOnly(endYear, endMonth, endDay));
}
