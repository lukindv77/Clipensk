using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void OnMaintenanceCurrentVacuumButtonLoaded(object sender, RoutedEventArgs e)
    {
        MaintenanceCurrentVacuumButton.Content = MaintenancePhysicalText(
            "CurrentVacuum",
            "Уплотнить Current");
    }

    private void OnMaintenanceCurrentOptimizeButtonLoaded(object sender, RoutedEventArgs e)
    {
        MaintenanceCurrentOptimizeButton.Content = MaintenancePhysicalText(
            "CurrentOptimize",
            "Оптимизировать Current");
    }

    private async void OnMaintenanceCurrentVacuumClicked(object sender, RoutedEventArgs e)
    {
        await RunMaintenanceCurrentPhysicalOperationAsync(MaintenancePhysicalOperation.Vacuum);
    }

    private async void OnMaintenanceCurrentOptimizeClicked(object sender, RoutedEventArgs e)
    {
        await RunMaintenanceCurrentPhysicalOperationAsync(MaintenancePhysicalOperation.Optimize);
    }

    private async Task RunMaintenanceCurrentPhysicalOperationAsync(
        MaintenancePhysicalOperation operation)
    {
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
            var service = new ProtectedCurrentDatabaseMaintenanceService(session);
            DatabaseIdentity maintained = operation == MaintenancePhysicalOperation.Vacuum
                ? await service.VacuumAsync(session.CancellationToken)
                : await service.OptimizeAsync(session.CancellationToken);

            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            EnsureCurrentMaintenanceIdentity(session, maintained);

            MaintenanceInfo.Severity = InfoBarSeverity.Success;
            MaintenanceInfo.Message = operation == MaintenancePhysicalOperation.Vacuum
                ? MaintenancePhysicalText(
                    "CurrentVacuumCompleted",
                    "Current уплотнён (VACUUM) и прошёл проверку целостности.")
                : MaintenancePhysicalText(
                    "CurrentOptimizeCompleted",
                    "Current оптимизирован (PRAGMA optimize) и прошёл проверку целостности.");
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
                        "CurrentVacuumFailed",
                        "Не удалось уплотнить Current. Проверьте Current и storage-catalog.db и повторите операцию.")
                    : MaintenancePhysicalText(
                        "CurrentOptimizeFailed",
                        "Не удалось оптимизировать Current. Проверьте Current и storage-catalog.db и повторите операцию.");
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

    private static void EnsureCurrentMaintenanceIdentity(
        ProtectedStorageSessionLease session,
        DatabaseIdentity identity)
    {
        if (identity.StorageId != session.StorageId ||
            identity.Role != DatabaseRole.Current ||
            identity.SchemaVersion != ProtectedStorageDatabaseService.CurrentSchemaVersion ||
            identity.EncryptionVersion != ProtectedStorageDatabaseService.CurrentEncryptionVersion ||
            identity.ArchiveBaseNumber is not null ||
            identity.ArchiveSplitSequence is not null ||
            identity.CoverageStartDate is not null ||
            identity.CoverageEndDate is not null)
        {
            throw new InvalidDataException(
                "Current DatabaseIdentity does not match the active protected storage.");
        }
    }
}
