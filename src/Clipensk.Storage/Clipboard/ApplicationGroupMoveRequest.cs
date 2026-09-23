using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;

namespace Clipensk.Storage.Clipboard;

/// <summary>Where an application is moved, per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §3.</summary>
public abstract record ApplicationGroupMoveTarget;

/// <summary>
/// A new user group created for the move, with the name the user typed and the settings the user
/// chose (a copy of the default group's settings, possibly edited).
/// </summary>
public sealed record NewApplicationGroupTarget(
    ApplicationGroupName Name,
    ClipboardCapturePolicy Policy,
    IReadOnlyList<ApplicationCustomBinaryFormatConfiguration>? CustomBinaryConfigurations = null)
    : ApplicationGroupMoveTarget;

/// <summary>An existing user group; its current settings apply to the moved application.</summary>
public sealed record ExistingApplicationGroupTarget(ApplicationGroupId GroupId) : ApplicationGroupMoveTarget;

/// <summary>
/// Moving one application into a user group. <see cref="DeleteEmptiedSourceGroup"/> records the
/// user's answer when the move empties the application's current user group.
/// </summary>
public sealed record ApplicationGroupMoveRequest(
    ApplicationId ApplicationId,
    ApplicationGroupMoveTarget Target,
    bool DeleteEmptiedSourceGroup = false);
