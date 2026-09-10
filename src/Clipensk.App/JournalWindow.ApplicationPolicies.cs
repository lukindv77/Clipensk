using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Clipboard;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private long _applicationPoliciesGeneration;
    private bool _applicationPolicyEditInProgress;
    private ContentDialog? _applicationPolicyDialog;

    private void OnApplicationPoliciesPanelLoaded(object sender, RoutedEventArgs e)
    {
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
        EditApplicationPolicyButton.IsEnabled = false;
        SelectedApplicationIdentity.Text = string.Empty;
        SelectedApplicationPolicySummary.Text = string.Empty;
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
        EditApplicationPolicyButton.IsEnabled = false;
        SelectedApplicationIdentity.Text = string.Empty;
        SelectedApplicationPolicySummary.Text = string.Empty;

        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            ApplicationPoliciesList.ItemsSource = null;
            ApplicationPoliciesProgress.IsActive = false;
            ApplicationPoliciesProgress.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            IReadOnlyList<ApplicationIdentitySummary> identities = await Task.Run(
                async () => await new SqliteApplicationIdentityRepository(session)
                    .ListAsync(session.CancellationToken),
                session.CancellationToken);

            if (!IsCurrentApplicationPolicyOperation(session, generation))
            {
                return;
            }

            ApplicationPolicyListItem[] items = identities
                .Select(summary => new ApplicationPolicyListItem(summary, BuildApplicationDisplayName(summary)))
                .ToArray();
            ApplicationPoliciesList.ItemsSource = items;
            if (items.Length == 0)
            {
                ApplicationPoliciesInfo.Severity = InfoBarSeverity.Informational;
                ApplicationPoliciesInfo.Message = "Обнаруженных приложений пока нет. Они появятся после успешного определения источника захвата.";
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
                ApplicationPoliciesInfo.Message = "Не удалось загрузить список приложений из защищённого хранилища.";
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
        EditApplicationPolicyButton.IsEnabled = false;
        SelectedApplicationIdentity.Text = string.Empty;
        SelectedApplicationPolicySummary.Text = string.Empty;

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
            ClipboardCapturePolicy? applicationPolicy = await ReadApplicationPolicyAsync(
                session,
                selected.Summary.ApplicationId);
            if (!IsCurrentApplicationPolicyOperation(session, generation) ||
                !ReferenceEquals(ApplicationPoliciesList.SelectedItem, selected))
            {
                return;
            }

            SelectedApplicationIdentity.Text = BuildApplicationIdentitySummary(selected.Summary);
            SelectedApplicationPolicySummary.Text = BuildApplicationPolicySummary(applicationPolicy);
            EditApplicationPolicyButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (IsCurrentApplicationPolicyOperation(session, generation))
            {
                ApplicationPoliciesInfo.Severity = InfoBarSeverity.Error;
                ApplicationPoliciesInfo.Message = "Не удалось прочитать индивидуальное правило выбранного приложения.";
                ApplicationPoliciesInfo.IsOpen = true;
            }
        }
    }

    private async void OnEditApplicationPolicyClicked(object sender, RoutedEventArgs e)
    {
        if (_applicationPolicyEditInProgress ||
            ApplicationPoliciesList.SelectedItem is not ApplicationPolicyListItem selected)
        {
            return;
        }

        ProtectedStorageSessionLease? session = _protectedStorageSession;
        long generation = Volatile.Read(ref _applicationPoliciesGeneration);
        if (session is null || !IsCurrentApplicationPolicyOperation(session, generation))
        {
            return;
        }

        _applicationPolicyEditInProgress = true;
        EditApplicationPolicyButton.IsEnabled = false;
        try
        {
            ClipboardCapturePolicy? currentPolicy = await ReadApplicationPolicyAsync(
                session,
                selected.Summary.ApplicationId);
            if (!IsCurrentApplicationPolicyOperation(session, generation))
            {
                return;
            }

            var rule = new ComboBox
            {
                Header = "Базовое правило приложения",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                DisplayMemberPath = nameof(ApplicationRuleOption.Label),
                ItemsSource = new[]
                {
                    new ApplicationRuleOption("Наследовать глобальное правило", ClipboardCapturePolicyRule.Inherit),
                    new ApplicationRuleOption(PolicyText("Allow"), ClipboardCapturePolicyRule.Allow),
                    new ApplicationRuleOption(PolicyText("Deny"), ClipboardCapturePolicyRule.Deny),
                },
            };
            rule.SelectedIndex = currentPolicy?.Capture switch
            {
                ClipboardCapturePolicyRule.Allow => 1,
                ClipboardCapturePolicyRule.Deny => 2,
                _ => 0,
            };

            var error = new InfoBar
            {
                IsOpen = false,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
            };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = "Изменяется только базовое правило приложения. Существующие форматные переопределения сохраняются без изменений.",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(error);
            content.Children.Add(rule);

            var dialog = new ContentDialog
            {
                Title = selected.DisplayName,
                PrimaryButtonText = "Применить",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = ShellNavigation.XamlRoot,
                Content = content,
            };

            ClipboardCapturePolicy? appliedPolicy = null;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    if (!IsCurrentApplicationPolicyOperation(session, generation) ||
                        rule.SelectedItem is not ApplicationRuleOption selectedRule)
                    {
                        error.Message = "Защищённая сессия изменилась. Закройте диалог и повторите после разблокировки.";
                        error.IsOpen = true;
                        args.Cancel = true;
                        return;
                    }

                    var requested = new ClipboardCapturePolicy(
                        selectedRule.Rule,
                        currentPolicy?.Formats);
                    if (Application.Current is not App app ||
                        !await app.TryApplyApplicationCapturePolicyChangeAsync(
                            session,
                            selected.Summary.ApplicationId,
                            requested,
                            session.CancellationToken))
                    {
                        error.Message = "Изменение не завершено. Если durable-операция уже началась, сбор оставлен безопасно приостановленным и будет продолжен при следующей разблокировке.";
                        error.IsOpen = true;
                        args.Cancel = true;
                        return;
                    }

                    appliedPolicy = requested;
                }
                finally
                {
                    deferral.Complete();
                }
            };

            _applicationPolicyDialog = dialog;
            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            finally
            {
                if (ReferenceEquals(_applicationPolicyDialog, dialog))
                {
                    _applicationPolicyDialog = null;
                }
            }

            if (result == ContentDialogResult.Primary &&
                appliedPolicy is not null &&
                IsCurrentApplicationPolicyOperation(session, generation))
            {
                SelectedApplicationPolicySummary.Text = BuildApplicationPolicySummary(appliedPolicy);
                ApplicationPoliciesInfo.Severity = InfoBarSeverity.Success;
                ApplicationPoliciesInfo.Message = "Индивидуальное базовое правило приложения сохранено.";
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
                ApplicationPoliciesInfo.Message = "Не удалось подготовить или применить индивидуальное правило приложения.";
                ApplicationPoliciesInfo.IsOpen = true;
            }
        }
        finally
        {
            _applicationPolicyEditInProgress = false;
            if (session is not null &&
                IsCurrentApplicationPolicyOperation(session, generation) &&
                ApplicationPoliciesList.SelectedItem is ApplicationPolicyListItem)
            {
                EditApplicationPolicyButton.IsEnabled = true;
            }
        }
    }

    private async Task<ClipboardCapturePolicy?> ReadApplicationPolicyAsync(
        ProtectedStorageSessionLease session,
        global::Clipensk.Core.Applications.ApplicationId applicationId)
    {
        return await Task.Run(
            async () =>
            {
                ClipboardCapturePolicy? global = await new SqliteGlobalClipboardCapturePolicyRepository(session)
                    .ReadAsync(session.CancellationToken)
                    .ConfigureAwait(false);
                if (global is null)
                {
                    throw new InvalidDataException("Global capture policy is not configured.");
                }

                return await new SqliteClipboardCapturePolicyRepository(session, global)
                    .GetApplicationPolicyAsync(applicationId, session.CancellationToken)
                    .ConfigureAwait(false);
            },
            session.CancellationToken);
    }

    private bool IsCurrentApplicationPolicyOperation(
        ProtectedStorageSessionLease session,
        long generation) =>
        generation == Volatile.Read(ref _applicationPoliciesGeneration) &&
        ReferenceEquals(session, _protectedStorageSession) &&
        session.IsActive &&
        _lifecycle.CanAccessProtectedData;

    private static string BuildApplicationDisplayName(ApplicationIdentitySummary summary)
    {
        string? executable = summary.ExecutablePaths.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(executable))
        {
            string name = Path.GetFileName(executable);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        string? aumid = summary.ApplicationUserModelIds.FirstOrDefault();
        return string.IsNullOrWhiteSpace(aumid) ? summary.ApplicationId.ToString() : aumid;
    }

    private static string BuildApplicationIdentitySummary(ApplicationIdentitySummary summary)
    {
        string aumids = summary.ApplicationUserModelIds.Count == 0
            ? "—"
            : string.Join(Environment.NewLine, summary.ApplicationUserModelIds);
        string paths = summary.ExecutablePaths.Count == 0
            ? "—"
            : string.Join(Environment.NewLine, summary.ExecutablePaths);
        return $"ApplicationId: {summary.ApplicationId}{Environment.NewLine}AUMID: {aumids}{Environment.NewLine}Пути: {paths}";
    }

    private string BuildApplicationPolicySummary(ClipboardCapturePolicy? policy)
    {
        if (policy is null)
        {
            return "Индивидуальное правило не задано; используется глобальная политика.";
        }

        string rule = policy.Capture switch
        {
            ClipboardCapturePolicyRule.Inherit => "Наследовать глобальное правило",
            ClipboardCapturePolicyRule.Allow => PolicyText("Allow"),
            ClipboardCapturePolicyRule.Deny => PolicyText("Deny"),
            _ => policy.Capture.ToString(),
        };
        return $"Базовое правило: {rule}. Форматных переопределений: {policy.Formats.Count}.";
    }

    private sealed record ApplicationPolicyListItem(
        ApplicationIdentitySummary Summary,
        string DisplayName);

    private sealed record ApplicationRuleOption(
        string Label,
        ClipboardCapturePolicyRule Rule);
}
