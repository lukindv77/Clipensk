using Clipensk.Core.Applications;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.App;

/// <summary>
/// Application groups in the journal, per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §8: the group of
/// each record's source by current membership, the group filter with per-member toggles, and the list
/// of applications still in the default group, from which a group can be chosen.
/// </summary>
public sealed partial class JournalWindow
{
    private readonly HashSet<Guid> _journalExcludedGroupMembers = new();
    private ProtectedStorageSessionLease? _journalGroupFilterSession;

    /// <summary>
    /// Refills the group filter from storage, keeping the selected group while it still exists. A
    /// selected group that disappeared resets the filter to all groups.
    /// </summary>
    private async Task RefreshJournalGroupFilterAsync()
    {
        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !IsCurrentJournalSession(session))
        {
            return;
        }

        ApplicationGroupDirectory? groups;
        try
        {
            groups = await Task.Run(
                () => ReadJournalGroupsAsync(session, required: false, session.CancellationToken),
                session.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!IsCurrentJournalSession(session))
        {
            return;
        }

        var items = new List<JournalGroupFilterItem>
        {
            new(JournalGroupFilterKind.All, null, JournalText("Group.All")),
        };
        if (groups is not null)
        {
            items.Add(new JournalGroupFilterItem(JournalGroupFilterKind.Default, null, JournalText("Group.Default")));
            items.AddRange(groups.Groups.Select(group =>
                new JournalGroupFilterItem(JournalGroupFilterKind.User, group.GroupId, group.Name.Value)));
        }

        bool sameSession = ReferenceEquals(_journalGroupFilterSession, session);
        JournalGroupFilterItem? previous = sameSession ? CurrentJournalGroupFilter() : null;
        int index = previous is null
            ? 0
            : items.FindIndex(item => item.Kind == previous.Kind && item.GroupId == previous.GroupId);
        bool selectionLost = index < 0;
        if (selectionLost || !sameSession)
        {
            index = 0;
            _journalExcludedGroupMembers.Clear();
        }

        _journalInitializingPeriod = true;
        try
        {
            JournalGroupFilter.ItemsSource = items;
            JournalGroupFilter.SelectedIndex = index;
        }
        finally
        {
            _journalInitializingPeriod = false;
        }

        _journalGroupFilterSession = session;
        UpdateJournalGroupMembersButton();
        if (selectionLost)
        {
            ResetJournalForPendingQueryChange("Group.Missing");
        }
    }

    private JournalGroupFilterItem? CurrentJournalGroupFilter() =>
        JournalGroupFilter.SelectedItem as JournalGroupFilterItem;

    private void OnJournalGroupFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_journalInitializingPeriod)
        {
            return;
        }

        _journalExcludedGroupMembers.Clear();
        UpdateJournalGroupMembersButton();

        // The group filter and the single-application filter are alternatives, never combined.
        if (CurrentJournalGroupFilter() is { Kind: not JournalGroupFilterKind.All } &&
            CurrentJournalApplicationFilter() is not null)
        {
            _journalInitializingPeriod = true;
            try
            {
                JournalApplicationFilter.SelectedIndex = 0;
            }
            finally
            {
                _journalInitializingPeriod = false;
            }
        }

        ResetJournalForPendingQueryChange("FilterChanged");
    }

    /// <summary>Keeps the group filter off while a single application is chosen.</summary>
    private void ClearJournalGroupFilterForApplicationFilter()
    {
        if (CurrentJournalApplicationFilter() is null ||
            CurrentJournalGroupFilter() is not { Kind: not JournalGroupFilterKind.All })
        {
            return;
        }

        _journalInitializingPeriod = true;
        try
        {
            JournalGroupFilter.SelectedIndex = 0;
        }
        finally
        {
            _journalInitializingPeriod = false;
        }

        _journalExcludedGroupMembers.Clear();
        UpdateJournalGroupMembersButton();
    }

    private void UpdateJournalGroupMembersButton()
    {
        bool grouped = CurrentJournalGroupFilter() is { Kind: not JournalGroupFilterKind.All };
        JournalGroupMembersButton.Visibility = grouped ? Visibility.Visible : Visibility.Collapsed;
        JournalGroupMembersButton.Content = _journalExcludedGroupMembers.Count == 0
            ? JournalText("Group.Members")
            : FillText(JournalText("Group.MembersExcluded"), _journalExcludedGroupMembers.Count);
    }

    /// <summary>
    /// Lists the selected group's current members, each with a toggle that includes or excludes its
    /// records from the filtered journal.
    /// </summary>
    private async void OnJournalGroupMembersFlyoutOpening(object sender, object e)
    {
        JournalGroupMembersPanel.Children.Clear();
        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (CurrentJournalGroupFilter() is not { Kind: not JournalGroupFilterKind.All } group ||
            session is null ||
            !IsCurrentJournalSession(session))
        {
            return;
        }

        JournalGroupMembersPanel.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        try
        {
            (IReadOnlyList<ApplicationIdentitySummary> identities, ApplicationGroupDirectory groups) =
                await ReadJournalApplicationsAsync(session);
            if (!IsCurrentJournalSession(session) || !ReferenceEquals(CurrentJournalGroupFilter(), group))
            {
                return;
            }

            JournalGroupMembersPanel.Children.Clear();
            if (group.Kind == JournalGroupFilterKind.User &&
                (group.GroupId is not { } groupId || groups.FindGroup(groupId) is null))
            {
                JournalGroupMembersPanel.Children.Add(CreateWrappedText(JournalText("Group.Missing")));
                return;
            }

            ApplicationIdentitySummary[] members = identities
                .Where(summary => group.Kind == JournalGroupFilterKind.Default
                    ? groups.IsInDefaultGroup(summary.ApplicationId)
                    : groups.GroupOf(summary.ApplicationId)?.GroupId == group.GroupId)
                .OrderBy(static summary => ApplicationDisplayName.From(summary), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (members.Length == 0)
            {
                JournalGroupMembersPanel.Children.Add(CreateWrappedText(JournalText("Group.MembersEmpty")));
                return;
            }

            JournalGroupMembersPanel.Children.Add(CreateWrappedText(JournalText("Group.MembersHelp")));
            foreach (ApplicationIdentitySummary member in members)
            {
                Guid memberId = member.ApplicationId.Value;
                var toggle = new CheckBox
                {
                    Content = ApplicationDisplayName.From(member),
                    IsChecked = !_journalExcludedGroupMembers.Contains(memberId),
                };
                toggle.Checked += (_, _) => ToggleJournalGroupMember(memberId, include: true);
                toggle.Unchecked += (_, _) => ToggleJournalGroupMember(memberId, include: false);
                JournalGroupMembersPanel.Children.Add(toggle);
            }
        }
        catch (OperationCanceledException)
        {
            JournalGroupMembersPanel.Children.Clear();
        }
        catch
        {
            JournalGroupMembersPanel.Children.Clear();
            JournalGroupMembersPanel.Children.Add(CreateWrappedText(JournalText("Group.MembersFailed")));
        }
    }

    private void ToggleJournalGroupMember(Guid applicationId, bool include)
    {
        bool changed = include
            ? _journalExcludedGroupMembers.Remove(applicationId)
            : _journalExcludedGroupMembers.Add(applicationId);
        if (!changed)
        {
            return;
        }

        UpdateJournalGroupMembersButton();
        ResetJournalForPendingQueryChange("FilterChanged");
    }

    private void ResetJournalGroupFilterAfterGroupRemoved()
    {
        _journalInitializingPeriod = true;
        try
        {
            JournalGroupFilter.SelectedIndex = 0;
        }
        finally
        {
            _journalInitializingPeriod = false;
        }

        _journalExcludedGroupMembers.Clear();
        UpdateJournalGroupMembersButton();
        ResetJournalForPendingQueryChange("Group.Missing");
        _ = RefreshJournalGroupFilterAsync();
    }

    /// <summary>
    /// The source filter of a new journal query. A group filter resolves membership now, in Current:
    /// a user group's members, or for the default group every identity without a membership, minus
    /// the members the user switched off. Records without a source application never match a group.
    /// </summary>
    private static JournalSourceFilter ResolveJournalSourceFilter(
        Guid? applicationId,
        JournalGroupFilterItem? group,
        IReadOnlyCollection<Guid> excludedMembers,
        ApplicationGroupDirectory? groups,
        IReadOnlyList<ApplicationIdentitySummary>? identities)
    {
        if (group is null || group.Kind == JournalGroupFilterKind.All)
        {
            return new JournalSourceFilter(
                applicationId is Guid only ? ClipboardHistoryFilter.ForSource(only) : null,
                GroupMissing: false);
        }

        ArgumentNullException.ThrowIfNull(groups);
        IEnumerable<ApplicationId> members;
        if (group.Kind == JournalGroupFilterKind.Default)
        {
            ArgumentNullException.ThrowIfNull(identities);
            members = identities
                .Select(static summary => summary.ApplicationId)
                .Where(groups.IsInDefaultGroup);
        }
        else if (group.GroupId is { } groupId && groups.FindGroup(groupId) is not null)
        {
            members = groups.MembersOf(groupId);
        }
        else
        {
            return new JournalSourceFilter(null, GroupMissing: true);
        }

        var excluded = excludedMembers.ToHashSet();
        IEnumerable<Guid> sources = members
            .Select(static member => member.Value)
            .Where(member => !excluded.Contains(member));
        if (applicationId is Guid chosen)
        {
            sources = sources.Where(member => member == chosen);
        }

        return new JournalSourceFilter(new ClipboardHistoryFilter(sources), GroupMissing: false);
    }

    /// <summary>
    /// Reads the groups for journal labels and filters. Labels are a convenience: when the groups
    /// cannot be read and no group filter needs them, the journal is shown without group labels.
    /// </summary>
    private static async Task<ApplicationGroupDirectory?> ReadJournalGroupsAsync(
        ProtectedStorageSessionLease session,
        bool required,
        CancellationToken cancellationToken)
    {
        try
        {
            return await new SqliteApplicationGroupRepository(session)
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (!required)
        {
            return null;
        }
    }

    private static Task<(IReadOnlyList<ApplicationIdentitySummary> Identities, ApplicationGroupDirectory Groups)>
        ReadJournalApplicationsAsync(ProtectedStorageSessionLease session) =>
        Task.Run(
            async () =>
            {
                CancellationToken token = session.CancellationToken;
                IReadOnlyList<ApplicationIdentitySummary> identities = await new SqliteApplicationIdentityRepository(session)
                    .ListAsync(token)
                    .ConfigureAwait(false);
                ApplicationGroupDirectory groups = await new SqliteApplicationGroupRepository(session)
                    .ReadAsync(token)
                    .ConfigureAwait(false);
                return (identities, groups);
            },
            session.CancellationToken);

    /// <summary>The record's source and, when it has a source application, that application's group.</summary>
    private string BuildJournalSourceWithGroup(ClipboardHistoryEntry entry, ApplicationGroupDirectory? groups)
    {
        string source = BuildJournalSource(entry);
        if (groups is null || entry.SourceApplicationId is not { } applicationId)
        {
            return source;
        }

        return groups.GroupOf(applicationId) is { } group
            ? FillText(JournalText("SourceGroup"), source, group.Name.Value)
            : FillText(JournalText("SourceDefaultGroup"), source);
    }

    /// <summary>
    /// Lists the applications still in the default group; choosing one opens the same choice as on
    /// the Applications page: create a group with new settings or move into an existing group.
    /// </summary>
    private async void OnJournalUnassignedApplicationsClicked(object sender, RoutedEventArgs e)
    {
        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (_applicationPolicyEditInProgress || session is null || !IsCurrentJournalSession(session))
        {
            return;
        }

        _applicationPolicyEditInProgress = true;
        JournalUnassignedButton.IsEnabled = false;
        GroupChangeResult? result = null;
        try
        {
            (IReadOnlyList<ApplicationIdentitySummary> identities, ApplicationGroupDirectory groups) =
                await ReadJournalApplicationsAsync(session);
            if (!IsCurrentJournalSession(session))
            {
                return;
            }

            JournalUnassignedItem[] items = identities
                .Where(summary => groups.IsInDefaultGroup(summary.ApplicationId))
                .Select(static summary => new JournalUnassignedItem(
                    summary.ApplicationId,
                    ApplicationDisplayName.From(summary)))
                .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var list = new ListView
            {
                ItemsSource = items,
                DisplayMemberPath = nameof(JournalUnassignedItem.DisplayName),
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 420,
            };
            var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
            content.Children.Add(CreateWrappedText(JournalText(items.Length == 0 ? "Unassigned.Empty" : "Unassigned.Help")));
            if (items.Length > 0)
            {
                content.Children.Add(list);
            }

            var dialog = new ContentDialog
            {
                Title = JournalText("Unassigned.Title"),
                PrimaryButtonText = items.Length == 0 ? string.Empty : JournalText("Unassigned.Choose"),
                CloseButtonText = JournalText("Unassigned.Close"),
                IsPrimaryButtonEnabled = false,
                XamlRoot = ShellNavigation.XamlRoot,
                Content = content,
            };
            list.SelectionChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled = list.SelectedItem is JournalUnassignedItem;

            ContentDialogResult choice = await ShowApplicationGroupDialogAsync(dialog);
            if (choice != ContentDialogResult.Primary ||
                list.SelectedItem is not JournalUnassignedItem chosen ||
                !IsCurrentJournalSession(session))
            {
                return;
            }

            result = await RunApplicationGroupMoveDialogAsync(
                session,
                chosen.ApplicationId,
                chosen.DisplayName,
                () => IsCurrentJournalSession(session));
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            result = new GroupChangeResult(JournalText("Unassigned.Failed"), InfoBarSeverity.Error);
        }
        finally
        {
            _applicationPolicyEditInProgress = false;
            JournalUnassignedButton.IsEnabled = true;
        }

        if (result is null || !IsCurrentJournalSession(session))
        {
            return;
        }

        // A completed move can delete records and changes group labels, so a shown page is reread.
        await RefreshJournalGroupFilterAsync();
        if (result.Severity != InfoBarSeverity.Error && _journalPeriod is not null)
        {
            await LoadJournalAsync(reset: true);
        }

        JournalInfo.Severity = result.Severity;
        JournalInfo.Message = result.Message;
        JournalInfo.IsOpen = true;
    }

    private bool IsCurrentJournalSession(ProtectedStorageSessionLease session) =>
        ReferenceEquals(session, _protectedStorageSession) &&
        session.IsActive &&
        _lifecycle.CanAccessProtectedData;

    private enum JournalGroupFilterKind
    {
        All,
        Default,
        User,
    }

    private sealed record JournalGroupFilterItem(
        JournalGroupFilterKind Kind,
        ApplicationGroupId? GroupId,
        string DisplayName);

    private readonly record struct JournalSourceFilter(ClipboardHistoryFilter? Filter, bool GroupMissing);

    private sealed record JournalPage(
        IReadOnlyList<UnifiedClipboardHistoryEntry> Entries,
        ApplicationGroupDirectory? Groups,
        JournalSourceFilter Source);

    private sealed record JournalUnassignedItem(ApplicationId ApplicationId, string DisplayName);
}
