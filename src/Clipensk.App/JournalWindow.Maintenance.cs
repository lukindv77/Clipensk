using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private IReadOnlyList<MaintenanceArchiveListItem> _maintenanceArchives = [];
    private long _maintenanceGeneration;

    private void OnMaintenanceContentPanelLoaded(object sender, RoutedEventArgs e)
    {
        MaintenanceBody.Text = MaintenanceText("Body");
        MaintenanceArchiveLabel.Text = MaintenanceText("Archive");
        MaintenanceArchiveSelector.PlaceholderText = MaintenanceText("ArchivePlaceholder");
        MaintenanceStartDate.Header = MaintenanceText("StartDate");
        MaintenanceEndDate.Header = MaintenanceText("EndDate");
        MaintenanceValidateButton.Content = MaintenanceText("Validate");
        MaintenanceTransferButton.Content = MaintenanceText("Transfer");
        MaintenanceReloadButton.Content = MaintenanceText("Reload");

        ShellNavigation.SelectionChanged -= OnMaintenanceNavigationSelectionChanged;
        ShellNavigation.SelectionChanged += OnMaintenanceNavigationSelectionChanged;
        _lifecycle.ProtectedDataAccessChanged -= OnMaintenanceProtectedAccessChanged;
        _lifecycle.ProtectedDataAccessChanged += OnMaintenanceProtectedAccessChanged;
        Closed -= OnMaintenanceContentWindowClosed;
        Closed += OnMaintenanceContentWindowClosed;

        if (ReferenceEquals(ShellNavigation.SelectedItem, MaintenanceItem))
        {
            ShowMaintenanceContent();
            if (_lifecycle.CanAccessProtectedData)
            {
                _ = LoadMaintenanceArchivesAsync();
            }
            else
            {
                ShowMaintenanceLocked();
            }
        }
    }

    private async void OnMaintenanceNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        string? tag = args.SelectedItemContainer?.Tag as string;
        if (!string.Equals(tag, "maintenance", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _maintenanceGeneration);
            MaintenanceContentPanel.Visibility = Visibility.Collapsed;
            if (!string.Equals(tag, "journal", StringComparison.Ordinal))
            {
                PageBody.Visibility = Visibility.Visible;
            }
            return;
        }

        ShowMaintenanceContent();
        if (!_lifecycle.CanAccessProtectedData)
        {
            ShowMaintenanceLocked();
            return;
        }

        await LoadMaintenanceArchivesAsync();
    }

    private void OnMaintenanceProtectedAccessChanged(bool allowed)
    {
        Interlocked.Increment(ref _maintenanceGeneration);

        void RefreshMaintenance()
        {
            ClearMaintenanceProtectedUi();
            if (!ReferenceEquals(ShellNavigation.SelectedItem, MaintenanceItem))
            {
                return;
            }

            ShowMaintenanceContent();
            if (allowed)
            {
                _ = LoadMaintenanceArchivesAsync();
            }
            else
            {
                ShowMaintenanceLocked();
            }
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshMaintenance();
        }
        else
        {
            DispatcherQueue.TryEnqueue(RefreshMaintenance);
        }
    }

    private void OnMaintenanceContentWindowClosed(object sender, WindowEventArgs e)
    {
        ShellNavigation.SelectionChanged -= OnMaintenanceNavigationSelectionChanged;
        _lifecycle.ProtectedDataAccessChanged -= OnMaintenanceProtectedAccessChanged;
        Closed -= OnMaintenanceContentWindowClosed;
        Interlocked.Increment(ref _maintenanceGeneration);
        ClearMaintenanceProtectedUi();
    }

    private async void OnMaintenanceReloadClicked(object sender, RoutedEventArgs e)
    {
        await LoadMaintenanceArchivesAsync();
    }

    private void OnMaintenanceArchiveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        MaintenanceStartDate.Date = null;
        MaintenanceEndDate.Date = null;

        if (MaintenanceArchiveSelector.SelectedItem is MaintenanceArchiveListItem selected)
        {
            MaintenanceStartDate.MinDate = ToMaintenanceDateTimeOffset(selected.Coverage.StartDate);
            MaintenanceStartDate.MaxDate = ToMaintenanceDateTimeOffset(selected.Coverage.EndDate);
            MaintenanceEndDate.MinDate = ToMaintenanceDateTimeOffset(selected.Coverage.StartDate);
            MaintenanceEndDate.MaxDate = ToMaintenanceDateTimeOffset(selected.Coverage.EndDate);
        }

        UpdateMaintenanceTransferAvailability();
    }

    private void OnMaintenanceDateChanged(
        CalendarDatePicker sender,
        CalendarDatePickerDateChangedEventArgs args)
    {
        UpdateMaintenanceTransferAvailability();
    }

    private async void OnMaintenanceValidateClicked(object sender, RoutedEventArgs e)
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
            DatabaseIdentity identity = await new ProtectedArchiveDatabaseService(session)
                .ValidateAsync(selectedArchive.FileName, session.CancellationToken);

            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            if (identity.DatabaseId != selectedArchive.DatabaseId ||
                identity.CoverageStartDate != selectedArchive.Coverage.StartDate ||
                identity.CoverageEndDate != selectedArchive.Coverage.EndDate)
            {
                throw new InvalidDataException(
                    "Archive DatabaseIdentity does not match the storage catalog entry.");
            }

            MaintenanceInfo.Severity = InfoBarSeverity.Success;
            MaintenanceInfo.Message = string.Format(
                CultureInfo.CurrentCulture,
                MaintenanceText("Validated"),
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
                MaintenanceInfo.Message = MaintenanceText("ValidationFailed");
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

    private async void OnMaintenanceTransferClicked(object sender, RoutedEventArgs e)
    {
        if (!TryGetMaintenanceTransfer(
                out MaintenanceArchiveListItem? selectedArchive,
                out JournalDateRange transferRange))
        {
            MaintenanceInfo.Severity = InfoBarSeverity.Error;
            MaintenanceInfo.Message = MaintenanceText("InvalidRange");
            MaintenanceInfo.IsOpen = true;
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
            var service = new ProtectedCurrentToArchiveMaintenanceService(session);
            CurrentToArchiveMaintenanceResult result = await service.TransferAsync(
                selectedArchive!.FileName,
                transferRange,
                session.CancellationToken);

            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            SetMaintenanceArchiveItems(
                result.ArchiveSegments,
                selectedArchive.FileName,
                transferRange);

            MaintenanceInfo.Severity = InfoBarSeverity.Success;
            MaintenanceInfo.Message = result.Transfer.CopiedEventCount == 0
                ? MaintenanceText("NothingTransferred")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    MaintenanceText("Transferred"),
                    result.Transfer.CopiedEventCount,
                    result.Transfer.PurgedEventCount);
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
                MaintenanceInfo.Message = MaintenanceText("TransferFailed");
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

    private async Task LoadMaintenanceArchivesAsync()
    {
        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            if (ReferenceEquals(ShellNavigation.SelectedItem, MaintenanceItem))
            {
                ShowMaintenanceLocked();
            }
            return;
        }

        long generation = Interlocked.Increment(ref _maintenanceGeneration);
        MaintenanceInfo.IsOpen = false;
        SetMaintenanceBusy(true);
        try
        {
            IReadOnlyList<ArchiveSegmentDescriptor> archives =
                await new ProtectedArchiveSegmentCatalog(session)
                    .ReadAsync(session.CancellationToken);

            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            SetMaintenanceArchiveItems(archives);
            if (archives.Count == 0)
            {
                MaintenanceInfo.Severity = InfoBarSeverity.Informational;
                MaintenanceInfo.Message = MaintenanceText("Empty");
                MaintenanceInfo.IsOpen = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (IsCurrentMaintenanceOperation(session, generation))
            {
                ClearMaintenanceProtectedUi();
                MaintenanceInfo.Severity = InfoBarSeverity.Error;
                MaintenanceInfo.Message = MaintenanceText("LoadFailed");
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

    private void SetMaintenanceArchiveItems(
        IReadOnlyList<ArchiveSegmentDescriptor> descriptors,
        ArchiveFileName? selectedFileName = null,
        JournalDateRange? selectedRange = null)
    {
        MaintenanceArchiveListItem[] items = descriptors
            .OrderBy(item => item.Coverage.StartDate)
            .ThenBy(item => item.Coverage.EndDate)
            .ThenBy(item => item.FileName, StringComparer.Ordinal)
            .Select(BuildMaintenanceArchiveListItem)
            .ToArray();

        _maintenanceArchives = items;
        MaintenanceArchiveSelector.ItemsSource = items;
        MaintenanceArchiveSelector.SelectedItem = null;

        if (selectedFileName is ArchiveFileName fileName)
        {
            MaintenanceArchiveListItem? selected = items.FirstOrDefault(item => item.FileName == fileName);
            if (selected is not null)
            {
                MaintenanceArchiveSelector.SelectedItem = selected;
                if (selectedRange is JournalDateRange range)
                {
                    MaintenanceStartDate.Date = ToMaintenanceDateTimeOffset(range.StartDate);
                    MaintenanceEndDate.Date = ToMaintenanceDateTimeOffset(range.EndDate);
                }
            }
        }

        UpdateMaintenanceTransferAvailability();
    }

    private MaintenanceArchiveListItem BuildMaintenanceArchiveListItem(
        ArchiveSegmentDescriptor descriptor)
    {
        if (!ArchiveFileName.TryParse(descriptor.FileName, out ArchiveFileName fileName) ||
            !string.Equals(descriptor.FileName, fileName.FileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Catalog archive '{descriptor.FileName}' does not use a canonical file name.");
        }

        string displayText = string.Format(
            CultureInfo.CurrentCulture,
            MaintenanceText("ArchiveDisplay"),
            descriptor.FileName,
            descriptor.Coverage.StartDate,
            descriptor.Coverage.EndDate);
        return new MaintenanceArchiveListItem(
            descriptor.DatabaseId,
            fileName,
            descriptor.Coverage,
            displayText);
    }

    private bool TryGetMaintenanceTransfer(
        out MaintenanceArchiveListItem? selectedArchive,
        out JournalDateRange transferRange)
    {
        selectedArchive = MaintenanceArchiveSelector.SelectedItem as MaintenanceArchiveListItem;
        transferRange = default;
        if (selectedArchive is null ||
            MaintenanceStartDate.Date is not DateTimeOffset start ||
            MaintenanceEndDate.Date is not DateTimeOffset end)
        {
            return false;
        }

        DateOnly startDate = DateOnly.FromDateTime(start.DateTime);
        DateOnly endDate = DateOnly.FromDateTime(end.DateTime);
        if (endDate < startDate ||
            startDate < selectedArchive.Coverage.StartDate ||
            endDate > selectedArchive.Coverage.EndDate ||
            endDate >= DateOnly.FromDateTime(DateTime.Now))
        {
            return false;
        }

        transferRange = new JournalDateRange(startDate, endDate);
        return true;
    }

    private bool IsCurrentMaintenanceOperation(
        ProtectedStorageSessionLease session,
        long generation)
    {
        return generation == Volatile.Read(ref _maintenanceGeneration) &&
            ReferenceEquals(_protectedStorageSession, session) &&
            session.IsActive &&
            _lifecycle.CanAccessProtectedData &&
            ReferenceEquals(ShellNavigation.SelectedItem, MaintenanceItem);
    }

    private void ShowMaintenanceContent()
    {
        PageBody.Visibility = Visibility.Collapsed;
        JournalContentPanel.Visibility = Visibility.Collapsed;
        MaintenanceContentPanel.Visibility = Visibility.Visible;
    }

    private void ShowMaintenanceLocked()
    {
        ClearMaintenanceProtectedUi();
        MaintenanceInfo.Severity = InfoBarSeverity.Informational;
        MaintenanceInfo.Message = MaintenanceText("Locked");
        MaintenanceInfo.IsOpen = true;
    }

    private void ClearMaintenanceProtectedUi()
    {
        _maintenanceArchives = [];
        MaintenanceArchiveSelector.ItemsSource = null;
        MaintenanceArchiveSelector.SelectedItem = null;
        MaintenanceStartDate.Date = null;
        MaintenanceEndDate.Date = null;
        MaintenanceInfo.IsOpen = false;
        SetMaintenanceBusy(false);
    }

    private void SetMaintenanceBusy(bool busy)
    {
        bool protectedAccess = _lifecycle.CanAccessProtectedData &&
            _protectedStorageSession?.IsActive == true;
        bool archiveSelected = MaintenanceArchiveSelector.SelectedItem is MaintenanceArchiveListItem;

        MaintenanceProgress.IsActive = busy;
        MaintenanceProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        MaintenanceReloadButton.IsEnabled = !busy && protectedAccess;
        MaintenanceArchiveSelector.IsEnabled = !busy && protectedAccess && _maintenanceArchives.Count > 0;
        MaintenanceStartDate.IsEnabled = !busy && protectedAccess && archiveSelected;
        MaintenanceEndDate.IsEnabled = !busy && protectedAccess && archiveSelected;
        UpdateMaintenanceTransferAvailability(busy);
    }

    private void UpdateMaintenanceTransferAvailability(bool busy = false)
    {
        bool protectedAccess = _lifecycle.CanAccessProtectedData &&
            _protectedStorageSession?.IsActive == true;
        bool archiveSelected = MaintenanceArchiveSelector.SelectedItem is MaintenanceArchiveListItem;

        MaintenanceValidateButton.IsEnabled =
            !busy &&
            protectedAccess &&
            archiveSelected;
        MaintenanceTransferButton.IsEnabled =
            !busy &&
            protectedAccess &&
            archiveSelected &&
            MaintenanceStartDate.Date is not null &&
            MaintenanceEndDate.Date is not null;
    }

    private string MaintenanceText(string suffix) =>
        _localization.GetString($"Maintenance.{suffix}");

    private static DateTimeOffset ToMaintenanceDateTimeOffset(DateOnly date)
    {
        DateTime localMidnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        return new DateTimeOffset(localMidnight);
    }

    private sealed record MaintenanceArchiveListItem(
        Guid DatabaseId,
        ArchiveFileName FileName,
        JournalDateRange Coverage,
        string DisplayText);
}
