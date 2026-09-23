using Clipensk.Core.Clipboard;

namespace Clipensk.Core.Applications;

/// <summary>
/// A user application group with its own standalone capture policy, per
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §1–2. The policy has the global policy's shape — only
/// explicit Allow/Deny — and is never merged with the global policy.
/// </summary>
public sealed record ApplicationGroup
{
    public ApplicationGroup(
        ApplicationGroupId groupId,
        ApplicationGroupName name,
        ClipboardCapturePolicy policy,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(name);
        RequireStandalonePolicy(policy);
        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Group creation time must be UTC.", nameof(createdAtUtc));
        }

        GroupId = groupId;
        Name = name;
        Policy = policy;
        CreatedAtUtc = createdAtUtc;
    }

    public ApplicationGroupId GroupId { get; }

    public ApplicationGroupName Name { get; }

    public ClipboardCapturePolicy Policy { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>
    /// Requires a policy a group can own: explicit Allow/Deny for the capture rule and every format.
    /// </summary>
    public static void RequireStandalonePolicy(ClipboardCapturePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Capture is not (ClipboardCapturePolicyRule.Allow or ClipboardCapturePolicyRule.Deny))
        {
            throw new ArgumentException("A group capture rule must be explicit Allow or Deny.", nameof(policy));
        }
        foreach ((string formatName, ClipboardFormatCapturePolicy format) in policy.Formats)
        {
            if (format.Capture is not (ClipboardCapturePolicyRule.Allow or ClipboardCapturePolicyRule.Deny))
            {
                throw new ArgumentException(
                    $"Group rule for format '{formatName}' must be explicit Allow or Deny.",
                    nameof(policy));
            }
        }
    }
}

/// <summary>An application's membership in a user group; absence means the default group.</summary>
public sealed record ApplicationGroupAssignment(
    ApplicationId ApplicationId,
    ApplicationGroupId GroupId,
    DateTimeOffset JoinedAtUtc);
