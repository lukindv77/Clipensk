using System.Globalization;
using System.Text;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
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
    private string? _journalSearchTerm;
    private ClipboardHistoryFilter? _journalFilter;
    private ClipboardHistoryCursor? _journalCursor;

    private void OnJournalContentPanelLoaded(object sender, RoutedEventArgs e)
    {
        JournalStartDate.Header = JournalText("StartDate");
        JournalEndDate.Header = JournalText("EndDate");
        JournalSearchBox.PlaceholderText = JournalText("SearchPlaceholder");
        JournalSearchBox.Header = JournalText("Search");
        JournalLoadButton.Content = JournalText("Load");
        JournalLoadMoreButton.Content = JournalText("LoadMore");
        JournalCopyButton.Content = JournalText("Copy.Action");
        JournalCopyPlainTextButton.Content = JournalText("Copy.PlainTextAction");
        JournalUnassignedButton.Content = JournalText("Unassigned.Open");
        UpdateJournalGroupMembersButton();

        ShellNavigation.SelectionChanged -= OnJournalNavigationSelectionChanged;
        ShellNavigation.SelectionChanged += OnJournalNavigationSelectionChanged;
        _lifecycle.ProtectedDataAccessChanged -= OnJournalProtectedAccessChanged;
        _lifecycle.ProtectedDataAccessChanged += OnJournalProtectedAccessChanged;
        Closed -= OnJournalContentWindowClosed;
        Closed += OnJournalContentWindowClosed;

        EnsureJournalInitialPeriod();
        _ = EnsureJournalApplicationFilterLoadedAsync();
        _ = RefreshJournalGroupFilterAsync();
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
        _ = EnsureJournalApplicationFilterLoadedAsync();
        await RefreshJournalGroupFilterAsync();
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
                _ = EnsureJournalApplicationFilterLoadedAsync();
                _ = RefreshJournalGroupFilterAsync();
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

    private void OnJournalContentWindowClosed(object sender, WindowEventArgs e)
    {
        ShellNavigation.SelectionChanged -= OnJournalNavigationSelectionChanged;
        _lifecycle.ProtectedDataAccessChanged -= OnJournalProtectedAccessChanged;
        Closed -= OnJournalContentWindowClosed;
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

        ResetJournalForPendingQueryChange("PeriodChanged");
    }

    private void OnJournalSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_journalInitializingPeriod)
        {
            return;
        }

        ResetJournalForPendingQueryChange("FilterChanged");
    }

    /// <summary>
    /// The period and the search term are both committed only when the user clicks "Показать",
    /// exactly like the pre-existing period behavior: this avoids re-querying on every keystroke
    /// or date click while still making it obvious that displayed results are stale.
    /// </summary>
    private void ResetJournalForPendingQueryChange(string messageKey)
    {
        // The change also abandons a read still in flight, whose completion no longer clears the
        // busy state once the generation moved on.
        Interlocked.Increment(ref _journalGeneration);
        SetJournalBusy(false);
        _journalItems.Clear();
        _journalPeriod = null;
        _journalCursor = null;
        JournalEntriesList.ItemsSource = null;
        UpdateJournalCopyAvailability();
        JournalLoadMoreButton.Visibility = Visibility.Collapsed;
        JournalInfo.Severity = InfoBarSeverity.Informational;
        JournalInfo.Message = JournalText(messageKey);
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

        // A configured default spans the requested number of calendar days ending today, per
        // `docs/REQUIREMENTS.md` §8. Without one, the journal keeps its existing single-day default.
        DateTimeOffset start = today;
        if (_settings.DefaultJournalPeriodDays is int days)
        {
            (DateOnly startDate, DateOnly _) = DefaultJournalPeriod.ForDays(
                days,
                DateOnly.FromDateTime(now.DateTime));
            start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), now.Offset);
        }

        _journalInitializingPeriod = true;
        try
        {
            JournalStartDate.Date ??= start;
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

        string? searchTerm = ClipboardHistorySearchMatcher.Normalize(JournalSearchBox.Text);
        Guid? sourceApplicationId = CurrentJournalApplicationFilter();
        JournalGroupFilterItem? groupFilter = CurrentJournalGroupFilter();
        Guid[] excludedMembers = _journalExcludedGroupMembers.ToArray();

        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            ClearJournalUi();
            return;
        }

        // Every change of the source filter goes through ResetJournalForPendingQueryChange, which
        // clears the committed period, so "load more" always continues with the filter the first
        // page resolved; group membership is resolved once per query, in Current, before reading.
        ClipboardHistoryCursor? before = null;
        ClipboardHistoryFilter? committedFilter = null;
        if (!reset)
        {
            if (_journalPeriod is not JournalDateRange currentPeriod ||
                currentPeriod != period ||
                _journalSearchTerm != searchTerm ||
                _journalCursor is null)
            {
                return;
            }
            before = _journalCursor;
            committedFilter = _journalFilter;
        }

        long generation = Interlocked.Increment(ref _journalGeneration);
        if (reset)
        {
            _journalItems.Clear();
            _journalPeriod = period;
            _journalSearchTerm = searchTerm;
            _journalFilter = null;
            _journalCursor = null;
            JournalEntriesList.ItemsSource = null;
        }

        JournalInfo.IsOpen = false;
        SetJournalBusy(true);
        try
        {
            JournalPage page = await Task.Run(
                async () =>
                {
                    CancellationToken token = session.CancellationToken;
                    bool resolveGroup = reset && groupFilter is { Kind: not JournalGroupFilterKind.All };
                    ApplicationGroupDirectory? groups = await ReadJournalGroupsAsync(
                        session,
                        required: resolveGroup,
                        token).ConfigureAwait(false);
                    IReadOnlyList<ApplicationIdentitySummary>? identities =
                        resolveGroup && groupFilter!.Kind == JournalGroupFilterKind.Default
                            ? await new SqliteApplicationIdentityRepository(session)
                                .ListAsync(token)
                                .ConfigureAwait(false)
                            : null;
                    JournalSourceFilter source = reset
                        ? ResolveJournalSourceFilter(sourceApplicationId, groupFilter, excludedMembers, groups, identities)
                        : new JournalSourceFilter(committedFilter, GroupMissing: false);
                    if (source.GroupMissing)
                    {
                        return new JournalPage([], groups, source);
                    }

                    var repository = new ProtectedUnifiedClipboardHistoryRepository(session);
                    IReadOnlyList<UnifiedClipboardHistoryEntry> read = before is null
                        ? await repository.ReadAsync(
                            period,
                            JournalPageSize,
                            searchTerm,
                            source.Filter,
                            token)
                        : await repository.ReadBeforeAsync(
                            period,
                            JournalPageSize,
                            before,
                            searchTerm,
                            source.Filter,
                            token);
                    return new JournalPage(read, groups, source);
                },
                session.CancellationToken);

            if (!IsCurrentJournalOperation(session, generation) ||
                _journalPeriod != period ||
                _journalSearchTerm != searchTerm ||
                (!reset && !Equals(_journalCursor, before)))
            {
                return;
            }

            if (page.Source.GroupMissing)
            {
                ResetJournalGroupFilterAfterGroupRemoved();
                return;
            }

            IReadOnlyList<UnifiedClipboardHistoryEntry> entries = page.Entries;
            if (reset)
            {
                _journalFilter = page.Source.Filter;
            }

            foreach (UnifiedClipboardHistoryEntry entry in entries)
            {
                _journalItems.Add(BuildJournalListItem(entry, page.Groups));
            }

            if (entries.Count > 0)
            {
                _journalCursor = ClipboardHistoryCursor.FromEntry(period, entries[^1].Entry);
            }

            JournalEntriesList.ItemsSource = _journalItems.ToArray();
            UpdateJournalCopyAvailability();
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
        UpdateJournalCopyAvailability(busy);
    }

    private void ClearJournalUi()
    {
        _journalItems.Clear();
        _journalPeriod = null;
        _journalSearchTerm = null;
        _journalFilter = null;
        _journalCursor = null;
        JournalEntriesList.ItemsSource = null;
        UpdateJournalCopyAvailability();
        JournalInfo.IsOpen = false;
        JournalLoadMoreButton.Visibility = Visibility.Collapsed;
        SetJournalBusy(false);
    }

    private JournalListItem BuildJournalListItem(
        UnifiedClipboardHistoryEntry unified,
        ApplicationGroupDirectory? groups)
    {
        ClipboardHistoryEntry entry = unified.Entry;
        return new JournalListItem(
            entry.EventTime.Timestamp.ToString("dd.MM.yyyy HH:mm:ss zzz", CultureInfo.CurrentCulture),
            BuildJournalSourceWithGroup(entry, groups),
            BuildJournalPreview(entry),
            BuildJournalDetails(unified),
            entry);
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
        if (GetPlainTextRepresentation(entry) is not { } candidate)
        {
            return JournalText("NoPreview");
        }

        string normalized = NormalizeJournalPreview(candidate);
        return normalized.Length > JournalPreviewLength
            ? normalized[..JournalPreviewLength] + "…"
            : normalized;
    }

    /// <summary>
    /// The same plain-text candidate <see cref="ClipboardRestorePlanFactory.CreatePlainTextOnly"/>
    /// would choose to publish: the first payload in stored order with a non-empty
    /// <see cref="ClipboardHistoryPayload.SearchText"/>, falling back to the raw URL for a Link
    /// payload. Shared with <see cref="BuildJournalPreview"/> and
    /// <see cref="UpdateJournalCopyAvailability"/> so the preview and the "paste as plain text"
    /// button availability never disagree about whether an entry has one.
    /// </summary>
    private static string? GetPlainTextRepresentation(ClipboardHistoryEntry entry)
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
                return candidate;
            }
        }

        return null;
    }

    private string BuildJournalDetails(UnifiedClipboardHistoryEntry unified)
    {
        string formats = string.Join(
            ", ",
            unified.Entry.Payloads
                .OrderBy(item => item.PayloadOrder)
                .Select(item => item.FormatName)
                .Distinct(StringComparer.Ordinal)
                .Select(JournalFormatLabel));
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

    /// <summary>A standard format by its name in the capture rules, a custom one by its own name.</summary>
    private string JournalFormatLabel(string formatName)
    {
        foreach ((string name, string label) in StandardPolicyFormats())
        {
            if (string.Equals(name, formatName, StringComparison.Ordinal))
            {
                return label;
            }
        }

        return formatName;
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
        string DetailsText,
        ClipboardHistoryEntry Entry);
}
