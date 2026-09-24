using System.Globalization;
using Clipensk.Core.Input;
using Clipensk.Core.Settings;
using Clipensk.Windows.Autostart;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

/// <summary>
/// The first-run setup as a sequence of steps (smoke finding З4, 2026-09-24): 1 — the storage
/// folder, 2 — the password, 3 — the capture rules, 4 — the hotkey, autostart, auto-lock and the
/// journal's period, each confirmed explicitly. Steps 1 and 2 are the existing first-run and
/// password screens with a step header. Step 3 is required whenever the unlocked storage has no
/// rules — without them nothing is captured — and step 4 until the user has confirmed it once.
/// </summary>
public sealed partial class JournalWindow
{
    private const int InitialSetupStepCount = 4;

    /// <summary>Set by an unlock; the policy load that follows it decides which step, if any, is due.</summary>
    private bool _setupGuideArmed;

    /// <summary>The capture rules page is shown as the rules step.</summary>
    private bool _policySetupMode;

    private void InitializeInitialSetupUi()
    {
        FirstRunStep.Text = SetupStepText(1);
        LockSetupStep.Text = SetupStepText(2);
        SetupParametersStep.Text = SetupStepText(4);
        SetupParametersTitle.Text = _localization.GetString("Setup.Parameters.Title");
        SetupParametersBody.Text = _localization.GetString("Setup.Parameters.Body");
        SetupHotKeyTitle.Text = _localization.GetString("Settings.HotKey.Title");
        SetupHotKeyBody.Text = _localization.GetString("Setup.Parameters.HotKeyBody");
        SetupHotKeyKey.Header = _localization.GetString("Settings.HotKey.Key");
        SetupHotKeyKey.ItemsSource = BuildHotKeyOptions();
        SetupAutostartTitle.Text = _localization.GetString("Settings.Autostart.Title");
        SetupAutostartCheckBox.Content = _localization.GetString("Settings.Autostart.Enabled");
        SetupAutoLockTitle.Text = _localization.GetString("Settings.Lock.Title");
        SetupAutoLockCheckBox.Content = _localization.GetString("Settings.Lock.AutoLockEnabled");
        SetupAutoLockMinutes.Header = _localization.GetString("Settings.Lock.AutoLockAfterMinutes");
        SetupJournalTitle.Text = _localization.GetString("Settings.JournalPeriod.Title");
        SetupJournalBody.Text = _localization.GetString("Settings.JournalPeriod.Body");
        SetupJournalPeriodDays.Header = _localization.GetString("Settings.JournalPeriod.Days");
        FinishSetupButton.Content = _localization.GetString("Setup.Parameters.Finish");
    }

    private string SetupStepText(int step) =>
        _localization.GetString("Setup.Step")
            .Replace("{0}", step.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("{1}", InitialSetupStepCount.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);

    /// <summary>
    /// Called when the capture rules finish loading or saving. After an unlock it opens the rules
    /// step when the storage has none, else step 4 while that is not confirmed; after the rules step
    /// saves, it moves on to step 4, or to the journal.
    /// </summary>
    private void OnGlobalPolicySettled(PolicyViewState state)
    {
        bool armed = _setupGuideArmed;
        if (state is PolicyViewState.Unconfigured or PolicyViewState.Configured or PolicyViewState.Failed)
        {
            _setupGuideArmed = false;
        }

        if (state == PolicyViewState.Unconfigured && armed)
        {
            // Deferred: the rules are still being set up by the caller of this notification.
            DispatcherQueue.TryEnqueue(ShowPolicySetupStep);
            return;
        }

        if (state != PolicyViewState.Configured || !(armed || _policySetupMode))
        {
            return;
        }

        bool fromRulesStep = _policySetupMode;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_lifecycle.CanAccessProtectedData)
            {
                return;
            }

            if (!_settings.InitialSetupCompleted)
            {
                ShowSetupParametersStep();
            }
            else if (fromRulesStep)
            {
                ShellNavigation.SelectedItem = JournalItem;
                ShowPage("journal");
            }
        });
    }

    /// <summary>
    /// The step that must come before the journal, if one is due: the rules step while the storage
    /// has no rules, step 4 while it is not confirmed.
    /// </summary>
    private bool TryShowPendingSetupStep()
    {
        if (!_lifecycle.CanAccessProtectedData)
        {
            return false;
        }

        if (_policyViewState == PolicyViewState.Unconfigured)
        {
            ShowPolicySetupStep();
            return true;
        }

        if (_policyViewState == PolicyViewState.Configured && !_settings.InitialSetupCompleted)
        {
            ShowSetupParametersStep();
            return true;
        }

        return false;
    }

    private void ShowPolicySetupStep()
    {
        if (!_lifecycle.CanAccessProtectedData)
        {
            return;
        }

        ShellNavigation.SelectedItem = ApplicationsItem;
        ShowPage("applications");
        SetPolicySetupMode(true);
    }

    private void SetPolicySetupMode(bool enabled)
    {
        _policySetupMode = enabled;
        PolicySetupPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ApplicationPoliciesPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ApplicationGroupsPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (enabled)
        {
            PolicySetupStep.Text = _settings.InitialSetupCompleted
                ? _localization.GetString("Setup.Rules.Required")
                : SetupStepText(3);
            PolicySetupBody.Text = _localization.GetString("Setup.Rules.Body");
        }
    }

    private void ShowSetupParametersStep()
    {
        ShellNavigation.SelectedItem = null;
        HideContentPanels();
        SetPolicySetupMode(false);
        LoadSetupParameters();
        SetupParametersPanel.Visibility = Visibility.Visible;
    }

    private void LoadSetupParameters()
    {
        HotKeyGesture? gesture = _settings.JournalHotKey;
        SetupControlModifier.IsChecked = gesture?.Modifiers.HasFlag(HotKeyModifiers.Control) == true;
        SetupAltModifier.IsChecked = gesture?.Modifiers.HasFlag(HotKeyModifiers.Alt) == true;
        SetupShiftModifier.IsChecked = gesture?.Modifiers.HasFlag(HotKeyModifiers.Shift) == true;
        SetupWindowsModifier.IsChecked = gesture?.Modifiers.HasFlag(HotKeyModifiers.Windows) == true;
        SetupHotKeyKey.SelectedItem = gesture is null
            ? null
            : (SetupHotKeyKey.ItemsSource as IEnumerable<HotKeyOption>)?
                .FirstOrDefault(option => option.VirtualKey == gesture.VirtualKey);

        SetupAutostartCheckBox.IsChecked = _settings.AutostartEnabled;
        SetupAutoLockCheckBox.IsChecked = _settings.AutoLockEnabled;
        SetupAutoLockMinutes.Value = _settings.AutoLockAfterMinutes is int minutes ? minutes : double.NaN;
        SetupAutoLockMinutes.IsEnabled = _settings.AutoLockEnabled;
        SetupJournalPeriodDays.Value = _settings.DefaultJournalPeriodDays is int days ? days : double.NaN;
        SetupRotationSummary.Text = DescribeArchiveRotation(_settings.ArchiveRotation);
        SetupParametersInfo.IsOpen = false;
    }

    private void OnSetupAutoLockChanged(object sender, RoutedEventArgs e) =>
        SetupAutoLockMinutes.IsEnabled = SetupAutoLockCheckBox.IsChecked == true;

    private string DescribeArchiveRotation(ArchiveRotationSettings? rotation)
    {
        if (rotation is null || !rotation.IsConfigured)
        {
            return _localization.GetString("Setup.Parameters.RotationOff");
        }

        var thresholds = new List<string>();
        if (rotation.MaxCalendarDays is int days)
        {
            thresholds.Add(FillSetupText("Setup.Parameters.RotationDays", days));
        }

        if (rotation.MaxRecordCount is long records)
        {
            thresholds.Add(FillSetupText("Setup.Parameters.RotationRecords", records));
        }

        if (rotation.MaxBytes is long bytes)
        {
            thresholds.Add(FillSetupText("Setup.Parameters.RotationMegabytes", Math.Max(1, bytes / (1024 * 1024))));
        }

        string separator = _localization.GetString(
            rotation.ThresholdMode == ArchiveRotationThresholdMode.All
                ? "Setup.Parameters.RotationAnd"
                : "Setup.Parameters.RotationOr");
        return FillSetupText("Setup.Parameters.Rotation", string.Join(separator, thresholds));
    }

    private string FillSetupText(string key, object value) =>
        _localization.GetString(key).Replace(
            "{0}",
            Convert.ToString(value, CultureInfo.CurrentCulture),
            StringComparison.Ordinal);

    private async void OnFinishSetupClicked(object sender, RoutedEventArgs e)
    {
        HotKeyModifiers modifiers = HotKeyModifiers.None;
        if (SetupControlModifier.IsChecked == true) modifiers |= HotKeyModifiers.Control;
        if (SetupAltModifier.IsChecked == true) modifiers |= HotKeyModifiers.Alt;
        if (SetupShiftModifier.IsChecked == true) modifiers |= HotKeyModifiers.Shift;
        if (SetupWindowsModifier.IsChecked == true) modifiers |= HotKeyModifiers.Windows;

        bool autoLock = SetupAutoLockCheckBox.IsChecked == true;
        var parameters = new InitialSetupParameters(
            SetupHotKeyKey.SelectedItem is HotKeyOption key ? new HotKeyGesture(modifiers, key.VirtualKey) : null,
            SetupAutostartCheckBox.IsChecked == true,
            autoLock,
            WholeNumberOrNull(SetupAutoLockMinutes.Value),
            WholeNumberOrNull(SetupJournalPeriodDays.Value));

        if (parameters.FindProblem() is { } problem)
        {
            ShowSetupParametersMessage(problem);
            return;
        }

        FinishSetupButton.IsEnabled = false;
        ApplicationSettings previous = _settings;
        bool hotKeyRegistered = false;
        bool autostartChanged = false;
        try
        {
            // Windows acts on the registry and on the registered hotkey, so both are established
            // before the settings claim them; a later failure puts both back.
            if (parameters.AutostartEnabled != previous.AutostartEnabled)
            {
                try
                {
                    WindowsAutostartService.SetEnabled(parameters.AutostartEnabled);
                    autostartChanged = true;
                }
                catch
                {
                    ShowSetupParametersMessage("Settings.Autostart.SaveFailed");
                    return;
                }
            }

            try
            {
                _hotKeyService.Register(parameters.JournalHotKey!);
                hotKeyRegistered = true;
            }
            catch
            {
                RestoreSetupSideEffects(previous, hotKeyRegistered: true, autostartChanged);
                ShowSetupParametersMessage("Setup.Parameters.HotKeyFailed");
                return;
            }

            ApplicationSettings updated = parameters.ApplyTo(previous);
            await _settingsStore.SaveAsync(updated);
            _settings = updated;
        }
        catch
        {
            RestoreSetupSideEffects(previous, hotKeyRegistered, autostartChanged);
            ShowSetupParametersMessage("Setup.Parameters.Failed");
            return;
        }
        finally
        {
            FinishSetupButton.IsEnabled = true;
        }

        // The Settings page shows what is now saved.
        InitializeHotKeyEditor();
        LoadAutostartEditor();
        LoadAutoLockEditor();
        EnsureJournalInitialPeriod();
        ShellNavigation.SelectedItem = JournalItem;
        ShowPage("journal");
    }

    private void RestoreSetupSideEffects(ApplicationSettings previous, bool hotKeyRegistered, bool autostartChanged)
    {
        try
        {
            if (hotKeyRegistered)
            {
                if (previous.JournalHotKey is { } previousHotKey)
                {
                    _hotKeyService.Register(previousHotKey);
                }
                else
                {
                    _hotKeyService.Unregister();
                }
            }
        }
        catch
        {
            // The previous hotkey could not be restored; the saved settings still hold it.
        }

        try
        {
            if (autostartChanged)
            {
                WindowsAutostartService.SetEnabled(previous.AutostartEnabled);
            }
        }
        catch
        {
            // Nothing more can be done; Settings shows and re-applies the saved state.
        }
    }

    private void ShowSetupParametersMessage(string key)
    {
        SetupParametersInfo.Severity = InfoBarSeverity.Error;
        SetupParametersInfo.Message = _localization.GetString(key);
        SetupParametersInfo.IsOpen = true;
    }

    private static int? WholeNumberOrNull(double value) =>
        double.IsNaN(value) ? null : (int)Math.Round(value);
}
