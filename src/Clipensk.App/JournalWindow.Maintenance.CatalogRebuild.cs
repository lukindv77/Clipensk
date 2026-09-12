using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void OnMaintenanceRebuildCatalogButtonLoaded(object sender, RoutedEventArgs e)
    {
        MaintenanceRebuildCatalogButton.Content = MaintenanceText("RebuildCatalog");
    }

    private async void OnMaintenanceRebuildCatalogClicked(object sender, RoutedEventArgs e)
    {
        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            ShowMaintenanceLocked();
            return;
        }

        ArchiveFileName? selectedFileName =
            (MaintenanceArchiveSelector.SelectedItem as MaintenanceArchiveListItem)?.FileName;
        long generation = Interlocked.Increment(ref _maintenanceGeneration);
        MaintenanceInfo.IsOpen = false;
        SetMaintenanceBusy(true);
        try
        {
            IReadOnlyList<ArchiveSegmentDescriptor> rebuilt =
                await new ProtectedArchiveCatalogMaintenanceService(session)
                    .RebuildAsync(session.CancellationToken);

            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            SetMaintenanceArchiveItems(rebuilt, selectedFileName);
            MaintenanceInfo.Severity = InfoBarSeverity.Success;
            MaintenanceInfo.Message = string.Format(
                CultureInfo.CurrentCulture,
                MaintenanceText("CatalogRebuilt"),
                rebuilt.Count);
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
                MaintenanceInfo.Message = MaintenanceText("CatalogRebuildFailed");
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
}
