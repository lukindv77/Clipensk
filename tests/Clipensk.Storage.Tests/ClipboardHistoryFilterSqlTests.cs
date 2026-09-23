using Clipensk.Core.History;
using Clipensk.Storage.History;
using Xunit;
using static Clipensk.Storage.Tests.ApplicationGroupTestData;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

/// <summary>
/// Proves the <see cref="ClipboardHistoryFilter"/> SQL wiring on Current; the Archive and unified
/// paths share the same predicates and are exercised through the purge preview's detailed view.
/// </summary>
public sealed class ClipboardHistoryFilterSqlTests
{
    [Fact]
    public async Task SourceSet_MatchesOnlyItsMembersAndAnEmptySetMatchesNothing()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DurableApplicationId first = InsertIdentity(environment);
        DurableApplicationId second = InsertIdentity(environment);
        DurableApplicationId third = InsertIdentity(environment);
        Guid fromFirst = InsertEvent(environment, null, first, ("Text", "Text"));
        Guid fromSecond = InsertEvent(environment, null, second, ("Text", "Text"));
        InsertEvent(environment, null, third, ("Text", "Text"));
        InsertEvent(environment, null, null, ("Text", "Text"));

        IReadOnlyList<ClipboardHistoryEntry> both =
            await Read(environment, new ClipboardHistoryFilter([first.Value, second.Value]));
        IReadOnlyList<ClipboardHistoryEntry> none = await Read(environment, new ClipboardHistoryFilter([]));
        IReadOnlyList<ClipboardHistoryEntry> all = await Read(environment, filter: null);

        Assert.Equal(new[] { fromFirst, fromSecond }.Order(), both.Select(static entry => entry.EventId).Order());
        Assert.Empty(none);
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public async Task FormatName_MatchesExactlyAndReturnsTheWholeRecord()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DurableApplicationId source = InsertIdentity(environment);
        Guid withHtml = InsertEvent(environment, null, source, ("Text", "Text"), ("HTML Format", "Text"));
        InsertEvent(environment, null, source, ("Text", "Text"));
        InsertEvent(environment, null, source);

        IReadOnlyList<ClipboardHistoryEntry> html =
            await Read(environment, new ClipboardHistoryFilter(formatName: "HTML Format"));
        IReadOnlyList<ClipboardHistoryEntry> differentCase =
            await Read(environment, new ClipboardHistoryFilter(formatName: "html format"));

        ClipboardHistoryEntry entry = Assert.Single(html);
        Assert.Equal(withHtml, entry.EventId);
        Assert.Equal(["Text", "HTML Format"], entry.Payloads.Select(static payload => payload.FormatName));
        Assert.Empty(differentCase);
    }

    [Fact]
    public async Task SourcesFormatAndSearchCombine()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DurableApplicationId inScope = InsertIdentity(environment);
        DurableApplicationId outOfScope = InsertIdentity(environment);
        Guid expected = InsertEvent(environment, null, inScope, [("HTML Format", "Text")], "needle", null);
        InsertEvent(environment, null, inScope, [("HTML Format", "Text")], "hay", null);
        InsertEvent(environment, null, inScope, [("Text", "Text")], "needle", null);
        InsertEvent(environment, null, outOfScope, [("HTML Format", "Text")], "needle", null);

        IReadOnlyList<ClipboardHistoryEntry> entries =
            await new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory)
                .ReadAsync(
                    ArchiveCoverage,
                    100,
                    searchText: "needle",
                    filter: new ClipboardHistoryFilter([inScope.Value], "HTML Format"));

        Assert.Equal(expected, Assert.Single(entries).EventId);
    }

    private static async Task<IReadOnlyList<ClipboardHistoryEntry>> Read(
        GlobalPolicyTestEnvironment environment,
        ClipboardHistoryFilter? filter) =>
        await new SqliteCurrentClipboardHistoryRepository(environment.Session, environment.Factory)
            .ReadAsync(ArchiveCoverage, 100, filter: filter);
}
