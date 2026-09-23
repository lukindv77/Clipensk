using Clipensk.Core.Localization;
using Clipensk.Core.Settings;
using Clipensk.Infrastructure.Settings;
using Clipensk.Infrastructure.Storage;
using Clipensk.Windows.Interop;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public partial class App
{
    /// <summary>
    /// Finishes or undoes an interrupted data root relocation at startup, after the single-instance
    /// claim and before anything opens the data root (<c>docs/DATA_ROOT_RELOCATION_PROTOCOL.md</c>
    /// §7). Returns the settings to start with and what to tell the user, or <see langword="null"/>
    /// settings when Clipensk must not start: the marker could not be resolved safely.
    /// </summary>
    private static async Task<(ApplicationSettings? Settings, StartupNotice? Notice)> RecoverDataRootRelocationAsync(
        IApplicationSettingsStore settingsStore,
        ApplicationSettings settings,
        ILocalizationService localization)
    {
        if (settings.PendingDataRootRelocation is not { } marker)
        {
            return (settings, null);
        }

        try
        {
            DataRootRelocationRecoveryResult result = await Task.Run(
                () => new DataRootRelocationService(new SettingsDataRootLocationStore(settingsStore)).RecoverAsync());
            ApplicationSettings recovered = await settingsStore.LoadAsync();
            StartupNotice? notice = result.Outcome switch
            {
                DataRootRelocationRecoveryOutcome.RolledBack => new StartupNotice(
                    Fill(localization.GetString("Relocation.RecoveredRolledBack"), marker.SourcePath),
                    InfoBarSeverity.Warning),
                DataRootRelocationRecoveryOutcome.Completed when result.Retained.Count == 0 => new StartupNotice(
                    Fill(localization.GetString("Relocation.RecoveredCompleted"), marker.TargetPath),
                    InfoBarSeverity.Success),
                DataRootRelocationRecoveryOutcome.Completed => new StartupNotice(
                    Fill(
                        localization.GetString("Relocation.RecoveredCompletedWithRemnants"),
                        marker.TargetPath,
                        marker.SourcePath,
                        result.Retained.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)),
                    InfoBarSeverity.Warning),
                DataRootRelocationRecoveryOutcome.SourceRemovalPending => new StartupNotice(
                    Fill(localization.GetString("Relocation.RecoveredRemovalPending"), marker.TargetPath, marker.SourcePath),
                    InfoBarSeverity.Warning),
                _ => null,
            };
            return (recovered, notice);
        }
        catch when (
            marker.Phase == DataRootRelocationPhase.Copying &&
            settings.DataRootPath is { } dataRoot &&
            DataRootPaths.AreSame(dataRoot, marker.SourcePath))
        {
            // The data root is still the untouched source, so Clipensk can start; the leftover copy
            // is removed at a later start, and no new relocation begins until then.
            return (
                settings,
                new StartupNotice(
                    Fill(localization.GetString("Relocation.RollbackDeferred"), marker.SourcePath, marker.TargetPath),
                    InfoBarSeverity.Warning));
        }
        catch
        {
            WindowsStartupMessage.ShowError(
                Fill(
                    localization.GetString("Startup.RelocationRecoveryFailed"),
                    settings.DataRootPath ?? string.Empty,
                    marker.SourcePath,
                    marker.TargetPath),
                localization.GetString("App.Title"));
            return (null, null);
        }
    }

    private static string Fill(string template, params string[] values)
    {
        string result = template;
        for (int index = 0; index < values.Length; index++)
        {
            result = result.Replace("{" + index + "}", values[index], StringComparison.Ordinal);
        }

        return result;
    }

    private sealed record StartupNotice(string Message, InfoBarSeverity Severity);
}
