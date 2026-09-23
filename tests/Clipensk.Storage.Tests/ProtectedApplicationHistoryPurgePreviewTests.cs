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
        ClipboardCapturePolicyRule.Inherit,
        ("HTML Format", ClipboardCapturePolicyRule.Deny),
        ("PNG", ClipboardCapturePolicyRule.Deny));

    [Fact]
    public async Task Preview_ReportsCurrentAndArchiveSeparatelyChangesNothingAndMatchesThePurge()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        DurableApplicationId member = InsertIdentity(environment);
        DurableApplicationId other = InsertIdentity(environment);
        AddMember(environment, member, root);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, root);
        InsertArchiveIdentity(environment, archive, member);
        InsertArchiveIdentity(environment, archive, other);

        Guid mixed = InsertEvent(environment, null, root, ("Text", "Text"), ("HTML Format", "Text"));
        Guid memberHtml = InsertEvent(environment, null, member, ("HTML Format", "Text"));
        InsertEvent(environment, null, root);
        InsertEvent(environment, null, other, ("HTML Format", "Text"));
        Guid archivedMixed = InsertEvent(environment, archive, member, ("HTML Format", "Text"), ("Text", "Text"));
        (Guid archivedImage, string imagePath, _) = InsertArchivedImage(environment, archive, root, DeletionDate);
        InsertEvent(environment, archive, other, ("PNG", "Text"));

        ApplicationHistoryPurgePreview preview = await Preview(environment)
            .PreviewFirstAssignmentAsync(root, DenyHtmlAndPng);

        Assert.Equal(root, preview.RootApplicationId);
        Assert.Equal([root, member], preview.SourceApplicationIds);
        Assert.Equal(1, preview.ArchiveDatabaseCount);
        AssertSummary(preview.CurrentSummary, representations: 2, external: 0, records: 2, trimmed: 1,
            ("HTML Format", 2));
        AssertSummary(preview.ArchiveSummary, representations: 2, external: 1, records: 1, trimmed: 1,
            ("HTML Format", 1), ("PNG", 1));
        AssertSummary(preview.TotalSummary, representations: 4, external: 1, records: 3, trimmed: 2,
            ("HTML Format", 3), ("PNG", 1));
        Assert.False(preview.IsEmpty);

        Assert.Equal(2, PayloadCount(environment, null, mixed));
        Assert.Equal(1, PayloadCount(environment, null, memberHtml));
        Assert.Equal(2, PayloadCount(environment, archive, archivedMixed));
        Assert.Equal(1, EventCount(environment, archive, archivedImage));
        Assert.True(File.Exists(imagePath));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationCapturePolicy;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));

        ApplicationHistoryPurgeStartResult started = await new ProtectedApplicationHistoryPurgeService(
                environment.Session,
                environment.Factory)
            .StartFirstAssignmentAsync(root, DenyHtmlAndPng);
        ApplicationHistoryPurgeResumeResult resumed = await new ProtectedApplicationHistoryPurgeContinuation(
                environment.Session,
                environment.Factory)
            .ResumeAsync(DeletionDate);

        AssertSameSummary(preview.CurrentSummary, started.CurrentSummary);
        AssertSameSummary(preview.ArchiveSummary, resumed.ArchiveSummary);
    }

    [Fact]
    public async Task Preview_CountsARecordPresentInCurrentAndAnArchiveOnceUnderCurrent()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, root);
        Guid copied = Guid.NewGuid();
        (string, string)[] payloads = [("Text", "Text"), ("HTML Format", "Text")];
        InsertEvent(environment, null, root, payloads, "text", copied);
        InsertEvent(environment, archive, root, payloads, "text", copied);
        InsertEvent(environment, null, root, [], "text", Guid.NewGuid());
        Guid emptyCopy = Guid.NewGuid();
        InsertEvent(environment, archive, root, [], "text", emptyCopy);
        InsertEvent(environment, null, root, [], "text", emptyCopy);

        ApplicationHistoryPurgePreview preview = await Preview(environment)
            .PreviewFirstAssignmentAsync(root, DenyHtmlAndPng);

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
        DurableApplicationId configured = InsertIdentity(environment);
        DurableApplicationId member = InsertIdentity(environment);
        DurableApplicationId unconfigured = InsertIdentity(environment);
        environment.Execute($"INSERT INTO ApplicationCapturePolicy VALUES ('{configured}', 'Allow');");
        AddMember(environment, member, configured);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, unconfigured);
        InsertEvent(environment, archive, unconfigured, ("HTML Format", "Text"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Preview(environment).PreviewFirstAssignmentAsync(configured, DenyHtmlAndPng));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Preview(environment).PreviewFirstAssignmentAsync(member, DenyHtmlAndPng));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Preview(environment).PreviewFirstAssignmentAsync(DurableApplicationId.New(), DenyHtmlAndPng));

        environment.Factory.Modes.Clear();
        ApplicationHistoryPurgePreview preview = await Preview(environment)
            .PreviewFirstAssignmentAsync(unconfigured, DenyHtmlAndPng);
        Assert.Equal(1, preview.ArchiveSummary.DeletedRecords);
        Assert.NotEmpty(environment.Factory.Modes);
        Assert.All(environment.Factory.Modes, static mode => Assert.Equal(SqliteOpenMode.ReadOnly, mode));

        await new ProtectedApplicationHistoryPurgeService(environment.Session, environment.Factory)
            .StartFirstAssignmentAsync(unconfigured, DenyHtmlAndPng);
        DurableApplicationId another = InsertIdentity(environment);
        await Assert.ThrowsAsync<PendingPolicyMaintenanceException>(() =>
            Preview(environment).PreviewFirstAssignmentAsync(another, DenyHtmlAndPng));
    }

    [Fact]
    public async Task Preview_RequiresTheGlobalPolicy()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DurableApplicationId root = InsertIdentity(environment);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Preview(environment).PreviewFirstAssignmentAsync(root, DenyHtmlAndPng));
    }

    [Fact]
    public async Task RecordsLosing_ListsExactlyTheScopedRecordsHoldingTheFormatAcrossCurrentAndArchive()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        DurableApplicationId member = InsertIdentity(environment);
        DurableApplicationId other = InsertIdentity(environment);
        AddMember(environment, member, root);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, member);
        InsertArchiveIdentity(environment, archive, other);

        Guid currentHtml = InsertEvent(environment, null, root, ("Text", "Text"), ("HTML Format", "Text"));
        InsertEvent(environment, null, root, ("Text", "Text"));
        InsertEvent(environment, null, other, ("HTML Format", "Text"));
        Guid archivedHtml = InsertEvent(environment, archive, member, ("HTML Format", "Text"));
        InsertEvent(environment, archive, other, ("HTML Format", "Text"));
        await new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory)
            .RebuildAsync(DeletionDate);

        ApplicationHistoryPurgePreview preview = await Preview(environment)
            .PreviewFirstAssignmentAsync(root, DenyHtmlAndPng);
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
    public async Task Coordinator_RunsTheConfirmedPreviewToCompletion()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, root);
        Guid currentHtml = InsertEvent(environment, null, root, ("HTML Format", "Text"));
        (Guid archivedImage, string imagePath, string trashPath) =
            InsertArchivedImage(environment, archive, root, DeletionDate);

        ApplicationHistoryPurgePreview preview = await Preview(environment)
            .PreviewFirstAssignmentAsync(root, DenyHtmlAndPng);
        ApplicationHistoryPurgeResult result = await Coordinator(environment)
            .RunConfirmedFirstAssignmentAsync(preview, DenyHtmlAndPng, null, DeletionDate);

        AssertSameSummary(preview.CurrentSummary, result.CurrentSummary);
        AssertSameSummary(preview.ArchiveSummary, result.ArchiveSummary);
        Assert.Equal(1, result.ArchiveDatabaseCount);
        Assert.Equal(0, EventCount(environment, null, currentHtml));
        Assert.Equal(0, EventCount(environment, archive, archivedImage));
        Assert.False(File.Exists(imagePath));
        Assert.True(File.Exists(trashPath));
        Assert.Equal(1, environment.Scalar($"SELECT COUNT(*) FROM ApplicationCapturePolicy WHERE ApplicationId = '{root}';"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task ConfirmedStart_RefusesWithoutWritingWhenTheGlobalPolicyChangedTheRule()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        Guid mixed = InsertEvent(environment, null, root, ("Text", "Text"), ("HTML Format", "Text"));
        ApplicationHistoryPurgePreview preview = await Preview(environment)
            .PreviewFirstAssignmentAsync(root, DenyHtmlAndPng);

        await new ProtectedCapturePolicyPublishService(environment.Session, environment.Factory)
            .PublishGlobalPolicyAsync(Policy(
                ClipboardCapturePolicyRule.Allow,
                ("Text", ClipboardCapturePolicyRule.Deny),
                ("HTML Format", ClipboardCapturePolicyRule.Allow),
                ("PNG", ClipboardCapturePolicyRule.Allow)));

        await Assert.ThrowsAsync<ApplicationHistoryPurgePreviewOutdatedException>(() =>
            Coordinator(environment).RunConfirmedFirstAssignmentAsync(preview, DenyHtmlAndPng, null, DeletionDate));

        Assert.Equal(2, PayloadCount(environment, null, mixed));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationCapturePolicy;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task ConfirmedStart_RefusesWithoutWritingWhenTheGroupChanged()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        DurableApplicationId joined = InsertIdentity(environment);
        Guid joinedHtml = InsertEvent(environment, null, joined, ("HTML Format", "Text"));
        ApplicationHistoryPurgePreview preview = await Preview(environment)
            .PreviewFirstAssignmentAsync(root, DenyHtmlAndPng);
        Assert.True(preview.IsEmpty);

        AddMember(environment, joined, root);

        await Assert.ThrowsAsync<ApplicationHistoryPurgePreviewOutdatedException>(() =>
            new ProtectedApplicationHistoryPurgeService(environment.Session, environment.Factory)
                .StartConfirmedFirstAssignmentAsync(preview, DenyHtmlAndPng));

        Assert.Equal(1, PayloadCount(environment, null, joinedHtml));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationCapturePolicy;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

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
