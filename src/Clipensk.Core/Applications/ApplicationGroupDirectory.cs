using Clipensk.Core.Clipboard;

namespace Clipensk.Core.Applications;

/// <summary>
/// A consistent snapshot of the user application groups and their memberships, per
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §2.3. An application without a membership is in the
/// default group, whose policy is the global one. Construction fails closed on duplicate groups,
/// duplicate name keys, repeated memberships or memberships in unknown groups.
/// </summary>
public sealed class ApplicationGroupDirectory
{
    private readonly Dictionary<ApplicationGroupId, ApplicationGroup> _groups = new();
    private readonly Dictionary<ApplicationId, ApplicationGroupAssignment> _assignments = new();
    private readonly Dictionary<ApplicationGroupId, List<ApplicationId>> _members = new();

    public static ApplicationGroupDirectory Empty { get; } = new([], []);

    public ApplicationGroupDirectory(
        IEnumerable<ApplicationGroup> groups,
        IEnumerable<ApplicationGroupAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(assignments);

        var nameKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ApplicationGroup group in groups)
        {
            ArgumentNullException.ThrowIfNull(group);
            if (!_groups.TryAdd(group.GroupId, group))
            {
                throw new InvalidDataException($"Application group '{group.GroupId}' is listed twice.");
            }
            if (!nameKeys.Add(group.Name.Key))
            {
                throw new InvalidDataException($"Two application groups share the name '{group.Name}'.");
            }
            _members.Add(group.GroupId, []);
        }

        foreach (ApplicationGroupAssignment assignment in assignments)
        {
            ArgumentNullException.ThrowIfNull(assignment);
            if (!_groups.ContainsKey(assignment.GroupId))
            {
                throw new InvalidDataException(
                    $"Application '{assignment.ApplicationId}' belongs to unknown group '{assignment.GroupId}'.");
            }
            if (!_assignments.TryAdd(assignment.ApplicationId, assignment))
            {
                throw new InvalidDataException(
                    $"Application '{assignment.ApplicationId}' belongs to more than one group.");
            }
            _members[assignment.GroupId].Add(assignment.ApplicationId);
        }

        foreach (List<ApplicationId> members in _members.Values)
        {
            members.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        }

        Groups = _groups.Values
            .OrderBy(static group => group.Name.Key, StringComparer.Ordinal)
            .ThenBy(static group => group.GroupId.Value)
            .ToArray();
    }

    /// <summary>User groups ordered by name; the default group is not listed.</summary>
    public IReadOnlyList<ApplicationGroup> Groups { get; }

    public IReadOnlyCollection<ApplicationGroupAssignment> Assignments => _assignments.Values;

    public ApplicationGroup? FindGroup(ApplicationGroupId groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        return _groups.GetValueOrDefault(groupId);
    }

    /// <summary>The application's user group, or <see langword="null"/> for the default group.</summary>
    public ApplicationGroup? GroupOf(ApplicationId applicationId)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        return _assignments.TryGetValue(applicationId, out ApplicationGroupAssignment? assignment)
            ? _groups[assignment.GroupId]
            : null;
    }

    public bool IsInDefaultGroup(ApplicationId applicationId) => GroupOf(applicationId) is null;

    /// <summary>The members of a user group, in identifier order.</summary>
    public IReadOnlyList<ApplicationId> MembersOf(ApplicationGroupId groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        return _members.TryGetValue(groupId, out List<ApplicationId>? members)
            ? members
            : throw new ArgumentException($"Application group '{groupId}' does not exist.", nameof(groupId));
    }

    /// <summary>The policy capture applies to the application: its group's, else the global one.</summary>
    public ClipboardCapturePolicy EffectivePolicy(ApplicationId applicationId, ClipboardCapturePolicy globalPolicy)
    {
        ArgumentNullException.ThrowIfNull(globalPolicy);
        return GroupOf(applicationId)?.Policy ?? globalPolicy;
    }

    public bool IsNameTaken(ApplicationGroupName name, ApplicationGroupId? except = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _groups.Values.Any(group =>
            string.Equals(group.Name.Key, name.Key, StringComparison.Ordinal) &&
            (except is null || group.GroupId != except));
    }
}
