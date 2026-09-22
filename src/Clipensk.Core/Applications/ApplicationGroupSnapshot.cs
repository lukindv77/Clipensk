namespace Clipensk.Core.Applications;

/// <summary>
/// Consistent view of application groups and of which group roots have a personal capture policy,
/// per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §2. Construction enforces the group invariants, so
/// an instance can never describe a nested group or a member with its own policy.
/// </summary>
public sealed class ApplicationGroupSnapshot
{
    private readonly Dictionary<ApplicationId, ApplicationGroupMembership> _memberships;
    private readonly Dictionary<ApplicationId, List<ApplicationId>> _childrenByRoot;
    private readonly HashSet<ApplicationId> _personallyConfigured;

    public ApplicationGroupSnapshot(
        IEnumerable<ApplicationGroupMembership> memberships,
        IEnumerable<ApplicationId> personallyConfiguredApplicationIds)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(personallyConfiguredApplicationIds);

        _memberships = new Dictionary<ApplicationId, ApplicationGroupMembership>();
        foreach (ApplicationGroupMembership membership in memberships)
        {
            ArgumentNullException.ThrowIfNull(membership);
            if (membership.ApplicationId == membership.ParentApplicationId)
            {
                throw new InvalidDataException("An application cannot be a member of its own group.");
            }
            if (membership.RetainedFromApplicationId == membership.ApplicationId)
            {
                throw new InvalidDataException("A retained identity cannot be retained from itself.");
            }
            if (!_memberships.TryAdd(membership.ApplicationId, membership))
            {
                throw new InvalidDataException("An application cannot belong to more than one group.");
            }
        }

        _childrenByRoot = new Dictionary<ApplicationId, List<ApplicationId>>();
        foreach (ApplicationGroupMembership membership in _memberships.Values)
        {
            if (_memberships.ContainsKey(membership.ParentApplicationId))
            {
                throw new InvalidDataException("Application groups must be flat: a group root cannot be a member.");
            }

            if (!_childrenByRoot.TryGetValue(membership.ParentApplicationId, out List<ApplicationId>? children))
            {
                children = [];
                _childrenByRoot.Add(membership.ParentApplicationId, children);
            }
            children.Add(membership.ApplicationId);
        }

        foreach (List<ApplicationId> children in _childrenByRoot.Values)
        {
            children.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        }

        _personallyConfigured = [];
        foreach (ApplicationId applicationId in personallyConfiguredApplicationIds)
        {
            ArgumentNullException.ThrowIfNull(applicationId);
            if (_memberships.ContainsKey(applicationId))
            {
                throw new InvalidDataException("A group member cannot have its own capture policy.");
            }
            _personallyConfigured.Add(applicationId);
        }
    }

    public static ApplicationGroupSnapshot Empty { get; } = new([], []);

    public IReadOnlyCollection<ApplicationGroupMembership> Memberships => _memberships.Values;

    public bool IsMember(ApplicationId applicationId) =>
        _memberships.ContainsKey(applicationId ?? throw new ArgumentNullException(nameof(applicationId)));

    public ApplicationGroupMembership? GetMembership(ApplicationId applicationId) =>
        _memberships.GetValueOrDefault(applicationId ?? throw new ArgumentNullException(nameof(applicationId)));

    public bool HasMembers(ApplicationId applicationId) =>
        _childrenByRoot.ContainsKey(applicationId ?? throw new ArgumentNullException(nameof(applicationId)));

    /// <summary>The group root that governs capture for <paramref name="applicationId"/>.</summary>
    public ApplicationId RootOf(ApplicationId applicationId)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        return _memberships.TryGetValue(applicationId, out ApplicationGroupMembership? membership)
            ? membership.ParentApplicationId
            : applicationId;
    }

    /// <summary>The root itself followed by its members in a stable order.</summary>
    public IReadOnlyList<ApplicationId> MembersOf(ApplicationId root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (_memberships.ContainsKey(root))
        {
            throw new ArgumentException("Only a group root has members.", nameof(root));
        }

        var result = new List<ApplicationId> { root };
        if (_childrenByRoot.TryGetValue(root, out List<ApplicationId>? children))
        {
            result.AddRange(children);
        }
        return result;
    }

    public bool IsPersonallyConfigured(ApplicationId root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return _personallyConfigured.Contains(root);
    }

    /// <summary>
    /// Whether capture for <paramref name="applicationId"/> is governed by a personal policy — its
    /// group root's — rather than by the global policy alone.
    /// </summary>
    public bool IsGovernedByPersonalPolicy(ApplicationId applicationId) =>
        _personallyConfigured.Contains(RootOf(applicationId));
}
