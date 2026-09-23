using System.Text;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Databases;
using Xunit;
using static Clipensk.Storage.Tests.ApplicationGroupTestData;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedApplicationHistoryPurgeTests
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
    public async Task MoveToANewGroup_CreatesTheGroupAndPurgesOnlyTheApplicationsCurrentHistory()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        DurableApplicationId other = InsertIdentity(environment);

        Guid mixed = InsertEvent(environment, null, moved, ("Text", "Text"), ("HTML Format", "Text"));
        Guid htmlOnly = InsertEvent(environment, null, moved, ("HTML Format", "Text"));
        Guid alreadyEmpty = InsertEvent(environment, null, moved);
        Guid textOnly = InsertEvent(environment, null, moved, ("Text", "Text"));
        Guid unlisted = InsertEvent(environment, null, moved, ("Vendor.Format", "Text"));
        Guid otherHtml = InsertEvent(environment, null, other, ("HTML Format", "Text"));
        Guid unknownSource = InsertEvent(environment, null, null, ("HTML Format", "Text"));

        ApplicationHistoryPurgeStartResult result = await Service(environment)
            .StartMoveAsync(NewGroup(moved, "Office", DenyHtmlAndPng));

        Assert.Equal(3, result.CurrentSummary.DeletedRepresentations);
        Assert.Equal(3, result.CurrentSummary.DeletedRecords);
        Assert.Equal(1, result.CurrentSummary.TrimmedRecords);
        Assert.Equal(2, result.CurrentSummary.DeletedRepresentationsByFormat["HTML Format"]);
        Assert.Equal(1, result.CurrentSummary.DeletedRepresentationsByFormat["Vendor.Format"]);

        Assert.Equal(1, PayloadCount(environment, null, mixed));
        Assert.Equal(0, EventCount(environment, null, htmlOnly));
        Assert.Equal(0, EventCount(environment, null, alreadyEmpty));
        Assert.Equal(1, PayloadCount(environment, null, textOnly));
        Assert.Equal(0, EventCount(environment, null, unlisted));
        Assert.Equal(1, PayloadCount(environment, null, otherHtml));
        Assert.Equal(1, PayloadCount(environment, null, unknownSource));

        ApplicationGroupDirectory groups = ReadGroups(environment);
        ApplicationGroup group = groups.FindGroup(result.GroupId)!;
        Assert.Equal("Office", group.Name.Value);
        Assert.Equal(DenyHtmlAndPng.Formats.Keys.Order(), group.Policy.Formats.Keys.Order());
        Assert.Same(group, groups.GroupOf(moved));
        Assert.True(groups.IsInDefaultGroup(other));
        Assert.Equal(1, environment.Scalar(
            $"SELECT COUNT(*) FROM PendingPolicyMaintenance WHERE OperationKind = '{ProtectedApplicationHistoryPurgeService.OperationKind}';"));
    }

    [Fact]
    public async Task Move_IgnoresMaxBytesWhenPurging()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        Guid largeText = InsertEvent(environment, null, moved, ("Text", "Text"));
        var tinyLimit = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, 1),
            });

        ApplicationHistoryPurgeStartResult result = await Service(environment)
            .StartMoveAsync(NewGroup(moved, "Tiny", tinyLimit));

        Assert.True(result.CurrentSummary.IsEmpty);
        Assert.Equal(1, PayloadCount(environment, null, largeText));
    }

    [Fact]
    public async Task MoveToAnExistingGroup_ResumePurgesArchivesFullyMovesTheOrphanedFileToTrashAndClearsTheMarker()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        DurableApplicationId other = InsertIdentity(environment);
        ApplicationGroupId office = InsertGroup(environment, "Office", DenyHtmlAndPng, other);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, moved);
        InsertArchiveIdentity(environment, archive, other);

        Guid archivedHtml = InsertEvent(environment, archive, moved, ("HTML Format", "Text"), ("Text", "Text"));
        (Guid archivedImage, string sourcePath, string trashPath) =
            InsertArchivedImage(environment, archive, moved, DeletionDate);
        Guid otherHtml = InsertEvent(environment, archive, other, ("HTML Format", "Text"));

        ApplicationHistoryPurgeStartResult started = await Service(environment)
            .StartMoveAsync(new ApplicationGroupMoveRequest(moved, new ExistingApplicationGroupTarget(office)));
        ApplicationHistoryPurgeResumeResult result = await Continuation(environment)
            .ResumeAsync(DeletionDate);

        Assert.Equal(office, started.GroupId);
        Assert.Equal(started.OperationId, result.OperationId);
        Assert.Equal(1, result.ArchiveDatabaseCount);
        Assert.Equal(2, result.ArchiveSummary.DeletedRepresentations);
        Assert.Equal(1, result.ArchiveSummary.DeletedExternalReferences);
        Assert.Equal(1, result.ArchiveSummary.DeletedRecords);
        Assert.Equal(1, result.ArchiveSummary.TrimmedRecords);

        Assert.Equal(1, PayloadCount(environment, archive, archivedHtml));
        Assert.Equal(0, EventCount(environment, archive, archivedImage));
        Assert.Equal(1, PayloadCount(environment, archive, otherHtml));
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(trashPath));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
        Assert.Equal(new[] { other, moved }.OrderBy(static id => id.Value), ReadGroups(environment).MembersOf(office));
    }

    [Fact]
    public async Task Resume_AfterACrashBetweenArchiveCommitAndPhaseMark_IsIdempotent()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, moved);
        Guid archivedHtml = InsertEvent(environment, archive, moved, ("HTML Format", "Text"));
        await Service(environment).StartMoveAsync(NewGroup(moved, "Office", DenyHtmlAndPng));

        var crashing = new ProtectedApplicationHistoryPurgeContinuation(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ApplicationHistoryPurgeContinuationCheckpoint.ArchiveDatabaseCommitted)
                {
                    throw new IOException("Simulated crash after the archive commit.");
                }
            });
        await Assert.ThrowsAsync<IOException>(() => crashing.ResumeAsync(DeletionDate));
        Assert.Equal(0, EventCount(environment, archive, archivedHtml));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));

        ApplicationHistoryPurgeResumeResult retry = await Continuation(environment).ResumeAsync(DeletionDate);

        Assert.True(retry.ArchiveSummary.IsEmpty);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task Resume_DispatchedFromTheGenericResumeEntryPoint()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        await Service(environment).StartMoveAsync(NewGroup(moved, "Office", DenyHtmlAndPng));

        PolicyMaintenanceResumeDispatchResult result =
            await new ProtectedPolicyMaintenanceResumeDispatcher(environment.Session, environment.Factory)
                .ResumeAsync(DeletionDate);

        Assert.True(result.HadPendingOperation);
        Assert.NotNull(result.HistoryPurge);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("membership")]
    public async Task Resume_FailsClosedWhenTheGroupChangedWhilePending(string change)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        ApplicationHistoryPurgeStartResult started =
            await Service(environment).StartMoveAsync(NewGroup(moved, "Office", DenyHtmlAndPng));
        if (change == "policy")
        {
            environment.Execute($"UPDATE ApplicationGroup SET CaptureRule = 'Deny' WHERE GroupId = '{started.GroupId}';");
        }
        else
        {
            ApplicationGroupId elsewhere = InsertGroup(environment, "Elsewhere", DenyHtmlAndPng);
            Join(environment, moved, elsewhere);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => Continuation(environment).ResumeAsync(DeletionDate));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task Start_RejectsInvalidMovesAndPendingMarkersWithoutWriting()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId member = InsertIdentity(environment);
        DurableApplicationId unassigned = InsertIdentity(environment);
        ApplicationGroupId office = InsertGroup(environment, "Офис", DenyHtmlAndPng, member);
        Guid html = InsertEvent(environment, null, unassigned, ("HTML Format", "Text"));

        await Assert.ThrowsAsync<ApplicationGroupNameTakenException>(() =>
            Service(environment).StartMoveAsync(NewGroup(unassigned, "ОФИС", DenyHtmlAndPng)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).StartMoveAsync(new ApplicationGroupMoveRequest(
                unassigned,
                new ExistingApplicationGroupTarget(ApplicationGroupId.New()))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).StartMoveAsync(new ApplicationGroupMoveRequest(
                member,
                new ExistingApplicationGroupTarget(office))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).StartMoveAsync(new ApplicationGroupMoveRequest(
                unassigned,
                new ExistingApplicationGroupTarget(office),
                DeleteEmptiedSourceGroup: true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).StartMoveAsync(NewGroup(DurableApplicationId.New(), "New", DenyHtmlAndPng)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Service(environment).StartMoveAsync(NewGroup(
                unassigned,
                "Inheriting",
                new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Inherit))));

        Assert.Equal(1, PayloadCount(environment, null, html));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ApplicationGroup;"));
        Assert.True(ReadGroups(environment).IsInDefaultGroup(unassigned));

        await Service(environment).StartMoveAsync(NewGroup(unassigned, "New", DenyHtmlAndPng));
        DurableApplicationId another = InsertIdentity(environment);
        await Assert.ThrowsAsync<PendingPolicyMaintenanceException>(() =>
            Service(environment).StartMoveAsync(NewGroup(another, "Another", DenyHtmlAndPng)));
        Assert.True(ReadGroups(environment).IsInDefaultGroup(another));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MoveFromAUserGroup_DeletesTheEmptiedSourceGroupOnlyWhenAsked(bool delete)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        ApplicationGroupId source = InsertGroup(environment, "Source", Global, moved);
        ApplicationGroupId target = InsertGroup(environment, "Target", DenyHtmlAndPng);

        await Service(environment).StartMoveAsync(new ApplicationGroupMoveRequest(
            moved,
            new ExistingApplicationGroupTarget(target),
            DeleteEmptiedSourceGroup: delete));

        ApplicationGroupDirectory groups = ReadGroups(environment);
        Assert.Equal(target, groups.GroupOf(moved)!.GroupId);
        Assert.Equal(delete, groups.FindGroup(source) is null);
        Assert.Equal(delete ? 0 : 3, environment.Scalar(
            $"SELECT COUNT(*) FROM ApplicationGroupFormatCapturePolicy WHERE GroupId = '{source}';"));
    }

    [Fact]
    public async Task MoveToANewGroup_StoresItsCustomBinaryMappings()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        ClipboardCapturePolicy withBinary = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow),
            ("Vendor.Binary", ClipboardCapturePolicyRule.Allow));

        await Service(environment).StartMoveAsync(new ApplicationGroupMoveRequest(
            moved,
            new NewApplicationGroupTarget(
                ApplicationGroupName.Create("Vendor"),
                withBinary,
                [new ApplicationCustomBinaryFormatConfiguration("Vendor.Binary", ".VBIN")])));

        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM CustomBinaryFormatConfiguration WHERE FormatName = 'Vendor.Binary' AND FileExtension = '.vbin';"));
    }

    [Fact]
    public async Task Start_FailureBeforeCommitLeavesNoGroupMembershipMarkerOrDeletion()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        Guid html = InsertEvent(environment, null, moved, ("HTML Format", "Text"));
        var failing = new ProtectedApplicationHistoryPurgeService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ApplicationHistoryPurgeStartCheckpoint.BeforeCommit)
                {
                    throw new IOException("Simulated failure before COMMIT.");
                }
            });

        await Assert.ThrowsAsync<IOException>(() => failing.StartMoveAsync(NewGroup(moved, "Office", DenyHtmlAndPng)));

        Assert.Equal(1, PayloadCount(environment, null, html));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationGroup;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationGroupMembership;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task Purge_OverwritesDeletedContentInTheDatabaseFile()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId moved = InsertIdentity(environment);
        const string Secret = "PURGE-ME-7f3c2a91-secret-content";
        InsertEvent(environment, null, moved, ("HTML Format", "Text"), secretText: Secret);
        Assert.Contains(Secret, ReadDatabaseText(environment.CurrentPath), StringComparison.Ordinal);

        await Service(environment).StartMoveAsync(NewGroup(moved, "Office", DenyHtmlAndPng));

        Assert.DoesNotContain(Secret, ReadDatabaseText(environment.CurrentPath), StringComparison.Ordinal);
    }

    private static ApplicationGroupMoveRequest NewGroup(
        DurableApplicationId application,
        string name,
        ClipboardCapturePolicy policy) =>
        new(application, new NewApplicationGroupTarget(ApplicationGroupName.Create(name), policy));

    private static ProtectedApplicationHistoryPurgeService Service(GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static ProtectedApplicationHistoryPurgeContinuation Continuation(GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static ClipboardCapturePolicy Policy(
        ClipboardCapturePolicyRule capture,
        params (string Name, ClipboardCapturePolicyRule Rule)[] formats) =>
        new(
            capture,
            formats.ToDictionary(
                item => item.Name,
                item => new ClipboardFormatCapturePolicy(item.Rule),
                StringComparer.Ordinal));

    private static string ReadDatabaseText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Encoding.UTF8.GetString(memory.ToArray());
    }
}
