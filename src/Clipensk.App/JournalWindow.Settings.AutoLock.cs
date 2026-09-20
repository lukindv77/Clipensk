using Clipensk.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void InitializeAutoLockEditor() => LoadAutoLockEditor();

    private void LoadAutoLockEditor()
    {
        AutoLockEnabledCheckBox.IsChecked = _settings.AutoLockEnabled;
        AutoLockAfterMinutesBox.Value = _settings.AutoLockAfterMinutes is int minutes
            ? minutes
            : double.NaN;
        AutoLockAfterMinutesBox.IsEnabled = _settings.AutoLockEnabled;
    }

    private void OnLockNowClicked(object sender, RoutedEventArgs e) => TryLockNow();

    private void OnAutoLockSettingChanged(object sender, RoutedEventArgs e) =>
        AutoLockAfterMinutesBox.IsEnabled = AutoLockEnabledCheckBox.IsChecked == true;

    private async void OnSaveAutoLockClicked(object sender, RoutedEventArgs e)
    {
        bool enabled = AutoLockEnabledCheckBox.IsChecked == true;
        int? minutes = double.IsNaN(AutoLockAfterMinutesBox.Value)
            ? null
            : (int)AutoLockAfterMinutesBox.Value;

        if (minutes is <= 0)
        {
            AutoLockInfo.Severity = InfoBarSeverity.Error;
            AutoLockInfo.Message = _localization.GetString("Settings.Lock.Invalid");
            AutoLockInfo.IsOpen = true;
            return;
        }

        try
        {
            ApplicationSettings updated = _settings with
            {
                AutoLockEnabled = enabled,
                AutoLockAfterMinutes = minutes,
            };
            await _settingsStore.SaveAsync(updated);
            _settings = updated;

            AutoLockInfo.Severity = InfoBarSeverity.Success;
            AutoLockInfo.Message = _localization.GetString(
                enabled && minutes is not null
                    ? "Settings.Lock.Saved"
                    : "Settings.Lock.Disabled");
            AutoLockInfo.IsOpen = true;
        }
        catch (Exception)
        {
            AutoLockInfo.Severity = InfoBarSeverity.Error;
            AutoLockInfo.Message = _localization.GetString("Settings.Lock.SaveFailed");
            AutoLockInfo.IsOpen = true;
        }
    }
}
