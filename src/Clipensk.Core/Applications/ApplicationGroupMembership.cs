namespace Clipensk.Core.Applications;

/// <summary>
/// One application's membership in the group whose root is <see cref="ParentApplicationId"/>.
/// A retained identity (<see cref="RetainedFromApplicationId"/> set) holds records that stayed in
/// the group when that application was split out without carrying its records.
/// </summary>
public sealed record ApplicationGroupMembership(
    ApplicationId ApplicationId,
    ApplicationId ParentApplicationId,
    ApplicationId? RetainedFromApplicationId,
    DateTimeOffset JoinedAtUtc)
{
    public bool IsRetained => RetainedFromApplicationId is not null;
}
