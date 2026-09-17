using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private PendingArchiveSplitOperation? _maintenancePendingArchiveSplit;

    private void OnMaintenanceArchiveSplitPanelLoaded(object sender, RoutedEventArgs e)
    {
        MaintenanceSplitTitle.Text = MaintenanceText("Split.Title");
        MaintenanceSplitBody.Text = MaintenanceText("Split.Body");
        MaintenanceSplitBoundary.Header = MaintenanceText("Split.Boundary");
        MaintenanceSplitButton.Content = MaintenanceText("Split.Action");
        MaintenanceSplitResumeButton.Content = MaintenanceText("Split.ResumeAction");

        RefreshMaintenanceSplitSelection();
        UpdateMaintenanceSplitAvailability();
    }

    private void RefreshMaintenanceSplitSelection()
    {
        MaintenanceSplitBoundary.Date = null;

        if (_maintenancePendingArchiveSplit is not null ||
            MaintenanceArchiveSelector.SelectedItem is not MaintenanceArchiveListItem selected ||
            selected.Coverage.StartDate >= selected.Coverage.EndDate)
        {
            UpdateMaintenanceSplitAvailability();
            return;
        }

        MaintenanceSplitBoundary.MinDate = ToMaintenanceDateTimeOffset(selected.Coverage.StartDate);
        MaintenanceSplitBoundary.MaxDate = ToMaintenanceDateTimeOffset(selected.Coverage.EndDate.AddDays(-1));
        UpdateMaintenanceSplitAvailability();
    }

    private void OnMaintenanceSplitBoundaryChanged(
        CalendarDatePicker sender,
        CalendarDatePickerDateChangedEventArgs args)
    {
        UpdateMaintenanceSplitAvailability();
    }

    private void UpdateMaintenanceSplitAvailability(bool busy = false)
    {
        bool protectedAccess = _lifecycle.CanAccessProtectedData &&
            _protectedStorageSession?.IsActive == true;
        bool hasPending = _maintenancePendingArchiveSplit is not null;
        bool canChooseBoundary =
            !busy &&
            protectedAccess &&
            !hasPending &&
            MaintenanceArchiveSelector.SelectedItem is MaintenanceArchiveListItem selected &&
            selected.Coverage.StartDate < selected.Coverage.EndDate;

        MaintenanceSplitBoundary.IsEnabled = canChooseBoundary;
        MaintenanceSplitButton.IsEnabled =
            canChooseBoundary &&
            TryBuildMaintenanceSplitRanges(out _, out _, out _);
        MaintenanceSplitResumeButton.Visibility = hasPending
            ? Visibility.Visible
            : Visibility.Collapsed;
        MaintenanceSplitResumeButton.IsEnabled = !busy && protectedAccess && hasPending;
        MaintenanceSplitStatus.Visibility = hasPending
            ? Visibility.Visible
            : Visibility.Collapsed;
        MaintenanceSplitStatus.Text = hasPending
            ? string.Format(
                CultureInfo.CurrentCulture,
                MaintenanceText("Split.Pending"),
                _maintenancePendingArchiveSplit!.SourceFileName.FileName)
            : string.Empty;
    }

    private void SetMaintenancePendingArchiveSplit(PendingArchiveSplitOperation? pending)
    {
        _maintenancePendingArchiveSplit = pending;
        if (pending is not null)
        {
            MaintenanceSplitBoundary.Date = null;
        }

        UpdateMaintenanceSplitAvailability(MaintenanceProgress.IsActive);
    }

    private bool TryBuildMaintenanceSplitRanges(
        out MaintenanceArchiveListItem? selected,
        out JournalDateRange first,
        out JournalDateRange second)
    {
        selected = MaintenanceArchiveSelector.SelectedItem as MaintenanceArchiveListItem;
        first = default;
        second = default;
        if (_maintenancePendingArchiveSplit is not null ||
            selected is null ||
            MaintenanceSplitBoundary.Date is not DateTimeOffset boundaryValue)
        {
            return false;
        }

        DateOnly boundary = DateOnly.FromDateTime(boundaryValue.DateTime);
        if (boundary < selected.Coverage.StartDate ||
            boundary >= selected.Coverage.EndDate)
        {
            return false;
        }

        first = new JournalDateRange(selected.Coverage.StartDate, boundary);
        second = new JournalDateRange(boundary.AddDays(1), selected.Coverage.EndDate);
        return true;
    }

    private async void OnMaintenanceSplitClicked(object sender, RoutedEventArgs e)
    {
        if (!TryBuildMaintenanceSplitRanges(
                out MaintenanceArchiveListItem? selected,
                out JournalDateRange first,
                out JournalDateRange second))
        {
            ShowMaintenanceSplitError(MaintenanceText("Split.InvalidBoundary"));
            return;
        }

        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            ShowMaintenanceLocked();
            return;
        }

        if (!await ConfirmMaintenanceSplitAsync(selected!, first, second))
        {
            return;
        }

        long generation = Interlocked.Increment(ref _maintenanceGeneration);
        MaintenanceInfo.IsOpen = false;
        SetMaintenanceBusy(true);
        try
        {
            var expectedSource = new ArchiveSegmentDescriptor(
                selected!.DatabaseId,
                selected.FileName.FileName,
                selected.Coverage,
                selected.IsSealed);

            IReadOnlyList<ArchiveSegmentDescriptor> result =
                await new ProtectedArchiveSplitStartService(session)
                    .StartAndCompleteAsync(
                        expectedSource,
                        [first, second],
                        DateOnly.FromDateTime(DateTime.Now),
                        session.CancellationToken);

            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            SetMaintenancePendingArchiveSplit(null);
            SetMaintenanceArchiveItems(result, selected.FileName, first);
            MaintenanceSplitBoundary.Date = null;
            MaintenanceInfo.Severity = InfoBarSeverity.Success;
            MaintenanceInfo.Message = string.Format(
                CultureInfo.CurrentCulture,
                MaintenanceText("Split.Completed"),
                selected.FileName.FileName,
                result.Count);
            MaintenanceInfo.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (IsCurrentMaintenanceOperation(session, generation))
            {
                await RefreshMaintenancePendingSplitAsync(session);
                ShowMaintenanceSplitError(MaintenanceText("Split.Failed"));
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

    private async void OnMaintenanceSplitResumeClicked(object sender, RoutedEventArgs e)
    {
        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            ShowMaintenanceLocked();
            return;
        }

        PendingArchiveSplitOperation? pending;
        try
        {
            pending = await new SqlitePendingArchiveSplitRepository(session)
                .ReadAsync(session.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            ShowMaintenanceSplitError(MaintenanceText("Split.ResumeFailed"));
            return;
        }

        if (pending is null)
        {
            SetMaintenancePendingArchiveSplit(null);
            await LoadMaintenanceArchivesAsync();
            return;
        }

        SetMaintenancePendingArchiveSplit(pending);
        if (!await ConfirmMaintenanceSplitResumeAsync(pending))
        {
            return;
        }

        long generation = Interlocked.Increment(ref _maintenanceGeneration);
        MaintenanceInfo.IsOpen = false;
        SetMaintenanceBusy(true);
        try
        {
            IReadOnlyList<ArchiveSegmentDescriptor> result =
                await new ProtectedArchiveSplitRecoveryService(session)
                    .RecoverAsync(
                        pending.OperationId,
                        DateOnly.FromDateTime(DateTime.Now),
                        session.CancellationToken);

            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            SetMaintenancePendingArchiveSplit(null);
            SetMaintenanceArchiveItems(result, pending.SourceFileName);
            MaintenanceInfo.Severity = InfoBarSeverity.Success;
            MaintenanceInfo.Message = MaintenanceText("Split.ResumeCompleted");
            MaintenanceInfo.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (IsCurrentMaintenanceOperation(session, generation))
            {
                await RefreshMaintenancePendingSplitAsync(session);
                ShowMaintenanceSplitError(MaintenanceText("Split.ResumeFailed"));
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

    private async Task<PendingArchiveSplitOperation?> ReadMaintenancePendingSplitAsync(
        ProtectedStorageSessionLease session)
    {
        return await new SqlitePendingArchiveSplitRepository(session)
            .ReadAsync(session.CancellationToken);
    }

    private async Task RefreshMaintenancePendingSplitAsync(ProtectedStorageSessionLease session)
    {
        try
        {
            PendingArchiveSplitOperation? pending = await ReadMaintenancePendingSplitAsync(session);
            if (ReferenceEquals(_protectedStorageSession, session) &&
                session.IsActive &&
                _lifecycle.CanAccessProtectedData)
            {
                SetMaintenancePendingArchiveSplit(pending);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // A failed marker refresh must not hide the original split/recovery failure.
        }
    }

    private async Task<bool> ConfirmMaintenanceSplitAsync(
        MaintenanceArchiveListItem selected,
        JournalDateRange first,
        JournalDateRange second)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ShellNavigation.XamlRoot,
            Title = MaintenanceText("Split.ConfirmTitle"),
            Content = string.Format(
                CultureInfo.CurrentCulture,
                MaintenanceText("Split.ConfirmBody"),
                selected.FileName.FileName,
                selected.Coverage.StartDate,
                selected.Coverage.EndDate,
                first.StartDate,
                first.EndDate,
                second.StartDate,
                second.EndDate),
            PrimaryButtonText = MaintenanceText("Split.ConfirmAction"),
            CloseButtonText = MaintenanceText("Split.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmMaintenanceSplitResumeAsync(
        PendingArchiveSplitOperation pending)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ShellNavigation.XamlRoot,
            Title = MaintenanceText("Split.ResumeConfirmTitle"),
            Content = string.Format(
                CultureInfo.CurrentCulture,
                MaintenanceText("Split.ResumeConfirmBody"),
                pending.SourceFileName.FileName),
            PrimaryButtonText = MaintenanceText("Split.ResumeConfirmAction"),
            CloseButtonText = MaintenanceText("Split.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ShowMaintenanceSplitError(string message)
    {
        MaintenanceInfo.Severity = InfoBarSeverity.Error;
        MaintenanceInfo.Message = message;
        MaintenanceInfo.IsOpen = true;
    }
}
