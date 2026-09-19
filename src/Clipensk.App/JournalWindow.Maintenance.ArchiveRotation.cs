using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void OnMaintenanceArchiveRotationPanelLoaded(object sender, RoutedEventArgs e)
    {
        MaintenanceRotationTitle.Text = MaintenanceText("Rotation.Title");
        MaintenanceRotationBody.Text = MaintenanceText("Rotation.Body");
        MaintenanceRotateButton.Content = MaintenanceText("Rotation.Action");
        UpdateMaintenanceRotationAvailability();
    }

    /// <summary>
    /// Manual rotation uses the persisted thresholds; there is no separate manual rule. Without
    /// configured thresholds there is nothing to evaluate, so the action stays disabled and says so.
    /// </summary>
    private void UpdateMaintenanceRotationAvailability(bool busy = false)
    {
        bool protectedAccess = _lifecycle.CanAccessProtectedData &&
            _protectedStorageSession?.IsActive == true;
        bool configured = _settings.ArchiveRotation?.IsConfigured == true;

        MaintenanceRotateButton.IsEnabled = !busy && protectedAccess && configured;
        MaintenanceRotationStatus.Text = configured
            ? MaintenanceText("Rotation.Configured")
            : MaintenanceText("Rotation.NotConfigured");
    }

    private async void OnMaintenanceRotateClicked(object sender, RoutedEventArgs e)
    {
        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            ShowMaintenanceLocked();
            return;
        }

        if (_settings.ArchiveRotation is not { IsConfigured: true } settings)
        {
            UpdateMaintenanceRotationAvailability();
            return;
        }

        if (Application.Current is not App app)
        {
            return;
        }

        long generation = Interlocked.Increment(ref _maintenanceGeneration);
        MaintenanceInfo.IsOpen = false;
        SetMaintenanceBusy(true);
        UpdateMaintenanceRotationAvailability(true);
        try
        {
            ArchiveRotationRunResult? result = await app.TryRunArchiveRotationAsync(
                session,
                settings,
                session.CancellationToken);

            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            if (result is null)
            {
                MaintenanceInfo.Severity = InfoBarSeverity.Informational;
                MaintenanceInfo.Message = MaintenanceText("Rotation.NotStarted");
                MaintenanceInfo.IsOpen = true;
                return;
            }

            if (!result.Started)
            {
                MaintenanceInfo.Severity = InfoBarSeverity.Informational;
                MaintenanceInfo.Message = MaintenanceText("Rotation.NothingDue");
                MaintenanceInfo.IsOpen = true;
                return;
            }

            await LoadMaintenanceArchivesAsync();
            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            MaintenanceInfo.Severity = InfoBarSeverity.Success;
            MaintenanceInfo.Message = string.Format(
                _localization.GetString("Maintenance.Rotation.Completed"),
                result.Targets.Count,
                FormatRotationOpenTail(result.OpenTail));
            MaintenanceInfo.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (IsCurrentMaintenanceOperation(session, generation))
            {
                MaintenanceInfo.Severity = InfoBarSeverity.Error;
                MaintenanceInfo.Message = MaintenanceText("Rotation.Failed");
                MaintenanceInfo.IsOpen = true;
            }
        }
        finally
        {
            if (IsCurrentMaintenanceOperation(session, generation))
            {
                SetMaintenanceBusy(false);
                UpdateMaintenanceRotationAvailability(false);
            }
        }
    }

    private string FormatRotationOpenTail(JournalDateRange? openTail) =>
        openTail is { } tail
            ? string.Format(
                _localization.GetString("Maintenance.Rotation.OpenTail"),
                tail.StartDate,
                tail.EndDate)
            : MaintenanceText("Rotation.NoOpenTail");
}
