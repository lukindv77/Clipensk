using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void OnMaintenanceVacuumButtonLoaded(object sender, RoutedEventArgs e)
    {
        MaintenanceVacuumButton.Content = MaintenancePhysicalText(
            "Vacuum",
            "Уплотнить архив");
    }

    private void OnMaintenanceOptimizeButtonLoaded(object sender, RoutedEventArgs e)
    {
        MaintenanceOptimizeButton.Content = MaintenancePhysicalText(
            "Optimize",
            "Оптимизировать архив");
    }

    private async void OnMaintenanceVacuumClicked(object sender, RoutedEventArgs e)
    {
        await RunMaintenancePhysicalOperationAsync(MaintenancePhysicalOperation.Vacuum);
    }

    private async void OnMaintenanceOptimizeClicked(object sender, RoutedEventArgs e)
    {
        await RunMaintenancePhysicalOperationAsync(MaintenancePhysicalOperation.Optimize);
    }

    private async Task RunMaintenancePhysicalOperationAsync(
        MaintenancePhysicalOperation operation)
    {
        if (MaintenanceArchiveSelector.SelectedItem is not MaintenanceArchiveListItem selectedArchive)
        {
            return;
        }

        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            ShowMaintenanceLocked();
            return;
        }

        long generation = Interlocked.Increment(ref _maintenanceGeneration);
        MaintenanceInfo.IsOpen = false;
        SetMaintenanceBusy(true);
        try
        {
            var service = new ProtectedArchiveDatabaseMaintenanceService(session);
            DatabaseIdentity maintained = operation == MaintenancePhysicalOperation.Vacuum
                ? await service.VacuumAsync(
                    selectedArchive.FileName,
                    selectedArchive.DatabaseId,
                    selectedArchive.Coverage,
                    session.CancellationToken)
                : await service.OptimizeAsync(
                    selectedArchive.FileName,
                    selectedArchive.DatabaseId,
                    selectedArchive.Coverage,
                    session.CancellationToken);

            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            EnsureMaintenanceIdentityMatchesSelection(selectedArchive, maintained);

            MaintenanceInfo.Severity = InfoBarSeverity.Success;
            MaintenanceInfo.Message = string.Format(
                CultureInfo.CurrentCulture,
                operation == MaintenancePhysicalOperation.Vacuum
                    ? MaintenancePhysicalText(
                        "VacuumCompleted",
                        "Архив {0} уплотнён (VACUUM) и прошёл проверку целостности.")
                    : MaintenancePhysicalText(
                        "OptimizeCompleted",
                        "Архив {0} оптимизирован (PRAGMA optimize) и прошёл проверку целостности."),
                selectedArchive.FileName.FileName);
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
                MaintenanceInfo.Message = operation == MaintenancePhysicalOperation.Vacuum
                    ? MaintenancePhysicalText(
                        "VacuumFailed",
                        "Не удалось уплотнить архив. Проверьте целостность хранилища и повторите операцию.")
                    : MaintenancePhysicalText(
                        "OptimizeFailed",
                        "Не удалось оптимизировать архив. Проверьте целостность хранилища и повторите операцию.");
                MaintenanceInfo.IsOpen = true;
            }
        }
        finally
        {
            if (IsCurrentMaintenanceOperation(session, generation))
            {
                SetMaintenanceBusy(false);
            }
        }
    }

    private static void EnsureMaintenanceIdentityMatchesSelection(
        MaintenanceArchiveListItem selectedArchive,
        DatabaseIdentity identity)
    {
        if (identity.DatabaseId != selectedArchive.DatabaseId ||
            identity.CoverageStartDate != selectedArchive.Coverage.StartDate ||
            identity.CoverageEndDate != selectedArchive.Coverage.EndDate)
        {
            throw new InvalidDataException(
                "Archive DatabaseIdentity does not match the selected storage catalog segment.");
        }
    }

    private string MaintenancePhysicalText(string suffix, string russianFallback)
    {
        string key = $"Maintenance.{suffix}";
        string localized = _localization.GetString(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? russianFallback
            : localized;
    }

    private enum MaintenancePhysicalOperation
    {
        Vacuum,
        Optimize,
    }
}
