using System.Globalization;
using System.Text;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private const int JournalPageSize = 100;
    private const int JournalPreviewLength = 240;

    private readonly List<JournalListItem> _journalItems = new();
    private long _journalGeneration;
    private bool _journalInitializingPeriod;
    private JournalDateRange? _journalPeriod;
    private ClipboardHistoryCursor? _journalCursor;

    private void OnJournalContentPanelLoaded(object sender, RoutedEventArgs e)
    {
        JournalStartDate.Header = JournalText("StartDate");
        JournalEndDate.Header = JournalText("EndDate");
        JournalLoadButton.Content = JournalText("Load");
        JournalLoadMoreButton.Content = JournalText("LoadMore");

        ShellNavigation.SelectionChanged -= OnJournalNavigationSelectionChanged;
        ShellNavigation.SelectionChanged += OnJournalNavigationSelectionChanged;
        _lifecycle.ProtectedDataAccessChanged -= OnJournalProtectedAccessChanged;
        _lifecycle.ProtectedDataAccessChanged += OnJournalProtectedAccessChanged;
        Closed -= OnJournalWindowClosed;
        Closed += OnJournalWindowClosed;

        EnsureJournalInitialPeriod();
        if (ReferenceEquals(ShellNavigation.SelectedItem, JournalItem) &&
            _lifecycle.CanAccessProtectedData)
        {
            ShowJournalContent();
            _ = LoadJournalAsync(reset: true);
        }
    }

    private async void OnJournalNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag ||
            !string.Equals(tag, "journal", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _journalGeneration);
            JournalContentPanel.Visibility = Visibility.Collapsed;
            PageBody.Visibility = Visibility.Visible;
            return;
        }

        if (!_lifecycle.CanAccessProtectedData)
        {
            return;
        }

        ShowJournalContent();
        EnsureJournalInitialPeriod();
        await LoadJournalAsync(reset: true);
    }

    private void OnJournalProtectedAccessChanged(bool allowed)
    {
        Interlocked.Increment(ref _journalGeneration);
        if (!allowed)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                ClearJournalUi();
            }
            else
            {
                DispatcherQueue.TryEnqueue(ClearJournalUi);
            }
            return;
        }

        void RefreshIfSelected()
        {
            if (ReferenceEquals(ShellNavigation.SelectedItem, JournalItem))
            {
                ShowJournalContent();
                EnsureJournalInitialPeriod();
                _ = LoadJournalAsync(reset: true);
            }
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshIfSelected();
        }
        else
        {
            DispatcherQueue.TryEnqueue(RefreshIfSelected);
        }
    }

    private void OnJournalWindowClosed(object sender, WindowEventArgs e)
    {
        ShellNavigation.SelectionChanged -= OnJournalNavigationSelectionChanged;
        _lifecycle.ProtectedDataAccessChanged -= OnJournalProtectedAccessChanged;
        Closed -= OnJournalWindowClosed;
        Interlocked.Increment(ref _journalGeneration);
        ClearJournalUi();
    }

    private async void OnJournalLoadClicked(object sender, RoutedEventArgs e)
    {
        await LoadJournalAsync(reset: true);
    }

    private async void OnJournalLoadMoreClicked(object sender, RoutedEventArgs e)
    {
        await LoadJournalAsync(reset: false);
    }

    private void OnJournalPeriodChanged(
        CalendarDatePicker sender,
        CalendarDatePickerDateChangedEventArgs args)
    {
        if (_journalInitializingPeriod)
        {
            return;
        }

        Interlocked.Increment(ref _journalGeneration);
        _journalItems.Clear();
        _journalPeriod = null;
        _journalCursor = null;
        JournalEntriesList.ItemsSource = null;
        JournalLoadMoreButton.Visibility = Visibility.Collapsed;
        JournalInfo.Severity = InfoBarSeverity.Informational;
        JournalInfo.Message = JournalText("PeriodChanged");
        JournalInfo.IsOpen = true;
    }

    private void EnsureJournalInitialPeriod()
    {
        if (JournalStartDate.Date is not null && JournalEndDate.Date is not null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Date, now.Offset);
        _journalInitializingPeriod = true;
        try
        {
            JournalStartDate.Date ??= today;
            JournalEndDate.Date ??= today;
        }
        finally
        {
            _journalInitializingPeriod = false;
        }
    }

    private void ShowJournalContent()
    {
        PageBody.Visibility = Visibility.Collapsed;
        JournalContentPanel.Visibility = Visibility.Visible;
    }

    private async Task LoadJournalAsync(bool reset)
    {
        if (!TryGetJournalPeriod(out JournalDateRange period))
        {
            JournalInfo.Severity = InfoBarSeverity.Error;
            JournalInfo.Message = JournalText("InvalidPeriod");
            JournalInfo.IsOpen = true;
            return;
        }

        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            ClearJournalUi();
            return;
        }

        ClipboardHistoryCursor? before = null;
        if (!reset)
        {
            if (_journalPeriod is not JournalDateRange currentPeriod ||
                currentPeriod != period ||
                _journalCursor is null)
            {
                return;
            }
            before = _journalCursor;
        }

        long generation = Interlocked.Increment(ref _journalGeneration);
        if (reset)
        {
            _journalItems.Clear();
            _journalPeriod = period;
            _journalCursor = null;
            JournalEntriesList.ItemsSource = null;
        }

        JournalInfo.IsOpen = false;
        SetJournalBusy(true);
        try
        {
            IReadOnlyList<UnifiedClipboardHistoryEntry> entries = await Task.Run(
                async () =>
                {
                    var repository = new ProtectedUnifiedClipboardHistoryRepository(session);
                    return before is null
                        ? await repository.ReadAsync(
                            period,
                            JournalPageSize,
                            session.CancellationToken)
                        : await repository.ReadBeforeAsync(
                            period,
                            JournalPageSize,
                            before,
                            session.CancellationToken);
                },
                session.CancellationToken);

            if (!IsCurrentJournalOperation(session, generation) ||
                _journalPeriod != period ||
                (!reset && !Equals(_journalCursor, before)))
            {
                return;
            }

            foreach (UnifiedClipboardHistoryEntry entry in entries)
            {
                _journalItems.Add(BuildJournalListItem(entry));
            }

            if (entries.Count > 0)
            {
                _journalCursor = ClipboardHistoryCursor.FromEntry(period, entries[^1].Entry);
            }

            JournalEntriesList.ItemsSource = _journalItems.ToArray();
            JournalLoadMoreButton.Visibility = entries.Count == JournalPageSize
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (reset && entries.Count == 0)
            {
                JournalInfo.Severity = InfoBarSeverity.Informational;
                JournalInfo.Message = JournalText("Empty");
                JournalInfo.IsOpen = true;
            }
            else if (!reset && entries.Count == 0)
            {
                JournalInfo.Severity = InfoBarSeverity.Informational;
                JournalInfo.Message = JournalText("End");
                JournalInfo.IsOpen = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (IsCurrentJournalOperation(session, generation))
            {
                JournalInfo.Severity = InfoBarSeverity.Error;
                JournalInfo.Message = JournalText("LoadFailed");
                JournalInfo.IsOpen = true;
            }
        }
        finally
        {
            if (IsCurrentJournalOperation(session, generation))
            {
                SetJournalBusy(false);
            }
        }
    }

    private bool TryGetJournalPeriod(out JournalDateRange period)
    {
        period = default;
        if (JournalStartDate.Date is not DateTimeOffset start ||
            JournalEndDate.Date is not DateTimeOffset end)
        {
            return false;
        }

        DateOnly startDate = DateOnly.FromDateTime(start.DateTime);
        DateOnly endDate = DateOnly.FromDateTime(end.DateTime);
        if (endDate < startDate)
        {
            return false;
        }

        period = new JournalDateRange(startDate, endDate);
        return true;
    }

    private bool IsCurrentJournalOperation(ProtectedStorageSessionLease session, long generation)
    {
        return generation == Volatile.Read(ref _journalGeneration) &&
            ReferenceEquals(_protectedStorageSession, session) &&
            session.IsActive &&
            _lifecycle.CanAccessProtectedData &&
            ReferenceEquals(ShellNavigation.SelectedItem, JournalItem);
    }

    private void SetJournalBusy(bool busy)
    {
        JournalProgress.IsActive = busy;
        JournalProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        JournalLoadButton.IsEnabled = !busy;
        JournalStartDate.IsEnabled = !busy;
        JournalEndDate.IsEnabled = !busy;
        JournalLoadMoreButton.IsEnabled = !busy;
    }

    private void ClearJournalUi()
    {
        _journalItems.Clear();
        _journalPeriod = null;
        _journalCursor = null;
        JournalEntriesList.ItemsSource = null;
        JournalInfo.IsOpen = false;
        JournalLoadMoreButton.Visibility = Visibility.Collapsed;
        SetJournalBusy(false);
    }

    private JournalListItem BuildJournalListItem(UnifiedClipboardHistoryEntry unified)
    {
        ClipboardHistoryEntry entry = unified.Entry;
        return new JournalListItem(
            entry.EventTime.Timestamp.ToString("dd.MM.yyyy HH:mm:ss zzz", CultureInfo.CurrentCulture),
            BuildJournalSource(entry),
            BuildJournalPreview(entry),
            BuildJournalDetails(unified));
    }

    private string BuildJournalSource(ClipboardHistoryEntry entry)
    {
        if (entry.SourceApplication is { } source)
        {
            if (!string.IsNullOrWhiteSpace(source.ExecutablePath))
            {
                string fileName = Path.GetFileName(source.ExecutablePath);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    return fileName;
                }
            }

            if (!string.IsNullOrWhiteSpace(source.ApplicationUserModelId))
            {
                return source.ApplicationUserModelId;
            }
        }

        return JournalText("UnknownSource");
    }

    private string BuildJournalPreview(ClipboardHistoryEntry entry)
    {
        foreach (ClipboardHistoryPayload payload in entry.Payloads.OrderBy(item => item.PayloadOrder))
        {
            string? candidate = payload.SearchText;
            if (string.IsNullOrWhiteSpace(candidate) &&
                payload.Kind == ClipboardHistoryPayloadKind.Link)
            {
                candidate = payload.InlineCanonicalText;
            }

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                string normalized = NormalizeJournalPreview(candidate);
                if (normalized.Length > JournalPreviewLength)
                {
                    return normalized[..JournalPreviewLength] + "…";
                }
                return normalized;
            }
        }

        return JournalText("NoPreview");
    }

    private string BuildJournalDetails(UnifiedClipboardHistoryEntry unified)
    {
        string formats = string.Join(
            ", ",
            unified.Entry.Payloads
                .OrderBy(item => item.PayloadOrder)
                .Select(item => item.FormatName)
                .Distinct(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(formats))
        {
            formats = JournalText("NoFormats");
        }

        string locations = string.Join(
            ", ",
            unified.Locations.Select(location => location.Kind switch
            {
                ClipboardHistoryPhysicalLocationKind.Current => JournalText("LocationCurrent"),
                ClipboardHistoryPhysicalLocationKind.Archive => string.Format(
                    CultureInfo.CurrentCulture,
                    JournalText("LocationArchive"),
                    location.FileName),
                _ => location.Kind.ToString(),
            }));

        return string.Format(
            CultureInfo.CurrentCulture,
            JournalText("Details"),
            formats,
            locations);
    }

    private static string NormalizeJournalPreview(string value)
    {
        var output = new StringBuilder(value.Length);
        bool pendingWhitespace = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = output.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                output.Append(' ');
                pendingWhitespace = false;
            }
            output.Append(character);
        }

        return output.ToString();
    }

    private string JournalText(string suffix) => _localization.GetString($"Journal.{suffix}");

    private sealed record JournalListItem(
        string TimestampText,
        string SourceText,
        string PreviewText,
        string DetailsText);
}
