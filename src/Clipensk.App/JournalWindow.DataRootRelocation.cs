using System.Globalization;
using Clipensk.Core.Security;
using Clipensk.Core.Settings;
using Clipensk.Infrastructure.Settings;
using Clipensk.Infrastructure.Storage;
using Clipensk.Windows.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Clipensk.App;

/// <summary>
/// Moving the data root to another folder from Settings, per
/// <c>docs/DATA_ROOT_RELOCATION_PROTOCOL.md</c>: the user picks a folder, sees what will move and
/// confirms; Clipensk locks itself (decision D2), copies and verifies the whole storage, switches the
/// path and deletes the old folder; cancellation is possible only before the switch (D3).
/// </summary>
public sealed partial class JournalWindow
{
    private const int RelocationBusyAttempts = 6;
    private const string RelocationSubfolderName = "Clipensk";
    private static readonly TimeSpan RelocationBusyDelay = TimeSpan.FromMilliseconds(500);

    private bool _dataRootRelocationInProgress;

    /// <summary>Reports on the lock screen how startup finished or undid an interrupted relocation.</summary>
    internal void ShowStartupNotice(string message, InfoBarSeverity severity)
    {
        LockInfo.Severity = severity;
        LockInfo.Message = message;
        LockInfo.IsOpen = true;
    }

    private void UpdateDataRootRelocationButton()
    {
        RelocateDataRootButton.Content = RelocationText("Start");
        RelocateDataRootButton.IsEnabled =
            !_dataRootRelocationInProgress &&
            _lifecycle.IsDataRootConfigured &&
            !string.IsNullOrWhiteSpace(_settings.DataRootPath) &&
            _credentialState != ProtectedStorageCredentialState.Invalid;
        UpdateDataRootBackupButtons();
    }

    private async void OnRelocateDataRootClicked(object sender, RoutedEventArgs e)
    {
        if (_dataRootRelocationInProgress || string.IsNullOrWhiteSpace(_settings.DataRootPath))
        {
            return;
        }

        _dataRootRelocationInProgress = true;
        UpdateDataRootRelocationButton();
        DataRootRelocationInfo.IsOpen = false;
        string sourcePath = _settings.DataRootPath;
        try
        {
            string? picked = await PickRelocationFolderAsync();
            if (picked is null)
            {
                return;
            }

            string target = ChooseRelocationTarget(picked);
            var service = new DataRootRelocationService(new SettingsDataRootLocationStore(_settingsStore));
            DataRootRelocationPreview preview;
            try
            {
                preview = await service.InspectAsync(target);
            }
            catch (DataRootRelocationRefusedException refused)
            {
                ShowRelocationMessage(DataRootRelocationInfo, RefusalText(refused.Reason, target), InfoBarSeverity.Error);
                return;
            }

            if (!await ConfirmRelocationAsync(preview))
            {
                return;
            }

            // Decision D2: the move runs locked, with no protected session and capture stopped.
            if (_lifecycle.CanAccessProtectedData && !TryLockNow())
            {
                ShowRelocationMessage(DataRootRelocationInfo, RelocationText("LockFailed"), InfoBarSeverity.Error);
                return;
            }

            RefreshLifecycleUi();
            (string message, InfoBarSeverity severity) = await RunRelocationAsync(service, preview, sourcePath);
            await ReloadSettingsAfterRelocationAsync();
            if (_credentialState == ProtectedStorageCredentialState.Invalid)
            {
                // Reloading found the storage unusable; that must not be hidden behind the result.
                message += Environment.NewLine + _localization.GetString("Lock.InvalidMetadata");
                severity = InfoBarSeverity.Error;
            }

            ShowRelocationMessage(LockInfo, message, severity);
        }
        catch
        {
            await ReloadSettingsAfterRelocationAsync();

            // Once Clipensk has locked itself the lock screen is shown, not Settings.
            InfoBar bar = SettingsPanel.Visibility == Visibility.Visible ? DataRootRelocationInfo : LockInfo;
            ShowRelocationMessage(bar, await DescribeUnfinishedRelocationAsync("Failed"), InfoBarSeverity.Error);
        }
        finally
        {
            _dataRootRelocationInProgress = false;
            UpdateDataRootRelocationButton();
        }
    }

    private async Task<string?> PickRelocationFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");

        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        _systemPickerOpen = true;
        try
        {
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        finally
        {
            _systemPickerOpen = false;
        }
    }

    /// <summary>
    /// An empty picked folder becomes the new data root itself; otherwise the storage goes into a new
    /// <c>Clipensk</c> folder inside it, so a relocation never mixes the storage with other files.
    /// </summary>
    private static string ChooseRelocationTarget(string picked)
    {
        string folder = DataRootPaths.Normalize(picked);
        return Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()
            ? folder
            : Path.Combine(folder, RelocationSubfolderName);
    }

    private async Task<bool> ConfirmRelocationAsync(DataRootRelocationPreview preview)
    {
        var dialog = new ContentDialog
        {
            Title = RelocationText("ConfirmTitle"),
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = FillText(
                        RelocationText("ConfirmBody"),
                        preview.SourcePath,
                        preview.TargetPath,
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
            PrimaryButtonText = RelocationText("Confirm"),
            CloseButtonText = RelocationText("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = ShellNavigation.XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Runs the move behind a progress dialog that cannot be dismissed: its only button cancels the
    /// copy, and disappears once every file is copied, when verification and the switch begin.
    /// </summary>
    private async Task<(string Message, InfoBarSeverity Severity)> RunRelocationAsync(
        DataRootRelocationService service,
        DataRootRelocationPreview preview,
        string sourcePath)
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
            Title = RelocationText("ProgressTitle"),
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
                status.Text = RelocationText("Verifying");
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
            DataRootRelocationResult result = await Task.Run(
                () => RelocateWhenSourceIsFreeAsync(
                    service,
                    preview.TargetPath,
                    path => protection = WindowsDataRootProtectionService.Protect(path),
                    progress,
                    cancellation.Token));

            string message = result.SourceRemoval.Retained.Count == 0
                ? FillText(RelocationText("Completed"), result.DataRootPath)
                : FillText(
                    RelocationText(result.SourceRemoval.RetryPending ? "CompletedRemovalPending" : "CompletedWithRemnants"),
                    result.DataRootPath,
                    sourcePath,
                    result.SourceRemoval.Retained.Count);
            if (protection is DataRootProtectionResult.Unsupported)
            {
                message += " " + RelocationText("ProtectionUnsupported");
            }

            return (message, result.SourceRemoval.Retained.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            return (await DescribeUnfinishedRelocationAsync("Cancelled"), InfoBarSeverity.Informational);
        }
        catch (DataRootRelocationRefusedException refused)
        {
            return (RefusalText(refused.Reason, preview.TargetPath), InfoBarSeverity.Error);
        }
        catch
        {
            return (await DescribeUnfinishedRelocationAsync("Failed"), InfoBarSeverity.Error);
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
    /// the exclusive open then refuses before anything is written, and the move is tried again.
    /// </summary>
    private static async Task<DataRootRelocationResult> RelocateWhenSourceIsFreeAsync(
        DataRootRelocationService service,
        string target,
        Action<string> protectTarget,
        IProgress<DataRootRelocationProgress> progress,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await service.RelocateAsync(target, protectTarget, progress, cancellationToken)
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

    /// <summary>
    /// After a failed or cancelled move the storage is still at the old place; if undoing the copy
    /// did not finish, the marker stays and the next start removes the copy.
    /// </summary>
    private async Task<string> DescribeUnfinishedRelocationAsync(string key)
    {
        try
        {
            ApplicationSettings settings = await _settingsStore.LoadAsync();
            if (settings.PendingDataRootRelocation is not null)
            {
                return RelocationText(key + "CleanupPending");
            }
        }
        catch
        {
        }

        return RelocationText(key);
    }

    /// <summary>
    /// Every later settings save starts from the in-memory settings, so they must name the data
    /// root the move left in the settings file, or the next save would restore the old path.
    /// </summary>
    private async Task ReloadSettingsAfterRelocationAsync()
    {
        try
        {
            _settings = await _settingsStore.LoadAsync();
        }
        catch
        {
            // The settings stay as they were in memory; the settings file is the source of truth at
            // the next start.
        }

        DataRootValue.Text = _settings.DataRootPath ?? _localization.GetString("Settings.DataRoot.NotConfigured");
        if (!string.IsNullOrWhiteSpace(_settings.DataRootPath))
        {
            try
            {
                _credentialState = await _credentialService.GetStateAsync(_settings.DataRootPath);
            }
            catch
            {
                _credentialState = ProtectedStorageCredentialState.Invalid;
            }
        }

        RefreshCredentialUi();
        RefreshLifecycleUi();
    }

    private static async Task<ContentDialogResult> ShowRelocationDialogAsync(ContentDialog dialog) =>
        await dialog.ShowAsync();

    private static void ShowRelocationMessage(InfoBar bar, string message, InfoBarSeverity severity)
    {
        bar.Severity = severity;
        bar.Message = message;
        bar.IsOpen = true;
    }

    private string RefusalText(DataRootRelocationRefusal reason, string target) =>
        FillText(RelocationText("Refused." + reason), target);

    private string RelocationText(string key) => _localization.GetString("Relocation." + key);

    private static string FormatRelocationBytes(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? bytes.ToString(CultureInfo.CurrentCulture) + " " + units[0]
            : value.ToString("0.#", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
