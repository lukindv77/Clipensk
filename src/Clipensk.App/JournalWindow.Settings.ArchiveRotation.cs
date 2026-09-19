using Clipensk.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private sealed record ArchiveRotationModeOption(
        string DisplayName,
        ArchiveRotationThresholdMode? Mode);

    private void InitializeArchiveRotationEditor()
    {
        RotationMode.ItemsSource = new[]
        {
            new ArchiveRotationModeOption(
                _localization.GetString("Settings.Rotation.Mode.Unset"),
                null),
            new ArchiveRotationModeOption(
                _localization.GetString("Settings.Rotation.Mode.Any"),
                ArchiveRotationThresholdMode.Any),
            new ArchiveRotationModeOption(
                _localization.GetString("Settings.Rotation.Mode.All"),
                ArchiveRotationThresholdMode.All),
        };

        LoadArchiveRotationEditor();
    }

    private void LoadArchiveRotationEditor()
    {
        ArchiveRotationSettingsDraft draft =
            ArchiveRotationSettingsDraft.FromSettings(_settings.ArchiveRotation);

        SetOptionalNumber(RotationMaxRecords, draft.MaxRecordCount);
        SetOptionalNumber(RotationMaxMegabytes, draft.MaxMegabytes);
        SetOptionalNumber(RotationMaxDays, draft.MaxCalendarDays);
        RotationMode.SelectedIndex = draft.ThresholdMode switch
        {
            ArchiveRotationThresholdMode.Any => 1,
            ArchiveRotationThresholdMode.All => 2,
            _ => 0,
        };

        if (draft.HasSubMegabyteRemainder)
        {
            // Only a hand-edited settings file can reach this. Say so before the user saves and
            // silently changes the stored threshold.
            RotationInfo.Severity = InfoBarSeverity.Warning;
            RotationInfo.Message = _localization.GetString("Settings.Rotation.SizeRounded");
            RotationInfo.IsOpen = true;
        }
    }

    private async void OnSaveArchiveRotationClicked(object sender, RoutedEventArgs e)
    {
        ArchiveRotationSettings? rotation;
        try
        {
            var draft = new ArchiveRotationSettingsDraft
            {
                MaxRecordCount = ReadOptionalNumber(RotationMaxRecords),
                MaxMegabytes = ReadOptionalNumber(RotationMaxMegabytes),
                MaxCalendarDays = (int?)ReadOptionalNumber(RotationMaxDays),
                ThresholdMode = (RotationMode.SelectedItem as ArchiveRotationModeOption)?.Mode,
            };
            rotation = draft.ToSettings();
        }
        catch (Exception)
        {
            // The draft validates against the same contract the storage layer enforces, so an
            // invalid combination never reaches the settings file.
            RotationInfo.Severity = InfoBarSeverity.Error;
            RotationInfo.Message = _localization.GetString("Settings.Rotation.Invalid");
            RotationInfo.IsOpen = true;
            return;
        }

        try
        {
            ApplicationSettings updated = _settings with { ArchiveRotation = rotation };
            await _settingsStore.SaveAsync(updated);
            _settings = updated;

            RotationInfo.Severity = InfoBarSeverity.Success;
            RotationInfo.Message = _localization.GetString(
                rotation is null ? "Settings.Rotation.Disabled" : "Settings.Rotation.Saved");
            RotationInfo.IsOpen = true;
        }
        catch (Exception)
        {
            RotationInfo.Severity = InfoBarSeverity.Error;
            RotationInfo.Message = _localization.GetString("Settings.Rotation.SaveFailed");
            RotationInfo.IsOpen = true;
        }
    }

    /// <summary>
    /// <see cref="NumberBox"/> represents an empty field as <see cref="double.NaN"/>, which is how
    /// this editor expresses "threshold not configured".
    /// </summary>
    private static void SetOptionalNumber(NumberBox box, long? value) =>
        box.Value = value.HasValue ? value.Value : double.NaN;

    private static long? ReadOptionalNumber(NumberBox box) =>
        double.IsNaN(box.Value) ? null : (long)box.Value;
}
