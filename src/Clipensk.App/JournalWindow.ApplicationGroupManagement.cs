using Clipensk.Core.Applications;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.App;

/// <summary>
/// The group list of the Applications page, per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §7: every
/// group with its members, renaming a user group, editing its settings and deleting an empty one. The
/// default group is listed for its members only; its settings are the global policy on this page.
/// </summary>
public sealed partial class JournalWindow
{
    private void SetApplicationGroupManagementTexts()
    {
        ApplicationGroupsTitle.Text = ApplicationGroupText("ManageTitle");
        ApplicationGroupsBody.Text = ApplicationGroupText("ManageBody");
        ApplicationGroupRenameBox.Header = ApplicationGroupText("RenameHeader");
        ApplicationGroupRenameBox.MaxLength = ApplicationGroupName.MaxLength;
        RenameApplicationGroupButton.Content = ApplicationGroupText("Rename");
        ApplicationGroupSettingsButton.Content = ApplicationGroupText("GroupSettings");
        DeleteApplicationGroupButton.Content = ApplicationGroupText("Delete");
    }

    private void ClearApplicationGroupManagementUi(bool clearList)
    {
        if (clearList)
        {
            ApplicationGroupsList.SelectedItem = null;
            ApplicationGroupsList.ItemsSource = null;
        }

        ApplicationGroupsInfo.IsOpen = false;
        SelectedApplicationGroupSummary.Text = string.Empty;
        ApplicationGroupRenameBox.Text = string.Empty;
        SetApplicationGroupManagementButtons(null);
    }

    /// <summary>Lists the default group first, then the user groups by name, each with its members.</summary>
    private void PopulateApplicationGroupList(
        IReadOnlyList<ApplicationIdentitySummary> identities,
        ApplicationGroupDirectory groups)
    {
        IReadOnlyDictionary<ApplicationId, string> names = ApplicationDisplayName.ForList(identities);

        string[] SortedNames(IEnumerable<ApplicationId> members) => members
            .Select(member => names.TryGetValue(member, out string? name) ? name : member.ToString())
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var items = new List<ApplicationGroupListItem>();
        string[] defaultMembers = SortedNames(identities
            .Select(static summary => summary.ApplicationId)
            .Where(groups.IsInDefaultGroup));
        items.Add(new ApplicationGroupListItem(
            null,
            defaultMembers,
            FillText(ApplicationGroupText("GroupListItem"), ApplicationGroupText("DefaultGroup"), defaultMembers.Length)));
        foreach (ApplicationGroup group in groups.Groups)
        {
            string[] members = SortedNames(groups.MembersOf(group.GroupId));
            items.Add(new ApplicationGroupListItem(
                group,
                members,
                FillText(ApplicationGroupText("GroupListItem"), group.Name.Value, members.Length)));
        }

        ApplicationGroupsList.ItemsSource = items;
    }

    private void OnApplicationGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ApplicationGroupsList.SelectedItem is not ApplicationGroupListItem selected)
        {
            SelectedApplicationGroupSummary.Text = string.Empty;
            ApplicationGroupRenameBox.Text = string.Empty;
            SetApplicationGroupManagementButtons(null);
            return;
        }

        string members = selected.Members.Count == 0
            ? ApplicationGroupText("NoMembers")
            : string.Join(", ", selected.Members);
        SelectedApplicationGroupSummary.Text = selected.Group is { } group
            ? FillText(ApplicationGroupText("GroupSummary"), members, BuildGroupRulesSummary(group.Policy))
            : FillText(ApplicationGroupText("DefaultGroupSummary"), members);
        ApplicationGroupRenameBox.Text = selected.Group?.Name.Value ?? string.Empty;
        SetApplicationGroupManagementButtons(selected);
    }

    /// <summary>
    /// A user group can be renamed and edited; it can be deleted only while it has no members. The
    /// default group offers none of these.
    /// </summary>
    private void SetApplicationGroupManagementButtons(ApplicationGroupListItem? selected)
    {
        bool userGroup = selected?.Group is not null && !_applicationPolicyEditInProgress;
        ApplicationGroupRenameBox.IsEnabled = userGroup;
        RenameApplicationGroupButton.IsEnabled = userGroup;
        ApplicationGroupSettingsButton.IsEnabled = userGroup;
        DeleteApplicationGroupButton.IsEnabled = userGroup && selected!.Members.Count == 0;
    }

    private async void OnRenameApplicationGroupClicked(object sender, RoutedEventArgs e)
    {
        if (_applicationPolicyEditInProgress ||
            ApplicationGroupsList.SelectedItem is not ApplicationGroupListItem { Group: { } group })
        {
            return;
        }

        // The name is checked here, before anything is written or reread, so a rejected name stays
        // in the box for correction.
        if (!ApplicationGroupName.TryCreate(
                ApplicationGroupRenameBox.Text,
                out ApplicationGroupName? name,
                out ApplicationGroupNameError nameError))
        {
            ShowApplicationGroupsMessage(
                ApplicationGroupText(nameError == ApplicationGroupNameError.Empty ? "NameRequired" : "NameInvalid"),
                InfoBarSeverity.Error);
            return;
        }
        ApplicationGroupName newName = name!;
        if (string.Equals(newName.Value, group.Name.Value, StringComparison.Ordinal))
        {
            ShowApplicationGroupsMessage(ApplicationGroupText("RenameUnchanged"), InfoBarSeverity.Informational);
            return;
        }

        await RunApplicationPageGroupChangeAsync(
            async (session, _) =>
            {
                try
                {
                    await Task.Run(
                        () => new ProtectedCapturePolicyPublishService(session)
                            .RenameGroupAsync(group.GroupId, newName, session.CancellationToken),
                        session.CancellationToken);
                    return new GroupChangeResult(
                        FillText(ApplicationGroupText("Renamed"), group.Name.Value, newName.Value),
                        InfoBarSeverity.Success);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (ApplicationGroupNameTakenException)
                {
                    return new GroupChangeResult(ApplicationGroupText("NameTaken"), InfoBarSeverity.Error);
                }
                catch (PendingPolicyMaintenanceException)
                {
                    return new GroupChangeResult(ApplicationGroupText("MaintenancePending"), InfoBarSeverity.Error);
                }
                catch
                {
                    return new GroupChangeResult(ApplicationGroupText("RenameFailed"), InfoBarSeverity.Error);
                }
            },
            ApplicationGroupsInfo);
    }

    private async void OnApplicationGroupSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (ApplicationGroupsList.SelectedItem is not ApplicationGroupListItem { Group: { } group })
        {
            return;
        }

        await RunApplicationGroupSettingsAsync(group.GroupId, ApplicationGroupsInfo);
    }

    private async void OnDeleteApplicationGroupClicked(object sender, RoutedEventArgs e)
    {
        if (ApplicationGroupsList.SelectedItem is not ApplicationGroupListItem { Group: { } group, Members.Count: 0 })
        {
            return;
        }

        await RunApplicationPageGroupChangeAsync(
            async (session, isCurrent) =>
            {
                var dialog = new ContentDialog
                {
                    Title = FillText(ApplicationGroupText("DeleteTitle"), group.Name.Value),
                    Content = CreateWrappedText(ApplicationGroupText("DeleteBody")),
                    PrimaryButtonText = ApplicationGroupText("DeleteConfirm"),
                    CloseButtonText = ApplicationGroupText("Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = ShellNavigation.XamlRoot,
                };
                if (await ShowApplicationGroupDialogAsync(dialog) != ContentDialogResult.Primary || !isCurrent())
                {
                    return null;
                }

                try
                {
                    // Storage deletes the group only while it still has no members.
                    await Task.Run(
                        () => new ProtectedCapturePolicyPublishService(session)
                            .DeleteEmptyGroupAsync(group.GroupId, session.CancellationToken),
                        session.CancellationToken);
                    return new GroupChangeResult(
                        FillText(ApplicationGroupText("Deleted"), group.Name.Value),
                        InfoBarSeverity.Success);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (PendingPolicyMaintenanceException)
                {
                    return new GroupChangeResult(ApplicationGroupText("MaintenancePending"), InfoBarSeverity.Error);
                }
                catch
                {
                    return new GroupChangeResult(ApplicationGroupText("DeleteFailed"), InfoBarSeverity.Error);
                }
            },
            ApplicationGroupsInfo);
    }

    private void ShowApplicationGroupsMessage(string message, InfoBarSeverity severity)
    {
        ApplicationGroupsInfo.Severity = severity;
        ApplicationGroupsInfo.Message = message;
        ApplicationGroupsInfo.IsOpen = true;
    }

    /// <summary>A group of the list; <see cref="Group"/> is <see langword="null"/> for the default group.</summary>
    private sealed record ApplicationGroupListItem(
        ApplicationGroup? Group,
        IReadOnlyList<string> Members,
        string DisplayName);
}
