using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void OnMaintenanceArchiveSplitPanelLoaded(object sender, RoutedEventArgs e)
    {
        MaintenanceSplitTitle.Text = MaintenanceSplitText(
            "Title",
            "Разделить архив");
        MaintenanceSplitBody.Text = MaintenanceSplitText(
            "Body",
            "Выберите дату, которая станет последним календарным днём первого сегмента. Второй сегмент начнётся со следующего дня. Перед изменением файлов Clipensk построит и проверит полный shadow-набор.");
        MaintenanceSplitBoundary.Header = MaintenanceSplitText(
            "Boundary",
            "Последний день первого сегмента");
        MaintenanceSplitButton.Content = MaintenanceSplitText(
            "Action",
            "Разделить архив");
        MaintenanceSplitResumeButton.Content = MaintenanceSplitText(
            "ResumeAction",
            "Продолжить незавершённое разделение");

        MaintenanceArchiveSelector.SelectionChanged -= OnMaintenanceSplitArchiveSelectionChanged;
        MaintenanceArchiveSelector.SelectionChanged += OnMaintenanceSplitArchiveSelectionChanged;
        _lifecycle.ProtectedDataAccessChanged -= OnMaintenanceSplitProtectedAccessChanged;
        _lifecycle.ProtectedDataAccessChanged += OnMaintenanceSplitProtectedAccessChanged;
        Closed -= OnMaintenanceArchiveSplitWindowClosed;
        Closed += OnMaintenanceArchiveSplitWindowClosed;

        RefreshMaintenanceSplitSelection();
        _ = RefreshPendingArchiveSplitAsync();
    }

    private void OnMaintenanceArchiveSplitWindowClosed(object sender, WindowEventArgs e)
    {
        MaintenanceArchiveSelector.SelectionChanged -= OnMaintenanceSplitArchiveSelectionChanged;
        _lifecycle.ProtectedDataAccessChanged -= OnMaintenanceSplitProtectedAccessChanged;
        Closed -= OnMaintenanceArchiveSplitWindowClosed;
    }

    private void OnMaintenanceSplitProtectedAccessChanged(bool allowed)
    {
        void Refresh()
        {
            if (!allowed)
            {
                MaintenanceSplitBoundary.Date = null;
                MaintenanceSplitButton.IsEnabled = false;
                MaintenanceSplitResumeButton.Visibility = Visibility.Collapsed;
                return;
            }

            RefreshMaintenanceSplitSelection();
            _ = RefreshPendingArchiveSplitAsync();
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

    private void OnMaintenanceSplitArchiveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshMaintenanceSplitSelection();
    }

    private void RefreshMaintenanceSplitSelection()
    {
        MaintenanceSplitBoundary.Date = null;
        if (MaintenanceArchiveSelector.SelectedItem is not MaintenanceArchiveListItem selected ||
            selected.Coverage.StartDate >= selected.Coverage.EndDate)
        {
            MaintenanceSplitButton.IsEnabled = false;
            return;
        }

        MaintenanceSplitBoundary.MinDate = ToMaintenanceDateTimeOffset(selected.Coverage.StartDate);
        MaintenanceSplitBoundary.MaxDate = ToMaintenanceDateTimeOffset(
            selected.Coverage.EndDate.AddDays(-1));
        UpdateMaintenanceSplitAvailability();
    }

    private void OnMaintenanceSplitBoundaryChanged(
        CalendarDatePicker sender,
        CalendarDatePickerDateChangedEventArgs args)
    {
        UpdateMaintenanceSplitAvailability();
    }

    private void UpdateMaintenanceSplitAvailability()
    {
        bool protectedAccess = _lifecycle.CanAccessProtectedData &&
            _protectedStorageSession?.IsActive == true;
        bool valid = TryBuildMaintenanceSplitRanges(
            out _,
            out _,
            out _);
        MaintenanceSplitButton.IsEnabled = protectedAccess && valid;
    }

    private bool TryBuildMaintenanceSplitRanges(
        out MaintenanceArchiveListItem? selected,
        out JournalDateRange first,
        out JournalDateRange second)
    {
        selected = MaintenanceArchiveSelector.SelectedItem as MaintenanceArchiveListItem;
        first = default;
        second = default;
        if (selected is null ||
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
            ShowMaintenanceSplitError(MaintenanceSplitText(
                "InvalidBoundary",
                "Выберите дату разделения строго внутри периода выбранного архива."));
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
        MaintenanceArchiveSplitPanel.IsEnabled = false;
        try
        {
            IReadOnlyList<ArchiveSegmentDescriptor> result =
                await new ProtectedArchiveSplitCoordinator(session).SplitAsync(
                    selected!.FileName,
                    [first, second],
                    DateOnly.FromDateTime(DateTime.Now),
                    session.CancellationToken);

            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            SetMaintenanceArchiveItems(result, selected.FileName, first);
            MaintenanceSplitBoundary.Date = null;
            MaintenanceInfo.Severity = InfoBarSeverity.Success;
            MaintenanceInfo.Message = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                MaintenanceSplitText(
                    "Completed",
                    "Архив {0} разделён на {1} проверенных сегмента. Каталог перестроен и незавершённый marker очищен."),
                selected.FileName.FileName,
                result.Count);
            MaintenanceInfo.IsOpen = true;
            await RefreshPendingArchiveSplitAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (IsCurrentMaintenanceOperation(session, generation))
            {
                ShowMaintenanceSplitError(MaintenanceSplitText(
                    "Failed",
                    "Разделение архива не завершено. Если durable marker уже создан, используйте «Продолжить незавершённое разделение» после проверки хранилища."));
                await RefreshPendingArchiveSplitAsync();
            }
        }
        finally
        {
            if (IsCurrentMaintenanceOperation(session, generation))
            {
                SetMaintenanceBusy(false);
                MaintenanceArchiveSplitPanel.IsEnabled = true;
                UpdateMaintenanceSplitAvailability();
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

        var coordinator = new ProtectedArchiveSplitCoordinator(session);
        PendingArchiveSplitOperation? pending =
            await coordinator.ReadPendingAsync(session.CancellationToken);
        if (pending is null)
        {
            MaintenanceSplitResumeButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (!await ConfirmMaintenanceSplitResumeAsync(pending))
        {
            return;
        }

        long generation = Interlocked.Increment(ref _maintenanceGeneration);
        MaintenanceInfo.IsOpen = false;
        SetMaintenanceBusy(true);
        MaintenanceArchiveSplitPanel.IsEnabled = false;
        try
        {
            IReadOnlyList<ArchiveSegmentDescriptor> result = await coordinator.ResumeAsync(
                pending.OperationId,
                DateOnly.FromDateTime(DateTime.Now),
                session.CancellationToken);
            if (!IsCurrentMaintenanceOperation(session, generation))
            {
                return;
            }

            SetMaintenanceArchiveItems(result, pending.SourceFileName);
            MaintenanceInfo.Severity = InfoBarSeverity.Success;
            MaintenanceInfo.Message = MaintenanceSplitText(
                "ResumeCompleted",
                "Незавершённое разделение архива успешно продолжено и завершено.");
            MaintenanceInfo.IsOpen = true;
            await RefreshPendingArchiveSplitAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (IsCurrentMaintenanceOperation(session, generation))
            {
                ShowMaintenanceSplitError(MaintenanceSplitText(
                    "ResumeFailed",
                    "Не удалось безопасно продолжить незавершённое разделение. Durable marker сохранён; данные не следует исправлять вручную."));
                await RefreshPendingArchiveSplitAsync();
            }
        }
        finally
        {
            if (IsCurrentMaintenanceOperation(session, generation))
            {
                SetMaintenanceBusy(false);
                MaintenanceArchiveSplitPanel.IsEnabled = true;
                UpdateMaintenanceSplitAvailability();
            }
        }
    }

    private async Task RefreshPendingArchiveSplitAsync()
    {
        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            MaintenanceSplitResumeButton.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            PendingArchiveSplitOperation? pending =
                await new ProtectedArchiveSplitCoordinator(session)
                    .ReadPendingAsync(session.CancellationToken);
            if (!ReferenceEquals(_protectedStorageSession, session) || !session.IsActive)
            {
                return;
            }

            MaintenanceSplitResumeButton.Visibility = pending is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (pending is not null)
            {
                MaintenanceSplitResumeButton.Content = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    MaintenanceSplitText(
                        "ResumePending",
                        "Продолжить разделение {0} · этап {1}"),
                    pending.SourceFileName.FileName,
                    pending.Phase);
                MaintenanceSplitButton.IsEnabled = false;
            }
            else
            {
                UpdateMaintenanceSplitAvailability();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            MaintenanceSplitResumeButton.Visibility = Visibility.Collapsed;
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
            Title = MaintenanceSplitText(
                "ConfirmTitle",
                "Разделить выбранный архив?"),
            Content = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                MaintenanceSplitText(
                    "ConfirmBody",
                    "{0}\nТекущий период: {1:dd.MM.yyyy}–{2:dd.MM.yyyy}\nРезультат: {3:dd.MM.yyyy}–{4:dd.MM.yyyy} и {5:dd.MM.yyyy}–{6:dd.MM.yyyy}.\n\nОперация перестроит физические Archive-файлы и storage-catalog.db. После начала публикации восстановление выполняется только roll-forward."),
                selected.FileName.FileName,
                selected.Coverage.StartDate,
                selected.Coverage.EndDate,
                first.StartDate,
                first.EndDate,
                second.StartDate,
                second.EndDate),
            PrimaryButtonText = MaintenanceSplitText("ConfirmAction", "Разделить"),
            CloseButtonText = MaintenanceSplitText("Cancel", "Отмена"),
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
            Title = MaintenanceSplitText(
                "ResumeConfirmTitle",
                "Продолжить незавершённое разделение?"),
            Content = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                MaintenanceSplitText(
                    "ResumeConfirmBody",
                    "Архив: {0}\nDurable этап: {1}\n\nClipensk проверит фактические файлы и продолжит только допустимый roll-forward путь. Неожиданные коллизии или несоответствия завершатся fail-closed."),
                pending.SourceFileName.FileName,
                pending.Phase),
            PrimaryButtonText = MaintenanceSplitText("ResumeConfirmAction", "Продолжить"),
            CloseButtonText = MaintenanceSplitText("Cancel", "Отмена"),
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

    private string MaintenanceSplitText(string suffix, string russianFallback)
    {
        string key = $"Maintenance.Split.{suffix}";
        string localized = _localization.GetString(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? russianFallback
            : localized;
    }
}
