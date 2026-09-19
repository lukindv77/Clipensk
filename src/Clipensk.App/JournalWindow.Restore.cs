using Clipensk.Core.Clipboard;
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
        JournalCopyButton.IsEnabled =
            !busy &&
            _lifecycle.CanAccessProtectedData &&
            _protectedStorageSession?.IsActive == true &&
            JournalEntriesList.SelectedItem is JournalListItem;
    }

    private async void OnJournalCopyClicked(object sender, RoutedEventArgs e)
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
            ClipboardRestorePlan? plan = await app.TryRestoreToClipboardAsync(
                session,
                selected.Entry,
                session.CancellationToken);

            if (plan is null)
            {
                ShowJournalCopyMessage(InfoBarSeverity.Informational, JournalText("Copy.Locked"));
                return;
            }

            ShowJournalCopyMessage(InfoBarSeverity.Success, BuildJournalCopyMessage(plan));
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

    private void ShowJournalCopyMessage(InfoBarSeverity severity, string message)
    {
        JournalInfo.Severity = severity;
        JournalInfo.Message = message;
        JournalInfo.IsOpen = true;
    }
}
