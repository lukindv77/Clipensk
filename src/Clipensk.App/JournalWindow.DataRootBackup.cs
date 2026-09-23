using Clipensk.Core.Security;
using Clipensk.Infrastructure.Settings;
using Clipensk.Infrastructure.Storage;
using Clipensk.Windows.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

/// <summary>
/// "Create a backup…" and "Open a storage from a folder…" in Settings, per
/// <c>docs/BACKUP_PROTOCOL.md</c>. Both run with Clipensk locked, like the relocation, and share its
/// in-progress flag, so no two operations on the data root overlap.
/// </summary>
public sealed partial class JournalWindow
{
    private void UpdateDataRootBackupButtons()
    {
        bool available =
            !_dataRootRelocationInProgress &&
            _lifecycle.IsDataRootConfigured &&
            !string.IsNullOrWhiteSpace(_settings.DataRootPath);

        BackupDataRootButton.Content = BackupText("Start");
        BackupDataRootButton.IsEnabled = available && _credentialState != ProtectedStorageCredentialState.Invalid;

        // Opening another storage is also the way out of one that cannot be used.
        OpenDataRootButton.Content = OpenDataRootText("Start");
        OpenDataRootButton.IsEnabled = available;
    }

    private async void OnBackupDataRootClicked(object sender, RoutedEventArgs e)
    {
        if (_dataRootRelocationInProgress || string.IsNullOrWhiteSpace(_settings.DataRootPath))
        {
            return;
        }

        _dataRootRelocationInProgress = true;
        UpdateDataRootRelocationButton();
        DataRootRelocationInfo.IsOpen = false;
        try
        {
            string? picked = await PickRelocationFolderAsync();
            if (picked is null)
            {
                return;
            }

            var service = new DataRootBackupService(new SettingsDataRootLocationStore(_settingsStore));
            DataRootBackupPreview preview;
            try
            {
                preview = await service.InspectAsync(picked);
            }
            catch (DataRootRelocationRefusedException refused)
            {
                ShowRelocationMessage(DataRootRelocationInfo, BackupRefusalText(refused.Reason, picked), InfoBarSeverity.Error);
                return;
            }

            if (!await ConfirmBackupAsync(preview))
            {
                return;
            }

            // The copy runs locked, with no protected session and capture stopped (BACKUP_PROTOCOL.md §3).
            if (_lifecycle.CanAccessProtectedData && !TryLockNow())
            {
                ShowRelocationMessage(DataRootRelocationInfo, BackupText("LockFailed"), InfoBarSeverity.Error);
                return;
            }

            RefreshLifecycleUi();
            (string message, InfoBarSeverity severity) = await RunBackupAsync(service, preview);
            ShowRelocationMessage(LockInfo, message, severity);
        }
        catch
        {
            // Once Clipensk has locked itself the lock screen is shown, not Settings.
            InfoBar bar = SettingsPanel.Visibility == Visibility.Visible ? DataRootRelocationInfo : LockInfo;
            ShowRelocationMessage(bar, BackupText("Failed"), InfoBarSeverity.Error);
        }
        finally
        {
            _dataRootRelocationInProgress = false;
            UpdateDataRootRelocationButton();
        }
    }

    private async Task<bool> ConfirmBackupAsync(DataRootBackupPreview preview)
    {
        var dialog = new ContentDialog
        {
            Title = BackupText("ConfirmTitle"),
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = FillText(
                        BackupText("ConfirmBody"),
                        preview.SourcePath,
                        preview.BackupPath,
                        preview.FileCount,
                        FormatRelocationBytes(preview.ByteCount),
                        preview.AvailableBytes is long available
                            ? FormatRelocationBytes(available)
                            : RelocationText("SpaceUnknown")),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
                MaxHeight = 480,
            },
            PrimaryButtonText = BackupText("Confirm"),
            CloseButtonText = RelocationText("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = ShellNavigation.XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Runs the copy behind a progress dialog that cannot be dismissed: its only button cancels the
    /// copy, and disappears once every file is copied, when verification begins.
    /// </summary>
    private async Task<(string Message, InfoBarSeverity Severity)> RunBackupAsync(
        DataRootBackupService service,
        DataRootBackupPreview preview)
    {
        using var cancellation = new CancellationTokenSource();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = RelocationText("Preparing") };
        var bar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 1 };
        var content = new StackPanel { Spacing = 12, MinWidth = 360 };
        content.Children.Add(bar);
        content.Children.Add(status);

        bool finished = false;
        var dialog = new ContentDialog
        {
            Title = BackupText("ProgressTitle"),
            Content = content,
            CloseButtonText = RelocationText("Cancel"),
            XamlRoot = ShellNavigation.XamlRoot,
        };
        dialog.CloseButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (!cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
                status.Text = RelocationText("Cancelling");
            }
        };
        dialog.Closing += (_, args) =>
        {
            if (!finished)
            {
                args.Cancel = true;
            }
        };

        var progress = new Progress<DataRootRelocationProgress>(update =>
        {
            if (finished || cancellation.IsCancellationRequested)
            {
                return;
            }

            bar.IsIndeterminate = false;
            bar.Value = update.ByteCount == 0 ? 1 : (double)update.BytesCopied / update.ByteCount;
            if (update.FilesCopied == update.FileCount)
            {
                status.Text = BackupText("Verifying");
                dialog.CloseButtonText = string.Empty;
                return;
            }

            status.Text = FillText(
                RelocationText("Progress"),
                update.FilesCopied,
                update.FileCount,
                FormatRelocationBytes(update.BytesCopied),
                FormatRelocationBytes(update.ByteCount));
        });

        DataRootProtectionResult? protection = null;
        Task<ContentDialogResult> shown = ShowRelocationDialogAsync(dialog);
        try
        {
            DataRootBackupResult result = await Task.Run(
                () => BackUpWhenSourceIsFreeAsync(
                    service,
                    preview.BackupPath,
                    path => protection = WindowsDataRootProtectionService.Protect(path),
                    progress,
                    cancellation.Token));

            string message = FillText(
                BackupText("Completed"),
                result.BackupPath,
                result.FileCount,
                FormatRelocationBytes(result.ByteCount));
            if (protection is DataRootProtectionResult.Unsupported)
            {
                message += " " + BackupText("ProtectionUnsupported");
            }

            return (message, InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            return (DescribeUnfinishedBackup("Cancelled", preview), InfoBarSeverity.Informational);
        }
        catch (DataRootRelocationRefusedException refused)
        {
            return (BackupRefusalText(refused.Reason, preview.BackupPath), InfoBarSeverity.Error);
        }
        catch
        {
            return (DescribeUnfinishedBackup("Failed", preview), InfoBarSeverity.Error);
        }
        finally
        {
            finished = true;
            dialog.Hide();
            await shown;
        }
    }

    /// <summary>
    /// Right after the lock an operation of the closed session may still hold a file for a moment;
    /// the exclusive open then refuses before anything is written, and the copy is tried again.
    /// </summary>
    private static async Task<DataRootBackupResult> BackUpWhenSourceIsFreeAsync(
        DataRootBackupService service,
        string backupPath,
        Action<string> protectTarget,
        IProgress<DataRootRelocationProgress> progress,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await service.CreateAsync(backupPath, protectTarget, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DataRootRelocationRefusedException refused) when (
                refused.Reason == DataRootRelocationRefusal.SourceBusy &&
                attempt < RelocationBusyAttempts)
            {
                await Task.Delay(RelocationBusyDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>An unfinished copy is removed at once; if that failed, its folder is named.</summary>
    private string DescribeUnfinishedBackup(string key, DataRootBackupPreview preview)
    {
        string incomplete = preview.BackupPath + DataRootBackupService.IncompleteSuffix;
        return Directory.Exists(incomplete)
            ? FillText(BackupText(key + "Leftover"), incomplete)
            : BackupText(key);
    }

    private async void OnOpenDataRootClicked(object sender, RoutedEventArgs e)
    {
        if (_dataRootRelocationInProgress)
        {
            return;
        }

        _dataRootRelocationInProgress = true;
        UpdateDataRootRelocationButton();
        DataRootRelocationInfo.IsOpen = false;
        string chosen = string.Empty;
        try
        {
            string? picked = await PickRelocationFolderAsync();
            if (picked is null)
            {
                return;
            }

            chosen = picked;

            var service = new DataRootOpenService(new SettingsDataRootLocationStore(_settingsStore));
            DataRootOpenPreview preview;
            try
            {
                preview = await service.InspectAsync(picked);
            }
            catch (DataRootRelocationRefusedException refused)
            {
                ShowRelocationMessage(DataRootRelocationInfo, OpenDataRootRefusalText(refused.Reason, picked), InfoBarSeverity.Error);
                return;
            }

            // Readable database headers and no metadata file of the old format (CRYPTOGRAPHY.md §3).
            ProtectedStorageCredentialState state;
            try
            {
                state = await _credentialService.GetStateAsync(preview.NewPath);
            }
            catch
            {
                state = ProtectedStorageCredentialState.Invalid;
            }

            if (state != ProtectedStorageCredentialState.Ready)
            {
                ShowRelocationMessage(
                    DataRootRelocationInfo,
                    FillText(OpenDataRootText("Refused.Unreadable"), preview.NewPath),
                    InfoBarSeverity.Error);
                return;
            }

            if (!await ConfirmOpenDataRootAsync(preview))
            {
                return;
            }

            if (_lifecycle.CanAccessProtectedData && !TryLockNow())
            {
                ShowRelocationMessage(DataRootRelocationInfo, OpenDataRootText("LockFailed"), InfoBarSeverity.Error);
                return;
            }

            RefreshLifecycleUi();
            DataRootProtectionResult protection = WindowsDataRootProtectionService.Protect(preview.NewPath);
            await ValidateDataRootAsync(preview.NewPath);
            string opened = await service.OpenAsync(preview.NewPath);
            await ReloadSettingsAfterRelocationAsync();

            string message = FillText(OpenDataRootText("Completed"), opened, preview.CurrentPath ?? string.Empty);
            InfoBarSeverity severity = InfoBarSeverity.Success;
            if (protection is not DataRootProtectionResult.Protected)
            {
                message += " " + OpenDataRootText("ProtectionSkipped");
                severity = InfoBarSeverity.Warning;
            }
            if (_credentialState == ProtectedStorageCredentialState.Invalid)
            {
                message += Environment.NewLine + _localization.GetString("Lock.InvalidMetadata");
                severity = InfoBarSeverity.Error;
            }

            ShowRelocationMessage(LockInfo, message, severity);
        }
        catch (DataRootRelocationRefusedException refused)
        {
            await ReloadSettingsAfterRelocationAsync();
            InfoBar bar = SettingsPanel.Visibility == Visibility.Visible ? DataRootRelocationInfo : LockInfo;
            ShowRelocationMessage(bar, OpenDataRootRefusalText(refused.Reason, chosen), InfoBarSeverity.Error);
        }
        catch
        {
            await ReloadSettingsAfterRelocationAsync();
            InfoBar bar = SettingsPanel.Visibility == Visibility.Visible ? DataRootRelocationInfo : LockInfo;
            ShowRelocationMessage(bar, OpenDataRootText("Failed"), InfoBarSeverity.Error);
        }
        finally
        {
            _dataRootRelocationInProgress = false;
            UpdateDataRootRelocationButton();
        }
    }

    private async Task<bool> ConfirmOpenDataRootAsync(DataRootOpenPreview preview)
    {
        var dialog = new ContentDialog
        {
            Title = OpenDataRootText("ConfirmTitle"),
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = FillText(
                        OpenDataRootText("ConfirmBody"),
                        preview.CurrentPath ?? _localization.GetString("Settings.DataRoot.NotConfigured"),
                        preview.NewPath),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
                MaxHeight = 480,
            },
            PrimaryButtonText = OpenDataRootText("Confirm"),
            CloseButtonText = RelocationText("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = ShellNavigation.XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private string BackupRefusalText(DataRootRelocationRefusal reason, string path) =>
        FillText(BackupText("Refused." + reason), path);

    private string OpenDataRootRefusalText(DataRootRelocationRefusal reason, string path) =>
        FillText(OpenDataRootText("Refused." + reason), path);

    private string BackupText(string key) => _localization.GetString("Backup." + key);

    private string OpenDataRootText(string key) => _localization.GetString("OpenDataRoot." + key);
}
