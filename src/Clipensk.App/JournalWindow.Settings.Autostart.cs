using Clipensk.Core.Settings;
using Clipensk.Windows.Autostart;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void InitializeAutostartEditor() => LoadAutostartEditor();

    private void LoadAutostartEditor()
    {
        AutostartEnabledCheckBox.IsChecked = _settings.AutostartEnabled;
    }

    private async void OnSaveAutostartClicked(object sender, RoutedEventArgs e)
    {
        bool enabled = AutostartEnabledCheckBox.IsChecked == true;

        try
        {
            // The registry write is the source of truth Windows actually acts on; only persist the
            // setting once it succeeds, so Clipensk never claims a state it failed to establish.
            WindowsAutostartService.SetEnabled(enabled);

            ApplicationSettings updated = _settings with { AutostartEnabled = enabled };
            await _settingsStore.SaveAsync(updated);
            _settings = updated;

            AutostartInfo.Severity = InfoBarSeverity.Success;
            AutostartInfo.Message = _localization.GetString("Settings.Autostart.Saved");
            AutostartInfo.IsOpen = true;
        }
        catch (Exception)
        {
            AutostartInfo.Severity = InfoBarSeverity.Error;
            AutostartInfo.Message = _localization.GetString("Settings.Autostart.SaveFailed");
            AutostartInfo.IsOpen = true;
        }
    }
}
