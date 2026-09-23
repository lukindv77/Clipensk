using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Databases;
using Clipensk.Storage.History;
using Microsoft.Data.Sqlite;
using Xunit;
using static Clipensk.Storage.Tests.ApplicationGroupTestData;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedApplicationHistoryPurgePreviewTests
{
    private static readonly DateOnly DeletionDate = new(2026, 9, 22);

    private static readonly ClipboardCapturePolicy Global = Policy(
        ClipboardCapturePolicyRule.Allow,
        ("Text", ClipboardCapturePolicyRule.Allow),
        ("HTML Format", ClipboardCapturePolicyRule.Allow),
        ("PNG", ClipboardCapturePolicyRule.Allow));

    private static readonly ClipboardCapturePolicy DenyHtmlAndPng = Policy(
        ClipboardCapturePolicyRule.Allow,
        ("Text", ClipboardCapturePolicyRule.Allow),
        ("HTML Format", ClipboardCapturePolicyRule.Deny),
        ("PNG", ClipboardCapturePolicyRule.Deny));

    [Fact]
    public async Task Preview_ReportsCurrentAndArchiveSeparatelyChangesNothingAndMatchesThePurge()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        DurableApplicationId other = InsertIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, moved);
        InsertArchiveIdentity(environment, archive, other);

        Guid mixed = InsertEvent(environment, null, moved, ("Text", "Text"), ("HTML Format", "Text"));
        Guid htmlOnly = InsertEvent(environment, null, moved, ("HTML Format", "Text"));
        InsertEvent(environment, null, moved);
        InsertEvent(environment, null, other, ("HTML Format", "Text"));
        Guid archivedMixed = InsertEvent(environment, archive, moved, ("HTML Format", "Text"), ("Text", "Text"));
        (Guid archivedImage, string imagePath, _) = InsertArchivedImage(environment, archive, moved, DeletionDate);
        InsertEvent(environment, archive, other, ("PNG", "Text"));
        ApplicationGroupMoveRequest request = NewGroup(moved, "Office", DenyHtmlAndPng);

        ApplicationGroupMovePreview preview = await Preview(environment).PreviewMoveAsync(request);

        Assert.Equal(moved, preview.ApplicationId);
        Assert.Null(preview.TargetGroupId);
        Assert.Equal("Office", preview.TargetGroupName.Value);
        Assert.Null(preview.SourceGroupId);
        Assert.False(preview.SourceGroupBecomesEmpty);
        Assert.Equal(1, preview.ArchiveDatabaseCount);
        AssertSummary(preview.CurrentSummary, representations: 2, external: 0, records: 2, trimmed: 1,
            ("HTML Format", 2));
        AssertSummary(preview.ArchiveSummary, representations: 2, external: 1, records: 1, trimmed: 1,
            ("HTML Format", 1), ("PNG", 1));
        AssertSummary(preview.TotalSummary, representations: 4, external: 1, records: 3, trimmed: 2,
            ("HTML Format", 3), ("PNG", 1));
        Assert.False(preview.IsEmpty);

        Assert.Equal(2, PayloadCount(environment, null, mixed));
        Assert.Equal(1, PayloadCount(environment, null, htmlOnly));
        Assert.Equal(2, PayloadCount(environment, archive, archivedMixed));
        Assert.Equal(1, EventCount(environment, archive, archivedImage));
        Assert.True(File.Exists(imagePath));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationGroup;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));

        ApplicationHistoryPurgeResult result = await Coordinator(environment)
            .RunConfirmedMoveAsync(request, preview, DeletionDate);

        AssertSameSummary(preview.CurrentSummary, result.CurrentSummary);
        AssertSameSummary(preview.ArchiveSummary, result.ArchiveSummary);
        Assert.Equal(result.GroupId, ReadGroups(environment).GroupOf(moved)!.GroupId);
        Assert.False(File.Exists(imagePath));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task Preview_CountsARecordPresentInCurrentAndAnArchiveOnceUnderCurrent()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, moved);
        Guid copied = Guid.NewGuid();
        (string, string)[] payloads = [("Text", "Text"), ("HTML Format", "Text")];
        InsertEvent(environment, null, moved, payloads, "text", copied);
        InsertEvent(environment, archive, moved, payloads, "text", copied);
        InsertEvent(environment, null, moved, [], "text", Guid.NewGuid());
        Guid emptyCopy = Guid.NewGuid();
        InsertEvent(environment, archive, moved, [], "text", emptyCopy);
        InsertEvent(environment, null, moved, [], "text", emptyCopy);

        ApplicationGroupMovePreview preview = await Preview(environment)
            .PreviewMoveAsync(NewGroup(moved, "Office", DenyHtmlAndPng));

        AssertSummary(preview.CurrentSummary, representations: 1, external: 0, records: 2, trimmed: 1,
            ("HTML Format", 1));
        Assert.True(preview.ArchiveSummary.IsEmpty);
        Assert.Equal(0, preview.ArchiveSummary.TrimmedRecords);
    }

    [Fact]
    public async Task Preview_EnforcesTheStartPreconditionsAndOpensEveryDatabaseReadOnly()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId member = InsertIdentity(environment);
        DurableApplicationId unassigned = InsertIdentity(environment);
        ApplicationGroupId office = InsertGroup(environment, "Office", DenyHtmlAndPng, member);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, unassigned);
        InsertEvent(environment, archive, unassigned, ("HTML Format", "Text"));

        await Assert.ThrowsAsync<ApplicationGroupNameTakenException>(() =>
            Preview(environment).PreviewMoveAsync(NewGroup(unassigned, "OFFICE", DenyHtmlAndPng)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Preview(environment).PreviewMoveAsync(new ApplicationGroupMoveRequest(
                member,
                new ExistingApplicationGroupTarget(office))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Preview(environment).PreviewMoveAsync(NewGroup(DurableApplicationId.New(), "New", DenyHtmlAndPng)));

        environment.Factory.Modes.Clear();
        ApplicationGroupMovePreview preview = await Preview(environment)
            .PreviewMoveAsync(new ApplicationGroupMoveRequest(unassigned, new ExistingApplicationGroupTarget(office)));
        Assert.Equal(office, preview.TargetGroupId);
        Assert.Equal(1, preview.ArchiveSummary.DeletedRecords);
        Assert.NotEmpty(environment.Factory.Modes);
        Assert.All(environment.Factory.Modes, static mode => Assert.Equal(SqliteOpenMode.ReadOnly, mode));

        await new ProtectedApplicationHistoryPurgeService(environment.Session, environment.Factory)
            .StartMoveAsync(NewGroup(unassigned, "New", DenyHtmlAndPng));
        DurableApplicationId another = InsertIdentity(environment);
        await Assert.ThrowsAsync<PendingPolicyMaintenanceException>(() =>
            Preview(environment).PreviewMoveAsync(NewGroup(another, "Another", DenyHtmlAndPng)));
    }

    [Fact]
    public async Task Preview_ReportsTheSourceGroupAndWhetherTheMoveEmptiesIt()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId alone = InsertIdentity(environment);
        DurableApplicationId shared = InsertIdentity(environment);
        DurableApplicationId companion = InsertIdentity(environment);
        ApplicationGroupId solo = InsertGroup(environment, "Solo", Global, alone);
        ApplicationGroupId pair = InsertGroup(environment, "Pair", Global, shared, companion);
        ApplicationGroupId target = InsertGroup(environment, "Target", DenyHtmlAndPng);

        ApplicationGroupMovePreview emptying = await Preview(environment)
            .PreviewMoveAsync(new ApplicationGroupMoveRequest(alone, new ExistingApplicationGroupTarget(target)));
        ApplicationGroupMovePreview keeping = await Preview(environment)
            .PreviewMoveAsync(new ApplicationGroupMoveRequest(shared, new ExistingApplicationGroupTarget(target)));

        Assert.Equal(solo, emptying.SourceGroupId);
        Assert.True(emptying.SourceGroupBecomesEmpty);
        Assert.Equal(pair, keeping.SourceGroupId);
        Assert.False(keeping.SourceGroupBecomesEmpty);
        Assert.Equal("Target", keeping.TargetGroupName.Value);
    }

    [Fact]
    public async Task Preview_RequiresTheGlobalPolicy()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DurableApplicationId moved = InsertIdentity(environment);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Preview(environment).PreviewMoveAsync(NewGroup(moved, "Office", DenyHtmlAndPng)));
    }

    [Fact]
    public async Task RecordsLosing_ListsExactlyTheApplicationsRecordsHoldingTheFormatAcrossCurrentAndArchive()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        DurableApplicationId other = InsertIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, moved);
        InsertArchiveIdentity(environment, archive, other);

        Guid currentHtml = InsertEvent(environment, null, moved, ("Text", "Text"), ("HTML Format", "Text"));
        InsertEvent(environment, null, moved, ("Text", "Text"));
        InsertEvent(environment, null, other, ("HTML Format", "Text"));
        Guid archivedHtml = InsertEvent(environment, archive, moved, ("HTML Format", "Text"));
        InsertEvent(environment, archive, other, ("HTML Format", "Text"));
        await new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory)
            .RebuildAsync(DeletionDate);

        ApplicationGroupMovePreview preview = await Preview(environment)
            .PreviewMoveAsync(NewGroup(moved, "Office", DenyHtmlAndPng));
        ClipboardHistoryFilter filter = preview.RecordsLosing("HTML Format");
        IReadOnlyList<UnifiedClipboardHistoryEntry> entries =
            await new ProtectedUnifiedClipboardHistoryRepository(environment.Session, environment.Factory)
                .ReadAsync(ArchiveCoverage, 100, filter: filter);

        Assert.Equal(
            new[] { currentHtml, archivedHtml }.Order(),
            entries.Select(static entry => entry.Entry.EventId).Order());
        Assert.Equal(
            2,
            entries.Single(entry => entry.Entry.EventId == currentHtml).Entry.Payloads.Count);
        Assert.Equal(preview.TotalSummary.DeletedRepresentationsByFormat["HTML Format"], entries.Count);
        Assert.Throws<ArgumentException>(() => preview.RecordsLosing("Text"));
    }

    [Fact]
    public async Task ConfirmedMove_RefusesWithoutWritingWhenTheTargetGroupPolicyChanged()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        ApplicationGroupId office = InsertGroup(environment, "Office", DenyHtmlAndPng);
        Guid mixed = InsertEvent(environment, null, moved, ("Text", "Text"), ("HTML Format", "Text"));
        var request = new ApplicationGroupMoveRequest(moved, new ExistingApplicationGroupTarget(office));
        ApplicationGroupMovePreview preview = await Preview(environment).PreviewMoveAsync(request);

        await new ProtectedCapturePolicyPublishService(environment.Session, environment.Factory)
            .PublishGroupPolicyAsync(office, Policy(ClipboardCapturePolicyRule.Deny));

        await Assert.ThrowsAsync<ApplicationHistoryPurgePreviewOutdatedException>(() =>
            Coordinator(environment).RunConfirmedMoveAsync(request, preview, DeletionDate));

        Assert.Equal(2, PayloadCount(environment, null, mixed));
        Assert.True(ReadGroups(environment).IsInDefaultGroup(moved));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task ConfirmedMove_RefusesWithoutWritingWhenTheSourceGroupChanged()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        DurableApplicationId joiner = InsertIdentity(environment);
        ApplicationGroupId source = InsertGroup(environment, "Source", Global, moved);
        ApplicationGroupId target = InsertGroup(environment, "Target", DenyHtmlAndPng);
        var request = new ApplicationGroupMoveRequest(
            moved,
            new ExistingApplicationGroupTarget(target),
            DeleteEmptiedSourceGroup: true);
        ApplicationGroupMovePreview preview = await Preview(environment).PreviewMoveAsync(request);
        Assert.True(preview.SourceGroupBecomesEmpty);

        Join(environment, joiner, source);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            new ProtectedApplicationHistoryPurgeService(environment.Session, environment.Factory)
                .StartConfirmedMoveAsync(request, preview));

        ApplicationGroupDirectory groups = ReadGroups(environment);
        Assert.Equal(source, groups.GroupOf(moved)!.GroupId);
        Assert.NotNull(groups.FindGroup(source));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task Coordinator_ReportsAnIncompleteMoveWhenALaterPhaseFails()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, moved);
        Guid currentHtml = InsertEvent(environment, null, moved, ("HTML Format", "Text"));
        InsertEvent(environment, archive, moved, ("HTML Format", "Text"));
        ApplicationGroupMoveRequest request = NewGroup(moved, "Office", DenyHtmlAndPng);
        ApplicationGroupMovePreview preview = await Preview(environment).PreviewMoveAsync(request);
        var failing = new ProtectedApplicationHistoryPurgeCoordinator(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ApplicationHistoryPurgeContinuationCheckpoint.ArchivePhaseMarked)
                {
                    throw new IOException("Simulated failure after the archive phase.");
                }
            });

        ApplicationHistoryPurgeIncompleteException incomplete =
            await Assert.ThrowsAsync<ApplicationHistoryPurgeIncompleteException>(() =>
                failing.RunConfirmedMoveAsync(request, preview, DeletionDate));

        Assert.IsType<IOException>(incomplete.InnerException);
        Assert.Equal(1, incomplete.CurrentSummary.DeletedRecords);
        Assert.Equal(incomplete.GroupId, ReadGroups(environment).GroupOf(moved)!.GroupId);
        Assert.Equal(0, EventCount(environment, null, currentHtml));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));

        await new ProtectedApplicationHistoryPurgeContinuation(environment.Session, environment.Factory)
            .ResumeAsync(DeletionDate);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    private static ApplicationGroupMoveRequest NewGroup(
        DurableApplicationId application,
        string name,
        ClipboardCapturePolicy policy) =>
        new(application, new NewApplicationGroupTarget(ApplicationGroupName.Create(name), policy));

    private static ProtectedApplicationHistoryPurgeCoordinator Coordinator(GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static ProtectedApplicationHistoryPurgePreviewService Preview(GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static void AssertSummary(
        ClipboardHistoryPurgeSummary summary,
        int representations,
        int external,
        int records,
        int trimmed,
        params (string FormatName, int Count)[] byFormat)
    {
        Assert.Equal(representations, summary.DeletedRepresentations);
        Assert.Equal(external, summary.DeletedExternalReferences);
        Assert.Equal(records, summary.DeletedRecords);
        Assert.Equal(trimmed, summary.TrimmedRecords);
        Assert.Equal(
            byFormat.OrderBy(static item => item.FormatName, StringComparer.Ordinal),
            summary.DeletedRepresentationsByFormat
                .Select(static item => (item.Key, item.Value))
                .OrderBy(static item => item.Key, StringComparer.Ordinal));
    }

    private static void AssertSameSummary(ClipboardHistoryPurgeSummary expected, ClipboardHistoryPurgeSummary actual) =>
        AssertSummary(
            actual,
            expected.DeletedRepresentations,
            expected.DeletedExternalReferences,
            expected.DeletedRecords,
            expected.TrimmedRecords,
            expected.DeletedRepresentationsByFormat.Select(static item => (item.Key, item.Value)).ToArray());

    private static ClipboardCapturePolicy Policy(
        ClipboardCapturePolicyRule capture,
        params (string Name, ClipboardCapturePolicyRule Rule)[] formats) =>
        new(
            capture,
            formats.ToDictionary(
                item => item.Name,
                item => new ClipboardFormatCapturePolicy(item.Rule),
                StringComparer.Ordinal));
}
