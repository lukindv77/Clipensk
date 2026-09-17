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
    private long _maintenanceSplitProgressCallbackToken;

    private void OnMaintenanceArchiveSplitPanelLoaded(object sender, RoutedEventArgs e)
    {
        MaintenanceSplitTitle.Text = MaintenanceText("Split.Title");
        MaintenanceSplitBody.Text = MaintenanceText("Split.Body");
        MaintenanceSplitBoundary.Header = MaintenanceText("Split.Boundary");
        MaintenanceSplitButton.Content = MaintenanceText("Split.Action");
        MaintenanceSplitResumeButton.Content = MaintenanceText("Split.ResumeAction");

        MaintenanceArchiveSelector.SelectionChanged -= OnMaintenanceSplitArchiveSelectionChanged;
        MaintenanceArchiveSelector.SelectionChanged += OnMaintenanceSplitArchiveSelectionChanged;
        ShellNavigation.SelectionChanged -= OnMaintenanceSplitNavigationSelectionChanged;
        ShellNavigation.SelectionChanged += OnMaintenanceSplitNavigationSelectionChanged;
        _lifecycle.ProtectedDataAccessChanged -= OnMaintenanceSplitProtectedAccessChanged;
        _lifecycle.ProtectedDataAccessChanged += OnMaintenanceSplitProtectedAccessChanged;
        Closed -= OnMaintenanceSplitWindowClosed;
        Closed += OnMaintenanceSplitWindowClosed;

        if (_maintenanceSplitProgressCallbackToken == 0)
        {
            _maintenanceSplitProgressCallbackToken = MaintenanceProgress.RegisterPropertyChangedCallback(
                ProgressRing.IsActiveProperty,
                OnMaintenanceSplitProgressChanged);
        }

        RefreshMaintenanceSplitSelection();
        _ = RefreshMaintenancePendingSplitFromUiAsync();
    }

    private void OnMaintenanceSplitArchiveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshMaintenanceSplitSelection();
    }

    private void OnMaintenanceSplitNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        string? tag = args.SelectedItemContainer?.Tag as string;
        if (string.Equals(tag, "maintenance", StringComparison.Ordinal) &&
            _lifecycle.CanAccessProtectedData)
        {
            _ = RefreshMaintenancePendingSplitFromUiAsync();
        }
    }

    private void OnMaintenanceSplitProtectedAccessChanged(bool allowed)
    {
        void Refresh()
        {
            if (!allowed)
            {
                SetMaintenancePendingArchiveSplit(null);
                return;
            }

            _ = RefreshMaintenancePendingSplitFromUiAsync();
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            Refresh();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    private void OnMaintenanceSplitWindowClosed(object sender, WindowEventArgs e)
    {
        MaintenanceArchiveSelector.SelectionChanged -= OnMaintenanceSplitArchiveSelectionChanged;
        ShellNavigation.SelectionChanged -= OnMaintenanceSplitNavigationSelectionChanged;
        _lifecycle.ProtectedDataAccessChanged -= OnMaintenanceSplitProtectedAccessChanged;
        Closed -= OnMaintenanceSplitWindowClosed;

        if (_maintenanceSplitProgressCallbackToken != 0)
        {
            MaintenanceProgress.UnregisterPropertyChangedCallback(
                ProgressRing.IsActiveProperty,
                _maintenanceSplitProgressCallbackToken);
            _maintenanceSplitProgressCallbackToken = 0;
        }
    }

    private void OnMaintenanceSplitProgressChanged(DependencyObject sender, DependencyProperty dp)
    {
        UpdateMaintenanceSplitAvailability(MaintenanceProgress.IsActive);
    }

    private void RefreshMaintenanceSplitSelection()
    {
        MaintenanceSplitBoundary.Date = null;

        if (_maintenancePendingArchiveSplit is not null ||
            MaintenanceArchiveSelector.SelectedItem is not MaintenanceArchiveListItem selected ||
            selected.Coverage.StartDate >= selected.Coverage.EndDate)
        {
            UpdateMaintenanceSplitAvailability(MaintenanceProgress.IsActive);
            return;
        }

        MaintenanceSplitBoundary.MinDate = ToMaintenanceDateTimeOffset(selected.Coverage.StartDate);
        MaintenanceSplitBoundary.MaxDate = ToMaintenanceDateTimeOffset(selected.Coverage.EndDate.AddDays(-1));
        UpdateMaintenanceSplitAvailability(MaintenanceProgress.IsActive);
    }

    private void OnMaintenanceSplitBoundaryChanged(
        CalendarDatePicker sender,
        CalendarDatePickerDateChangedEventArgs args)
    {
        UpdateMaintenanceSplitAvailability(MaintenanceProgress.IsActive);
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
                _maintenancePendingArchiveSplit!.SourceFileName.FileName,
                _maintenancePendingArchiveSplit.Phase)
            : string.Empty;

        if (hasPending)
        {
            DisableMaintenanceActionsForPendingSplit();
        }
    }

    private void DisableMaintenanceActionsForPendingSplit()
    {
        MaintenanceCurrentVacuumButton.IsEnabled = false;
        MaintenanceCurrentOptimizeButton.IsEnabled = false;
        MaintenanceValidateCatalogButton.IsEnabled = false;
        MaintenanceRebuildCatalogButton.IsEnabled = false;
        MaintenanceReloadButton.IsEnabled = false;
        MaintenanceArchiveSelector.IsEnabled = false;
        MaintenanceStartDate.IsEnabled = false;
        MaintenanceEndDate.IsEnabled = false;
        MaintenanceValidateButton.IsEnabled = false;
        MaintenanceVacuumButton.IsEnabled = false;
        MaintenanceOptimizeButton.IsEnabled = false;
        MaintenanceTransferButton.IsEnabled = false;
    }

    private void SetMaintenancePendingArchiveSplit(PendingArchiveSplitOperation? pending)
    {
        _maintenancePendingArchiveSplit = pending;
        if (pending is not null)
        {
            MaintenanceSplitBoundary.Date = null;
        }
        else
        {
            SetMaintenanceBusy(MaintenanceProgress.IsActive);
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
        UpdateMaintenanceSplitAvailability(true);
        try
        {
            var expectedSource = new ArchiveSegmentDescriptor(
                selected!.DatabaseId,
                selected.FileName.FileName,
                selected.Coverage,
                false);

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
                UpdateMaintenanceSplitAvailability(false);
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
        UpdateMaintenanceSplitAvailability(true);
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
                UpdateMaintenanceSplitAvailability(false);
            }
        }
    }

    private async Task RefreshMaintenancePendingSplitFromUiAsync()
    {
        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            SetMaintenancePendingArchiveSplit(null);
            return;
        }

        await RefreshMaintenancePendingSplitAsync(session, showReadFailure: true);
    }

    private async Task<PendingArchiveSplitOperation?> ReadMaintenancePendingSplitAsync(
        ProtectedStorageSessionLease session)
    {
        return await new SqlitePendingArchiveSplitRepository(session)
            .ReadAsync(session.CancellationToken);
    }

    private async Task RefreshMaintenancePendingSplitAsync(
        ProtectedStorageSessionLease session,
        bool showReadFailure = false)
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
            if (showReadFailure &&
                ReferenceEquals(_protectedStorageSession, session) &&
                session.IsActive &&
                _lifecycle.CanAccessProtectedData)
            {
                ShowMaintenanceSplitError(MaintenanceText("Split.ResumeFailed"));
            }
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
                pending.SourceFileName.FileName,
                pending.Phase),
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
