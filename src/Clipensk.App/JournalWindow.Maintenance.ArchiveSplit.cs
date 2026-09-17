using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private CalendarDatePicker? _maintenanceSplitDate;
    private Button? _maintenanceSplitButton;

    private void OnMaintenanceContentPanelWithSplitLoaded(object sender, RoutedEventArgs e)
    {
        OnMaintenanceContentPanelLoaded(sender, e);
        InitializeMaintenanceArchiveSplitUi();
    }

    private void InitializeMaintenanceArchiveSplitUi()
    {
        if (_maintenanceSplitDate is not null || _maintenanceSplitButton is not null)
        {
            UpdateMaintenanceSplitSelection();
            return;
        }

        _maintenanceSplitDate = new CalendarDatePicker
        {
            Header = MaintenanceText("SplitDate"),
            IsEnabled = false,
        };
        _maintenanceSplitDate.DateChanged += OnMaintenanceSplitDateChanged;

        _maintenanceSplitButton = new Button
        {
            Content = MaintenanceText("Split"),
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _maintenanceSplitButton.Click += OnMaintenanceSplitClicked;

        var splitPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
        };
        splitPanel.Children.Add(_maintenanceSplitDate);
        splitPanel.Children.Add(_maintenanceSplitButton);
        MaintenanceContentPanel.Children.Add(splitPanel);

        MaintenanceArchiveSelector.SelectionChanged -= OnMaintenanceSplitArchiveSelectionChanged;
        MaintenanceArchiveSelector.SelectionChanged += OnMaintenanceSplitArchiveSelectionChanged;
        MaintenanceProgress.RegisterPropertyChangedCallback(
            ProgressRing.IsActiveProperty,
            OnMaintenanceProgressIsActiveChanged);
        UpdateMaintenanceSplitSelection();
    }

    private void OnMaintenanceProgressIsActiveChanged(
        DependencyObject sender,
        DependencyProperty dependencyProperty)
    {
        UpdateMaintenanceSplitAvailability(MaintenanceProgress.IsActive);
    }

    private void OnMaintenanceSplitArchiveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateMaintenanceSplitSelection();
    }

    private void UpdateMaintenanceSplitSelection()
    {
        if (_maintenanceSplitDate is null)
        {
            return;
        }

        _maintenanceSplitDate.Date = null;
        if (MaintenanceArchiveSelector.SelectedItem is MaintenanceArchiveListItem selected &&
            selected.Coverage.StartDate < selected.Coverage.EndDate)
        {
            _maintenanceSplitDate.MinDate = ToMaintenanceDateTimeOffset(selected.Coverage.StartDate.AddDays(1));
            _maintenanceSplitDate.MaxDate = ToMaintenanceDateTimeOffset(selected.Coverage.EndDate);
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
                IsSealed: false);

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
            _maintenanceSplitDate?.Date is not DateTimeOffset splitDateValue)
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
        if (_maintenanceSplitDate is null || _maintenanceSplitButton is null)
        {
            return;
        }

        bool protectedAccess = _lifecycle.CanAccessProtectedData &&
            _protectedStorageSession?.IsActive == true;
        bool canSplit = MaintenanceArchiveSelector.SelectedItem is MaintenanceArchiveListItem selected &&
            selected.Coverage.StartDate < selected.Coverage.EndDate;
        bool validBoundary = TryGetMaintenanceSplit(out _, out _);

        _maintenanceSplitDate.IsEnabled = !busy && protectedAccess && canSplit;
        _maintenanceSplitButton.IsEnabled = !busy && protectedAccess && canSplit && validBoundary;
    }
}
