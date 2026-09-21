using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void OnJournalSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateJournalCopyAvailability();

    private void UpdateJournalCopyAvailability(bool busy = false)
    {
        bool baseAvailable =
            !busy &&
            _lifecycle.CanAccessProtectedData &&
            _protectedStorageSession?.IsActive == true;
        JournalListItem? selected = JournalEntriesList.SelectedItem as JournalListItem;

        JournalCopyButton.IsEnabled = baseAvailable && selected is not null;

        // "Paste as plain text" needs an entry that actually has a plain-text representation to
        // publish — the same rule the preview column already uses, so the button and the preview
        // never disagree about whether one exists.
        JournalCopyPlainTextButton.IsEnabled = baseAvailable &&
            selected is not null &&
            GetPlainTextRepresentation(selected.Entry) is not null;
    }

    private async void OnJournalCopyClicked(object sender, RoutedEventArgs e) =>
        await RestoreSelectedEntryToClipboardAsync(
            (app, session, entry, token) => app.TryRestoreToClipboardAsync(session, entry, token),
            BuildJournalCopyMessage);

    private async void OnJournalCopyPlainTextClicked(object sender, RoutedEventArgs e) =>
        await RestoreSelectedEntryToClipboardAsync(
            (app, session, entry, token) => app.TryRestorePlainTextToClipboardAsync(session, entry, token),
            BuildJournalPlainTextCopyMessage);

    private async Task RestoreSelectedEntryToClipboardAsync(
        Func<App, ProtectedStorageSessionLease, ClipboardHistoryEntry, CancellationToken, Task<ClipboardRestorePlan?>> restoreAsync,
        Func<ClipboardRestorePlan, string> buildSuccessMessage)
    {
        if (JournalEntriesList.SelectedItem is not JournalListItem selected)
        {
            return;
        }

        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            ShowJournalCopyMessage(InfoBarSeverity.Informational, JournalText("Copy.Locked"));
            return;
        }

        if (Application.Current is not App app)
        {
            return;
        }

        UpdateJournalCopyAvailability(busy: true);
        try
        {
            ClipboardRestorePlan? plan = await restoreAsync(app, session, selected.Entry, session.CancellationToken);

            if (plan is null)
            {
                ShowJournalCopyMessage(InfoBarSeverity.Informational, JournalText("Copy.Locked"));
                return;
            }

            ShowJournalCopyMessage(InfoBarSeverity.Success, buildSuccessMessage(plan));
        }
        catch (OperationCanceledException)
        {
        }
        catch (FileNotFoundException)
        {
            // The external payload was physically collected into Trash after this page was loaded.
            ShowJournalCopyMessage(InfoBarSeverity.Error, JournalText("Copy.PayloadMissing"));
        }
        catch
        {
            ShowJournalCopyMessage(InfoBarSeverity.Error, JournalText("Copy.Failed"));
        }
        finally
        {
            UpdateJournalCopyAvailability();
        }
    }

    /// <summary>
    /// Says what actually reached the clipboard. A converted file drop and a skipped format are
    /// both visible outcomes the user cannot infer from the entry itself.
    /// </summary>
    private string BuildJournalCopyMessage(ClipboardRestorePlan plan)
    {
        string message = string.Format(
            _localization.GetString("Journal.Copy.Completed"),
            plan.Items.Count);

        if (plan.TextConvertedFormatNames.Count > 0)
        {
            message += " " + JournalText("Copy.ConvertedToText");
        }

        if (plan.SkippedFormatNames.Count > 0)
        {
            message += " " + string.Format(
                _localization.GetString("Journal.Copy.Skipped"),
                string.Join(", ", plan.SkippedFormatNames));
        }

        return message;
    }

    /// <summary>
    /// Says explicitly that only plain text reached the clipboard, so the user never mistakes this
    /// for a full restore that happened to lose formats — every other format is deliberately
    /// discarded by this mode, not skipped due to a storage decision.
    /// </summary>
    private string BuildJournalPlainTextCopyMessage(ClipboardRestorePlan plan)
    {
        string message = JournalText("Copy.PlainTextCompleted");

        if (plan.SkippedFormatNames.Count > 0)
        {
            message += " " + string.Format(
                _localization.GetString("Journal.Copy.Skipped"),
                string.Join(", ", plan.SkippedFormatNames));
        }

        return message;
    }

    private void ShowJournalCopyMessage(InfoBarSeverity severity, string message)
    {
        JournalInfo.Severity = severity;
        JournalInfo.Message = message;
        JournalInfo.IsOpen = true;
    }
}
