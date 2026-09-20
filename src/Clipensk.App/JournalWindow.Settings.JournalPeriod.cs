using Clipensk.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void InitializeJournalPeriodEditor() => LoadJournalPeriodEditor();

    private void LoadJournalPeriodEditor()
    {
        JournalPeriodDays.Value = _settings.DefaultJournalPeriodDays is int days
            ? days
            : double.NaN;
    }

    private async void OnSaveJournalPeriodClicked(object sender, RoutedEventArgs e)
    {
        int? days = double.IsNaN(JournalPeriodDays.Value) ? null : (int)JournalPeriodDays.Value;

        try
        {
            if (days is int value)
            {
                DefaultJournalPeriod.Validate(value);
            }
        }
        catch (Exception)
        {
            JournalPeriodInfo.Severity = InfoBarSeverity.Error;
            JournalPeriodInfo.Message = _localization.GetString("Settings.JournalPeriod.Invalid");
            JournalPeriodInfo.IsOpen = true;
            return;
        }

        try
        {
            ApplicationSettings updated = _settings with { DefaultJournalPeriodDays = days };
            await _settingsStore.SaveAsync(updated);
            _settings = updated;

            JournalPeriodInfo.Severity = InfoBarSeverity.Success;
            JournalPeriodInfo.Message = _localization.GetString(
                days is null
                    ? "Settings.JournalPeriod.Cleared"
                    : "Settings.JournalPeriod.Saved");
            JournalPeriodInfo.IsOpen = true;
        }
        catch (Exception)
        {
            JournalPeriodInfo.Severity = InfoBarSeverity.Error;
            JournalPeriodInfo.Message = _localization.GetString("Settings.JournalPeriod.SaveFailed");
            JournalPeriodInfo.IsOpen = true;
        }
    }
}
