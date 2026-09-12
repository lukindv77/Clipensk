using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedCurrentToArchiveMaintenanceServiceTests
{
    [Fact]
    public async Task TransferAsync_RefreshesCatalogAndIgnoresLateCallerCancellation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly day = today.AddDays(-3);
        var range = new JournalDateRange(day, day);
        var archiveFileName = new ArchiveFileName(41, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(archiveFileName, range);

        Guid eventId = Guid.NewGuid();
        environment.Execute($"""
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
                '{eventId:D}',
                '{day:yyyy-MM-dd}T12:00:00.0000000+00:00',
                0,
                'UTC',
                '{day:yyyy-MM-dd}',
                NULL, NULL, NULL, NULL);
            """);

        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        ArchiveSegmentDescriptor before = Assert.Single(await catalog.RebuildAsync(today));
        Assert.False(before.IsSealed);

        using var cancellation = new CancellationTokenSource();
        bool cancellationInjected = false;
        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (!cancellationInjected &&
                mode == SqliteOpenMode.ReadWrite &&
                string.Equals(
                    Path.GetFullPath(connection.DataSource),
                    Path.GetFullPath(environment.CatalogPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                cancellationInjected = true;
                cancellation.Cancel();
            }
        };

        try
        {
            var service = new ProtectedCurrentToArchiveMaintenanceService(
                environment.Session,
                environment.Factory);
            CurrentToArchiveMaintenanceResult result = await service.TransferAsync(
                archiveFileName,
                range,
                cancellation.Token);

            Assert.True(cancellationInjected);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(1, result.Transfer.CopiedEventCount);
            Assert.Equal(1, result.Transfer.PurgedEventCount);
            Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));

            ArchiveSegmentDescriptor projected = Assert.Single(result.ArchiveSegments);
            Assert.Equal(archiveFileName.FileName, projected.FileName);
            Assert.True(projected.IsSealed);

            ArchiveSegmentDescriptor persisted = Assert.Single(await catalog.ReadAsync());
            Assert.Equal(projected, persisted);
        }
        finally
        {
            environment.Factory.OnOpen = null;
        }
    }
}
