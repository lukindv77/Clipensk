using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void OnMaintenanceSplitButtonLoaded(object sender, RoutedEventArgs e)
    {
        MaintenanceSplitDate.Header = MaintenanceText("SplitDate");
        MaintenanceSplitButton.Content = MaintenanceText("Split");

        MaintenanceArchiveSelector.SelectionChanged -= OnMaintenanceSplitArchiveSelectionChanged;
        MaintenanceArchiveSelector.SelectionChanged += OnMaintenanceSplitArchiveSelectionChanged;
        UpdateMaintenanceSplitSelection();
    }

    private void OnMaintenanceSplitArchiveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateMaintenanceSplitSelection();
    }

    private void UpdateMaintenanceSplitSelection()
    {
        MaintenanceSplitDate.Date = null;
        if (MaintenanceArchiveSelector.SelectedItem is MaintenanceArchiveListItem selected &&
            selected.Coverage.StartDate < selected.Coverage.EndDate)
        {
            MaintenanceSplitDate.MinDate = ToMaintenanceDateTimeOffset(selected.Coverage.StartDate.AddDays(1));
            MaintenanceSplitDate.MaxDate = ToMaintenanceDateTimeOffset(selected.Coverage.EndDate);
        }

        UpdateMaintenanceSplitAvailability();
    }

    private void OnMaintenanceSplitDateChanged(
        CalendarDatePicker sender,
        CalendarDatePickerDateChangedEventArgs args)
    {
        UpdateMaintenanceSplitAvailability();
    }

    private async void OnMaintenanceSplitClicked(object sender, RoutedEventArgs e)
    {
        if (!TryGetMaintenanceSplit(
                out MaintenanceArchiveListItem? selectedArchive,
                out IReadOnlyList<JournalDateRange> resultRanges))
        {
            MaintenanceInfo.Severity = InfoBarSeverity.Error;
            MaintenanceInfo.Message = MaintenanceText("SplitInvalidBoundary");
            MaintenanceInfo.IsOpen = true;
            return;
        }

        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            ShowMaintenanceLocked();
            return;
        }

        ContentDialogResult confirmation = await ConfirmMaintenanceSplitAsync(
            selectedArchive!,
            resultRanges);
        if (confirmation != ContentDialogResult.Primary)
        {
            return;
        }

        if (!ReferenceEquals(_protectedStorageSession, session) ||
            !session.IsActive ||
            !_lifecycle.CanAccessProtectedData)
        {
            ShowMaintenanceLocked();
            return;
        }

        long generation = Interlocked.Increment(ref _maintenanceGeneration);
        MaintenanceInfo.IsOpen = false;
        SetMaintenanceBusy(true);
        try
        {
            var expectedSource = new ArchiveSegmentDescriptor(
                selectedArchive.DatabaseId,
                selectedArchive.FileName.FileName,
                selectedArchive.Coverage,
                selectedArchive.IsSealed);

            IReadOnlyList<ArchiveSegmentDescriptor> archives =
                await new ProtectedArchiveSplitStartService(session)
                    .StartAndCompleteAsync(
                        expectedSource,
                        resultRanges,
                        DateOnly.FromDateTime(DateTime.Now),
                        session.CancellationToken);

            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            SetMaintenanceArchiveItems(archives, selectedArchive.FileName);
            MaintenanceInfo.Severity = InfoBarSeverity.Success;
            MaintenanceInfo.Message = string.Format(
                CultureInfo.CurrentCulture,
                MaintenanceText("SplitCompleted"),
                selectedArchive.FileName.FileName,
                archives.Count);
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
                MaintenanceInfo.Message = MaintenanceText("SplitFailed");
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

    private async Task<ContentDialogResult> ConfirmMaintenanceSplitAsync(
        MaintenanceArchiveListItem selectedArchive,
        IReadOnlyList<JournalDateRange> ranges)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = MaintenanceContentPanel.XamlRoot,
            Title = MaintenanceText("SplitConfirmTitle"),
            Content = string.Format(
                CultureInfo.CurrentCulture,
                MaintenanceText("SplitConfirmBody"),
                selectedArchive.FileName.FileName,
                ranges[0].StartDate,
                ranges[0].EndDate,
                ranges[1].StartDate,
                ranges[1].EndDate),
            PrimaryButtonText = MaintenanceText("SplitConfirmAction"),
            CloseButtonText = MaintenanceText("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync();
    }

    private bool TryGetMaintenanceSplit(
        out MaintenanceArchiveListItem? selectedArchive,
        out IReadOnlyList<JournalDateRange> resultRanges)
    {
        selectedArchive = MaintenanceArchiveSelector.SelectedItem as MaintenanceArchiveListItem;
        resultRanges = [];
        if (selectedArchive is null ||
            MaintenanceSplitDate.Date is not DateTimeOffset splitDateValue)
        {
            return false;
        }

        DateOnly splitDate = DateOnly.FromDateTime(splitDateValue.DateTime);
        if (splitDate <= selectedArchive.Coverage.StartDate ||
            splitDate > selectedArchive.Coverage.EndDate)
        {
            return false;
        }

        resultRanges =
        [
            new JournalDateRange(selectedArchive.Coverage.StartDate, splitDate.AddDays(-1)),
            new JournalDateRange(splitDate, selectedArchive.Coverage.EndDate),
        ];
        return true;
    }

    private void UpdateMaintenanceSplitAvailability(bool busy = false)
    {
        bool protectedAccess = _lifecycle.CanAccessProtectedData &&
            _protectedStorageSession?.IsActive == true;
        bool canSplit = MaintenanceArchiveSelector.SelectedItem is MaintenanceArchiveListItem selected &&
            selected.Coverage.StartDate < selected.Coverage.EndDate;
        bool validBoundary = TryGetMaintenanceSplit(out _, out _);

        MaintenanceSplitDate.IsEnabled = !busy && protectedAccess && canSplit;
        MaintenanceSplitButton.IsEnabled = !busy && protectedAccess && canSplit && validBoundary;
    }

    private async Task<MaintenanceArchiveLoadResult> LoadMaintenanceArchiveSnapshotAsync(
        ProtectedStorageSessionLease session,
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken)
    {
        PendingArchiveSplitOperation? pending =
            await new SqlitePendingArchiveSplitRepository(session)
                .ReadAsync(cancellationToken);

        if (pending is null)
        {
            IReadOnlyList<ArchiveSegmentDescriptor> archives =
                await new ProtectedArchiveSegmentCatalog(session)
                    .ReadAsync(cancellationToken);
            return new MaintenanceArchiveLoadResult(archives, RecoveredPendingSplit: false);
        }

        IReadOnlyList<ArchiveSegmentDescriptor> recovered =
            await new ProtectedArchiveSplitRecoveryService(session)
                .RecoverAsync(
                    pending.OperationId,
                    currentCalendarDate,
                    cancellationToken);
        return new MaintenanceArchiveLoadResult(recovered, RecoveredPendingSplit: true);
    }

    private sealed record MaintenanceArchiveLoadResult(
        IReadOnlyList<ArchiveSegmentDescriptor> Archives,
        bool RecoveredPendingSplit);
}
