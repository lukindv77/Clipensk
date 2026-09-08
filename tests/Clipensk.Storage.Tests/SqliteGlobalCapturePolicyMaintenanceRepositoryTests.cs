using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqliteGlobalCapturePolicyMaintenanceRepositoryTests
{
    [Fact]
    public async Task ReadAsync_EmptyTableReturnsNullReadOnly()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqliteGlobalCapturePolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);
        environment.Factory.Modes.Clear();

        Assert.Null(await repository.ReadAsync());
        Assert.Equal(new[] { SqliteOpenMode.ReadOnly }, environment.Factory.Modes);
    }

    [Fact]
    public async Task ReadAsync_ReturnsExactPersistedState()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("""
            INSERT INTO GlobalCapturePolicyMaintenance (
                SingletonId, OperationId, Phase, StartedAtUtc)
            VALUES (
                1,
                '11111111-1111-1111-1111-111111111111',
                'CatalogRebuild',
                '2026-09-08T08:00:00.0000000+00:00');
            """);
        var repository = new SqliteGlobalCapturePolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);

        GlobalCapturePolicyMaintenanceState? state = await repository.ReadAsync();

        Assert.NotNull(state);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), state.OperationId);
        Assert.Equal(GlobalCapturePolicyMaintenancePhase.CatalogRebuild, state.Phase);
        Assert.Equal(TimeSpan.Zero, state.StartedAtUtc.Offset);
    }

    [Theory]
    [InlineData("NOT-A-GUID", "ArchiveCleanup", "2026-09-08T08:00:00.0000000+00:00")]
    [InlineData("11111111-1111-1111-1111-111111111111", "Unknown", "2026-09-08T08:00:00.0000000+00:00")]
    [InlineData("11111111-1111-1111-1111-111111111111", "TrashCollection", "2026-09-08T08:00:00.0000000+03:00")]
    public async Task ReadAsync_MalformedPersistedStateFailsClosed(
        string operationId,
        string phase,
        string startedAtUtc)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using (SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            SqliteOpenMode.ReadWrite))
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = $"""
                PRAGMA ignore_check_constraints = ON;
                INSERT INTO GlobalCapturePolicyMaintenance (
                    SingletonId, OperationId, Phase, StartedAtUtc)
                VALUES (1, '{operationId}', '{phase}', '{startedAtUtc}');
                """;
            insert.ExecuteNonQuery();
        }
        var repository = new SqliteGlobalCapturePolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.ReadAsync());
    }

    [Fact]
    public async Task ReadAsync_LockedSessionCancelsBeforeOpen()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        Assert.True(environment.Lifecycle.TryBeginLock());
        environment.Factory.Modes.Clear();
        var repository = new SqliteGlobalCapturePolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repository.ReadAsync());
        Assert.Empty(environment.Factory.Modes);
    }
}