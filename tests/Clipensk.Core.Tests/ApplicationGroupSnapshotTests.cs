using Clipensk.Core.Applications;
using Xunit;
using ApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Core.Tests;

public sealed class ApplicationGroupSnapshotTests
{
    private static readonly DateTimeOffset Joined = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    private static readonly ApplicationId Parent = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly ApplicationId ChildB = new(Guid.Parse("20000000-0000-0000-0000-000000000002"));
    private static readonly ApplicationId ChildA = new(Guid.Parse("20000000-0000-0000-0000-000000000001"));
    private static readonly ApplicationId Standalone = new(Guid.Parse("30000000-0000-0000-0000-000000000003"));

    [Fact]
    public void Empty_EveryApplicationIsItsOwnUnconfiguredRoot()
    {
        ApplicationGroupSnapshot snapshot = ApplicationGroupSnapshot.Empty;

        Assert.Equal(Standalone, snapshot.RootOf(Standalone));
        Assert.Equal([Standalone], snapshot.MembersOf(Standalone));
        Assert.False(snapshot.IsMember(Standalone));
        Assert.False(snapshot.HasMembers(Standalone));
        Assert.False(snapshot.IsGovernedByPersonalPolicy(Standalone));
    }

    [Fact]
    public void Members_ResolveToTheirRootAndInheritItsPersonalPolicy()
    {
        var snapshot = new ApplicationGroupSnapshot(
            [
                new ApplicationGroupMembership(ChildB, Parent, null, Joined),
                new ApplicationGroupMembership(ChildA, Parent, null, Joined),
            ],
            [Parent]);

        Assert.Equal(Parent, snapshot.RootOf(ChildA));
        Assert.Equal(Parent, snapshot.RootOf(Parent));
        Assert.Equal([Parent, ChildA, ChildB], snapshot.MembersOf(Parent));
        Assert.True(snapshot.HasMembers(Parent));
        Assert.True(snapshot.IsGovernedByPersonalPolicy(ChildB));
        Assert.False(snapshot.IsGovernedByPersonalPolicy(Standalone));
        Assert.Throws<ArgumentException>(() => snapshot.MembersOf(ChildA));
    }

    [Fact]
    public void RetainedIdentity_IsReportedAsRetained()
    {
        var snapshot = new ApplicationGroupSnapshot(
            [new ApplicationGroupMembership(ChildA, Parent, Standalone, Joined)],
            []);

        ApplicationGroupMembership? membership = snapshot.GetMembership(ChildA);
        Assert.NotNull(membership);
        Assert.True(membership!.IsRetained);
        Assert.Equal(Standalone, membership.RetainedFromApplicationId);
        Assert.Null(snapshot.GetMembership(Parent));
    }

    [Fact]
    public void NestedGroup_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => new ApplicationGroupSnapshot(
            [
                new ApplicationGroupMembership(ChildA, Parent, null, Joined),
                new ApplicationGroupMembership(ChildB, ChildA, null, Joined),
            ],
            []));
    }

    [Fact]
    public void MemberWithItsOwnPolicy_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => new ApplicationGroupSnapshot(
            [new ApplicationGroupMembership(ChildA, Parent, null, Joined)],
            [ChildA]));
    }

    [Fact]
    public void DuplicateOrSelfMembership_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => new ApplicationGroupSnapshot(
            [
                new ApplicationGroupMembership(ChildA, Parent, null, Joined),
                new ApplicationGroupMembership(ChildA, Standalone, null, Joined),
            ],
            []));
        Assert.Throws<InvalidDataException>(() => new ApplicationGroupSnapshot(
            [new ApplicationGroupMembership(Parent, Parent, null, Joined)],
            []));
        Assert.Throws<InvalidDataException>(() => new ApplicationGroupSnapshot(
            [new ApplicationGroupMembership(ChildA, Parent, ChildA, Joined)],
            []));
    }
}
