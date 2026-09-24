using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Clipboard;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private long _applicationPoliciesGeneration;
    private bool _applicationPolicyEditInProgress;
    private ContentDialog? _applicationPolicyDialog;

    private void OnApplicationPoliciesPanelLoaded(object sender, RoutedEventArgs e)
    {
        ApplicationPoliciesTitle.Text = ApplicationPolicyText("Title");
        ApplicationPoliciesBody.Text = ApplicationPolicyText("Body");
        SelectedApplicationDiscoveredFormatsTitle.Text = ApplicationPolicyText("DiscoveredFormats");
        ChangeApplicationGroupButton.Content = ApplicationGroupText("ChooseGroup");
        EditApplicationGroupSettingsButton.Content = ApplicationGroupText("GroupSettings");
        ReloadApplicationPoliciesButton.Content = ApplicationPolicyText("Reload");
        SetApplicationGroupManagementTexts();

        ShellNavigation.SelectionChanged -= OnApplicationPoliciesNavigationSelectionChanged;
        ShellNavigation.SelectionChanged += OnApplicationPoliciesNavigationSelectionChanged;
        _lifecycle.ProtectedDataAccessChanged -= OnApplicationPoliciesProtectedAccessChanged;
        _lifecycle.ProtectedDataAccessChanged += OnApplicationPoliciesProtectedAccessChanged;
        Closed -= OnApplicationPoliciesWindowClosed;
        Closed += OnApplicationPoliciesWindowClosed;

        if (ReferenceEquals(ShellNavigation.SelectedItem, ApplicationsItem))
        {
            _ = LoadApplicationPoliciesAsync();
        }
    }

    private async void OnApplicationPoliciesNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag ||
            !string.Equals(tag, "applications", StringComparison.Ordinal))
        {
            return;
        }

        await LoadApplicationPoliciesAsync();
    }

    private void OnApplicationPoliciesProtectedAccessChanged(bool allowed)
    {
        if (allowed)
        {
            return;
        }

        Interlocked.Increment(ref _applicationPoliciesGeneration);
        if (DispatcherQueue.HasThreadAccess)
        {
            ClearApplicationPoliciesUi();
        }
        else
        {
            DispatcherQueue.TryEnqueue(ClearApplicationPoliciesUi);
        }
    }

    private void OnApplicationPoliciesWindowClosed(object sender, WindowEventArgs e)
    {
        ShellNavigation.SelectionChanged -= OnApplicationPoliciesNavigationSelectionChanged;
        _lifecycle.ProtectedDataAccessChanged -= OnApplicationPoliciesProtectedAccessChanged;
        Closed -= OnApplicationPoliciesWindowClosed;
        Interlocked.Increment(ref _applicationPoliciesGeneration);
        ClearApplicationPoliciesUi();
    }

    private void ClearApplicationPoliciesUi()
    {
        _applicationPolicyDialog?.Hide();
        _applicationPolicyDialog = null;
        ApplicationPoliciesList.SelectedItem = null;
        ApplicationPoliciesList.ItemsSource = null;
        ApplicationPoliciesInfo.IsOpen = false;
        ApplicationPoliciesProgress.IsActive = false;
        ApplicationPoliciesProgress.Visibility = Visibility.Collapsed;
        SetApplicationGroupButtons(null);
        SelectedApplicationIdentity.Text = string.Empty;
        SelectedApplicationPolicySummary.Text = string.Empty;
        SelectedApplicationDiscoveredFormats.Text = string.Empty;
        SelectedApplicationDiscoveredFormatsPanel.Visibility = Visibility.Collapsed;
        ClearApplicationGroupManagementUi(clearList: true);
    }

    private async void OnReloadApplicationPoliciesClicked(object sender, RoutedEventArgs e)
    {
        await LoadApplicationPoliciesAsync();
    }

    private async Task LoadApplicationPoliciesAsync()
    {
        ProtectedStorageSessionLease? session = _protectedStorageSession;
        long generation = Interlocked.Increment(ref _applicationPoliciesGeneration);
        ApplicationPoliciesInfo.IsOpen = false;
        ApplicationPoliciesProgress.IsActive = true;
        ApplicationPoliciesProgress.Visibility = Visibility.Visible;
        SetApplicationGroupButtons(null);
        SelectedApplicationIdentity.Text = string.Empty;
        SelectedApplicationPolicySummary.Text = string.Empty;
        SelectedApplicationDiscoveredFormats.Text = string.Empty;
        SelectedApplicationDiscoveredFormatsPanel.Visibility = Visibility.Collapsed;
        ClearApplicationGroupManagementUi(clearList: false);

        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            ApplicationPoliciesList.ItemsSource = null;
            ApplicationGroupsList.ItemsSource = null;
            ApplicationPoliciesProgress.IsActive = false;
            ApplicationPoliciesProgress.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            (IReadOnlyList<ApplicationIdentitySummary> identities, ApplicationGroupDirectory groups) = await Task.Run(
                async () =>
                {
                    IReadOnlyList<ApplicationIdentitySummary> listed = await new SqliteApplicationIdentityRepository(session)
                        .ListAsync(session.CancellationToken)
                        .ConfigureAwait(false);
                    ApplicationGroupDirectory directory = await new SqliteApplicationGroupRepository(session)
                        .ReadAsync(session.CancellationToken)
                        .ConfigureAwait(false);
                    return (listed, directory);
                },
                session.CancellationToken);

            if (!IsCurrentApplicationPolicyOperation(session, generation))
            {
                return;
            }

            IReadOnlyDictionary<ApplicationId, string> names = ApplicationDisplayName.ForList(identities);
            ApplicationPolicyListItem[] items = identities
                .Select(summary => CreateApplicationListItem(summary, names[summary.ApplicationId], groups))
                .OrderBy(static item => item.Group is null ? 0 : 1)
                .ThenBy(static item => item.Group?.Name.Key, StringComparer.Ordinal)
                .ThenBy(static item => item.ApplicationName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ApplicationPoliciesList.ItemsSource = items;
            PopulateApplicationGroupList(identities, groups);
            if (items.Length == 0)
            {
                ApplicationPoliciesInfo.Severity = InfoBarSeverity.Informational;
                ApplicationPoliciesInfo.Message = ApplicationPolicyText("Empty");
                ApplicationPoliciesInfo.IsOpen = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (IsCurrentApplicationPolicyOperation(session, generation))
            {
                ApplicationPoliciesInfo.Severity = InfoBarSeverity.Error;
                ApplicationPoliciesInfo.Message = ApplicationPolicyText("LoadFailed");
                ApplicationPoliciesInfo.IsOpen = true;
            }
        }
        finally
        {
            if (IsCurrentApplicationPolicyOperation(session, generation))
            {
                ApplicationPoliciesProgress.IsActive = false;
                ApplicationPoliciesProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void OnApplicationPolicySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SetApplicationGroupButtons(null);
        SelectedApplicationIdentity.Text = string.Empty;
        SelectedApplicationPolicySummary.Text = string.Empty;
        SelectedApplicationDiscoveredFormats.Text = string.Empty;
        SelectedApplicationDiscoveredFormatsPanel.Visibility = Visibility.Collapsed;

        if (ApplicationPoliciesList.SelectedItem is not ApplicationPolicyListItem selected)
        {
            return;
        }

        ProtectedStorageSessionLease? session = _protectedStorageSession;
        long generation = Volatile.Read(ref _applicationPoliciesGeneration);
        if (session is null || !IsCurrentApplicationPolicyOperation(session, generation))
        {
            return;
        }

        try
        {
            IReadOnlyList<ApplicationDiscoveredFormat> discoveredFormats =
                await ReadApplicationDiscoveredFormatsAsync(
                    session,
                    selected.Summary.ApplicationId);
            if (!IsCurrentApplicationPolicyOperation(session, generation) ||
                !ReferenceEquals(ApplicationPoliciesList.SelectedItem, selected))
            {
                return;
            }

            SelectedApplicationIdentity.Text = BuildApplicationIdentitySummary(selected.Summary);
            SelectedApplicationPolicySummary.Text = BuildApplicationMembershipSummary(selected.Group);
            SelectedApplicationDiscoveredFormats.Text = BuildApplicationDiscoveredFormatsSummary(discoveredFormats);
            SelectedApplicationDiscoveredFormatsPanel.Visibility = Visibility.Visible;
            SetApplicationGroupButtons(selected);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (IsCurrentApplicationPolicyOperation(session, generation))
            {
                ApplicationPoliciesInfo.Severity = InfoBarSeverity.Error;
                ApplicationPoliciesInfo.Message = ApplicationPolicyText("ReadFailed");
                ApplicationPoliciesInfo.IsOpen = true;
            }
        }
    }

    /// <summary>
    /// A user group can always be chosen or changed; the group settings exist only for an
    /// application in a user group. <see langword="null"/> disables both.
    /// </summary>
    private void SetApplicationGroupButtons(ApplicationPolicyListItem? selected)
    {
        bool enabled = selected is not null && !_applicationPolicyEditInProgress;
        ChangeApplicationGroupButton.Content = ApplicationGroupText(
            selected?.Group is null ? "ChooseGroup" : "MoveToOtherGroup");
        ChangeApplicationGroupButton.IsEnabled = enabled;
        EditApplicationGroupSettingsButton.IsEnabled = enabled && selected!.Group is not null;
    }

    private async Task<IReadOnlyList<ApplicationDiscoveredFormat>> ReadApplicationDiscoveredFormatsAsync(
        ProtectedStorageSessionLease session,
        global::Clipensk.Core.Applications.ApplicationId applicationId)
    {
        return await Task.Run(
            async () => await new SqliteApplicationDiscoveredFormatRepository(session)
                .ListAsync(applicationId, session.CancellationToken)
                .ConfigureAwait(false),
            session.CancellationToken);
    }

    private bool IsCurrentApplicationPolicyOperation(
        ProtectedStorageSessionLease session,
        long generation) =>
        generation == Volatile.Read(ref _applicationPoliciesGeneration) &&
        ReferenceEquals(session, _protectedStorageSession) &&
        session.IsActive &&
        _lifecycle.CanAccessProtectedData;

    private string ApplicationPolicyText(string key) =>
        _localization.GetString("ApplicationPolicy." + key);

    private string ApplicationGroupText(string key) =>
        _localization.GetString("ApplicationGroup." + key);

    /// <summary>Fills <c>{0}</c>, <c>{1}</c>, … without <see cref="string.Format(string, object[])"/>, so a
    /// translated template with stray braces cannot throw.</summary>
    private static string FillText(string template, params object[] values)
    {
        string result = template;
        for (int index = 0; index < values.Length; index++)
        {
            result = result.Replace(
                "{" + index.ToString(CultureInfo.InvariantCulture) + "}",
                Convert.ToString(values[index], CultureInfo.CurrentCulture),
                StringComparison.Ordinal);
        }
        return result;
    }

    private ApplicationPolicyListItem CreateApplicationListItem(
        ApplicationIdentitySummary summary,
        string applicationName,
        ApplicationGroupDirectory groups)
    {
        ApplicationGroup? group = groups.GroupOf(summary.ApplicationId);
        return new ApplicationPolicyListItem(
            summary,
            applicationName,
            group,
            FillText(
                ApplicationGroupText("ListItem"),
                applicationName,
                group?.Name.Value ?? ApplicationGroupText("DefaultGroup")));
    }

    private string BuildApplicationMembershipSummary(ApplicationGroup? group)
    {
        if (group is null)
        {
            return ApplicationGroupText("DefaultMembership");
        }

        return FillText(ApplicationGroupText("Membership"), group.Name.Value, BuildGroupRulesSummary(group.Policy));
    }

    private string BuildGroupRulesSummary(ClipboardCapturePolicy policy) =>
        FillText(
            ApplicationGroupText("RulesSummary"),
            PolicyText(policy.Capture == ClipboardCapturePolicyRule.Allow ? "Allow" : "Deny"),
            policy.Formats.Count(static pair => pair.Value.Capture == ClipboardCapturePolicyRule.Allow));

    private string BuildApplicationIdentitySummary(ApplicationIdentitySummary summary)
    {
        string aumids = summary.ApplicationUserModelIds.Count == 0
            ? "—"
            : string.Join(Environment.NewLine, summary.ApplicationUserModelIds);
        string paths = summary.ExecutablePaths.Count == 0
            ? "—"
            : string.Join(Environment.NewLine, summary.ExecutablePaths);
        return $"ApplicationId: {summary.ApplicationId}{Environment.NewLine}AUMID: {aumids}{Environment.NewLine}{ApplicationPolicyText("Paths")}: {paths}";
    }

    private string BuildApplicationDiscoveredFormatsSummary(
        IReadOnlyList<ApplicationDiscoveredFormat> formats)
    {
        if (formats.Count == 0)
        {
            return ApplicationPolicyText("DiscoveredFormatsEmpty");
        }

        return string.Join(Environment.NewLine, formats.Select(static format => format.FormatName));
    }

    private sealed record ApplicationPolicyListItem(
        ApplicationIdentitySummary Summary,
        string ApplicationName,
        ApplicationGroup? Group,
        string DisplayName);
}
