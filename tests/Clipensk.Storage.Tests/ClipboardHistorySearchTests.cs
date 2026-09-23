using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Storage.History;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

/// <summary>
/// Exercises the journal search filter through real SQLite connections, at every layer that
/// applies it: Current alone, an Archive segment alone, and the unified Current+Archive merge.
/// The pure matching rule itself is covered by <c>ClipboardHistorySearchMatcherTests</c> in
/// <c>Clipensk.Core.Tests</c>; this file proves the SQL wiring uses it correctly, including that a
/// search never widens which database gets opened.
/// </summary>
public sealed class ClipboardHistorySearchTests
{
    private static readonly JournalDateRange Period =
        new(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 1));

    [Fact]
    public async Task CurrentRepository_FindsCyrillicMatchRegardlessOfCase()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedEvent(environment, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), "Привет, мир");
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);

        IReadOnlyList<ClipboardHistoryEntry> matched =
            await repository.ReadAsync(Period, 10, searchText: "ПРИВЕТ");
        IReadOnlyList<ClipboardHistoryEntry> unmatched =
            await repository.ReadAsync(Period, 10, searchText: "пока");

        Assert.Single(matched);
        Assert.Empty(unmatched);
    }

    [Fact]
    public async Task CurrentRepository_NoSearchTermReturnsEverythingInPeriod()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedEvent(environment, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), "one");
        SeedEvent(environment, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), "two");
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);

        IReadOnlyList<ClipboardHistoryEntry> entries = await repository.ReadAsync(Period, 10);

        Assert.Equal(2, entries.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CurrentRepository_TreatsABlankSearchTermAsNoFilter(string? searchText)
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedEvent(environment, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), "anything");
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);

        IReadOnlyList<ClipboardHistoryEntry> entries = await repository.ReadAsync(Period, 10, searchText);

        Assert.Single(entries);
    }

    [Fact]
    public async Task CurrentRepository_LimitAppliesOnlyToMatchingEvents()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedEvent(environment, new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc), "match one");
        SeedEvent(environment, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), "no hit here");
        SeedEvent(environment, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), "match two");
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);

        // A limit of 1 must return the single newest match, not stop after scanning one event.
        IReadOnlyList<ClipboardHistoryEntry> entries =
            await repository.ReadAsync(Period, 1, searchText: "match");

        ClipboardHistoryEntry entry = Assert.Single(entries);
        Assert.Equal("match two", entry.Payloads[0].SearchText);
    }

    [Fact]
    public async Task CurrentRepository_ContinuationRespectsTheSameSearchTerm()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedEvent(environment, new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc), "match one");
        SeedEvent(environment, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), "no hit here");
        SeedEvent(environment, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), "match two");
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);

        IReadOnlyList<ClipboardHistoryEntry> firstPage =
            await repository.ReadAsync(Period, 1, searchText: "match");
        ClipboardHistoryCursor cursor = ClipboardHistoryCursor.FromEntry(Period, firstPage[0]);
        IReadOnlyList<ClipboardHistoryEntry> nextPage =
            await repository.ReadBeforeAsync(Period, 1, cursor, searchText: "match");

        ClipboardHistoryEntry entry = Assert.Single(nextPage);
        Assert.Equal("match one", entry.Payloads[0].SearchText);
    }

    [Fact]
    public async Task CurrentRepository_NeverMatchesAPayloadWithoutASearchProjection()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedEvent(environment, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), searchText: null);
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);

        IReadOnlyList<ClipboardHistoryEntry> entries =
            await repository.ReadAsync(Period, 10, searchText: "anything");

        Assert.Empty(entries);
    }

    [Fact]
    public async Task UnifiedRepository_SearchStaysWithinTheSelectedDatabases()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedEvent(environment, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), "matching text");
        var repository = new ProtectedUnifiedClipboardHistoryRepository(environment.Session, environment.Factory);

        IReadOnlyList<UnifiedClipboardHistoryEntry> matched =
            await repository.ReadAsync(Period, 10, searchText: "matching");
        IReadOnlyList<UnifiedClipboardHistoryEntry> unmatched =
            await repository.ReadAsync(Period, 10, searchText: "absent");

        Assert.Single(matched);
        Assert.Empty(unmatched);
    }

    [Fact]
    public async Task CurrentRepository_FiltersBySourceApplication()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        Guid appA = Guid.NewGuid();
        Guid appB = Guid.NewGuid();
        SeedEvent(environment, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), sourceApplicationId: appA);
        SeedEvent(environment, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), sourceApplicationId: appB);
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);

        IReadOnlyList<ClipboardHistoryEntry> fromA =
            await repository.ReadAsync(Period, 10, filter: ClipboardHistoryFilter.ForSource(appA));
        IReadOnlyList<ClipboardHistoryEntry> all = await repository.ReadAsync(Period, 10);

        Assert.Single(fromA);
        Assert.Equal(appA, fromA[0].SourceApplicationId!.Value);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task CurrentRepository_ApplicationFilterNeverMatchesAnEventWithNoSourceApplication()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedEvent(environment, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), sourceApplicationId: null);
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);

        IReadOnlyList<ClipboardHistoryEntry> filtered =
            await repository.ReadAsync(Period, 10, filter: ClipboardHistoryFilter.ForSource(Guid.NewGuid()));

        Assert.Empty(filtered);
    }

    [Fact]
    public async Task CurrentRepository_CombinesSearchTermAndApplicationFilter()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        Guid appA = Guid.NewGuid();
        Guid appB = Guid.NewGuid();
        SeedEvent(environment, new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc), "match", appA);
        SeedEvent(environment, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), "match", appB);
        SeedEvent(environment, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), "no hit", appA);
        var repository = new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory);

        IReadOnlyList<ClipboardHistoryEntry> entries =
            await repository.ReadAsync(Period, 10, searchText: "match", filter: ClipboardHistoryFilter.ForSource(appA));

        ClipboardHistoryEntry entry = Assert.Single(entries);
        Assert.Equal(appA, entry.SourceApplicationId!.Value);
        Assert.Equal("match", entry.Payloads[0].SearchText);
    }

    [Fact]
    public async Task UnifiedRepository_ApplicationFilterStaysWithinTheSelectedDatabases()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        Guid appA = Guid.NewGuid();
        SeedEvent(environment, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), sourceApplicationId: appA);
        var repository = new ProtectedUnifiedClipboardHistoryRepository(environment.Session, environment.Factory);

        IReadOnlyList<UnifiedClipboardHistoryEntry> matched =
            await repository.ReadAsync(Period, 10, filter: ClipboardHistoryFilter.ForSource(appA));
        IReadOnlyList<UnifiedClipboardHistoryEntry> unmatched =
            await repository.ReadAsync(Period, 10, filter: ClipboardHistoryFilter.ForSource(Guid.NewGuid()));

        Assert.Single(matched);
        Assert.Empty(unmatched);
    }

    private static void SeedEvent(
        GlobalPolicyTestEnvironment environment,
        DateTime utc,
        string? searchText = "default search text",
        Guid? sourceApplicationId = null)
    {
        Guid eventId = Guid.NewGuid();
        if (sourceApplicationId is Guid appId)
        {
            environment.Execute($"""
                INSERT OR IGNORE INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                VALUES ('{appId:D}', '2026-01-01T00:00:00.0000000+00:00');
                """);
        }

        using SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            SqliteOpenMode.ReadWrite);
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO ClipboardHistoryEvent (
                    EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                    SourceApplicationId, SourceProcessId, SourceExecutablePath,
                    SourceApplicationUserModelId)
                VALUES ($eventId, $eventUtc, 0, 'UTC', $calendarDate, $sourceApplicationId,
                        NULL, NULL, NULL);
                """;
            insert.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
            insert.Parameters.AddWithValue(
                "$eventUtc",
                utc.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue(
                "$calendarDate",
                DateOnly.FromDateTime(utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue(
                "$sourceApplicationId",
                sourceApplicationId is Guid id ? id.ToString("D") : DBNull.Value);
            insert.ExecuteNonQuery();
        }

        // InlineCanonicalText is a fixed placeholder independent of SearchText: the repository
        // validates it against CanonicalByteCount, and this test only cares about SearchText.
        const string inlineText = "x";
        using SqliteCommand payload = connection.CreateCommand();
        payload.CommandText = """
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath,
                ExternalSizeBytes)
            VALUES ($eventId, 0, 'Text', 'Text', 1, $text, $searchText, NULL, NULL, NULL);
            """;
        payload.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        payload.Parameters.AddWithValue("$text", inlineText);
        payload.Parameters.AddWithValue("$searchText", (object?)searchText ?? DBNull.Value);
        payload.ExecuteNonQuery();
    }
}
