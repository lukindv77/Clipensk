using System.Security.Cryptography;
using Clipensk.Core.Security;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Clipensk.App;

/// <summary>
/// Changing the password from Settings (<c>docs/PASSWORD_CHANGE_PROTOCOL.md</c>): the current
/// password is checked against the session's key, Clipensk locks itself, and every database is
/// re-encrypted with the new password's key; cancellation is possible only while copies are made.
/// </summary>
public sealed partial class JournalWindow
{
    private const int PasswordChangeBusyAttempts = 6;
    private static readonly TimeSpan PasswordChangeBusyDelay = TimeSpan.FromMilliseconds(500);

    private bool _passwordChangeInProgress;

    private void UpdateChangePasswordButton()
    {
        ChangePasswordButton.Content = _localization.GetString("PasswordChange.Start");
        ChangePasswordButton.IsEnabled =
            !_passwordChangeInProgress &&
            TryGetActiveProtectedStorageSession(out _);
    }

    private async void OnChangePasswordClicked(object sender, RoutedEventArgs e)
    {
        if (_passwordChangeInProgress ||
            !TryGetActiveProtectedStorageSession(out ProtectedStorageSessionLease? session) ||
            session is null)
        {
            return;
        }

        _passwordChangeInProgress = true;
        UpdateChangePasswordButton();
        ChangePasswordInfo.IsOpen = false;
        MasterKeyLease? currentKey = null;
        MasterKeyLease? newKey = null;
        string dataRootPath = session.DataRootPath;
        Guid storageId = session.StorageId;
        try
        {
            PasswordChangeRequest? request = await AskForPasswordChangeAsync();
            if (request is null)
            {
                return;
            }

            if (string.IsNullOrEmpty(request.NewPassword) ||
                !string.Equals(request.NewPassword, request.Confirmation, StringComparison.Ordinal))
            {
                ShowPasswordChangeMessage(ChangePasswordInfo, "PasswordChange.Mismatch", InfoBarSeverity.Error);
                return;
            }

            // The session's key goes away with the lock; the change keeps its own copy.
            currentKey = new MasterKeyLease(session.DangerousGetMasterKeyMemory().ToArray());
            byte[] salt = StorageKeyMaterial.GetSalt(currentKey.DangerousGetMemory().Span).ToArray();
            using (MasterKeyLease entered = await _credentialService.DeriveStorageKeyAsync(request.CurrentPassword, salt))
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        entered.DangerousGetMemory().Span,
                        currentKey.DangerousGetMemory().Span))
                {
                    ShowPasswordChangeMessage(ChangePasswordInfo, "PasswordChange.WrongCurrent", InfoBarSeverity.Error);
                    return;
                }
            }

            newKey = await _credentialService.DeriveStorageKeyAsync(request.NewPassword, salt);
            if (CryptographicOperations.FixedTimeEquals(newKey.DangerousGetMemory().Span, currentKey.DangerousGetMemory().Span))
            {
                ShowPasswordChangeMessage(ChangePasswordInfo, "PasswordChange.Same", InfoBarSeverity.Warning);
                return;
            }

            // Like the storage move (decision D2), the change runs locked, with capture stopped.
            if (_lifecycle.CanAccessProtectedData && !TryLockNow())
            {
                ShowPasswordChangeMessage(ChangePasswordInfo, "PasswordChange.LockFailed", InfoBarSeverity.Error);
                return;
            }

            RefreshLifecycleUi();
            (string key, InfoBarSeverity severity) = await RunPasswordChangeAsync(dataRootPath, storageId, currentKey, newKey);
            if (severity == InfoBarSeverity.Success)
            {
                await SavePasswordHintAsync(request.Hint);
            }

            ShowPasswordChangeMessage(LockInfo, key, severity);
        }
        catch
        {
            InfoBar bar = SettingsPanel.Visibility == Visibility.Visible ? ChangePasswordInfo : LockInfo;
            ShowPasswordChangeMessage(bar, "PasswordChange.Failed", InfoBarSeverity.Error);
        }
        finally
        {
            currentKey?.Dispose();
            newKey?.Dispose();
            _passwordChangeInProgress = false;
            UpdateChangePasswordButton();
        }
    }

    private async Task<PasswordChangeRequest?> AskForPasswordChangeAsync()
    {
        var current = new PasswordBox { Header = _localization.GetString("PasswordChange.Current") };
        var fresh = new PasswordBox { Header = _localization.GetString("PasswordChange.New") };
        var confirmation = new PasswordBox { Header = _localization.GetString("PasswordChange.Confirm") };
        var hint = new TextBox
        {
            Header = _localization.GetString("PasswordChange.Hint"),
            Text = _settings.PasswordHint,
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 360 };
        content.Children.Add(new TextBlock
        {
            Text = _localization.GetString("PasswordChange.Body"),
            TextWrapping = TextWrapping.Wrap,
        });
        // A mismatched confirmation is shown while it is typed, not after Apply (З1).
        var mismatch = new TextBlock
        {
            Text = _localization.GetString("Lock.PasswordMismatch"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        if (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out object? critical) &&
            critical is Brush criticalBrush)
        {
            mismatch.Foreground = criticalBrush;
        }

        content.Children.Add(current);
        content.Children.Add(fresh);
        content.Children.Add(confirmation);
        content.Children.Add(mismatch);
        content.Children.Add(hint);

        var dialog = new ContentDialog
        {
            XamlRoot = ShellNavigation.XamlRoot,
            Title = _localization.GetString("PasswordChange.Title"),
            Content = new ScrollViewer { Content = content },
            PrimaryButtonText = _localization.GetString("PasswordChange.Apply"),
            CloseButtonText = _localization.GetString("PasswordChange.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        void RefreshMismatch(object sender, RoutedEventArgs e)
        {
            bool isMismatch = PasswordConfirmation.IsMismatch(fresh.Password, confirmation.Password);
            mismatch.Visibility = isMismatch ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = !isMismatch;
        }

        fresh.PasswordChanged += RefreshMismatch;
        confirmation.PasswordChanged += RefreshMismatch;

        ContentDialogResult result = await dialog.ShowAsync();
        PasswordChangeRequest? request = result == ContentDialogResult.Primary
            ? new PasswordChangeRequest(current.Password, fresh.Password, confirmation.Password, hint.Text.Trim())
            : null;
        current.Password = string.Empty;
        fresh.Password = string.Empty;
        confirmation.Password = string.Empty;
        return request;
    }

    /// <summary>Runs the change with a progress dialog; returns the message key for the lock screen.</summary>
    private async Task<(string Key, InfoBarSeverity Severity)> RunPasswordChangeAsync(
        string dataRootPath,
        Guid storageId,
        MasterKeyLease currentKey,
        MasterKeyLease newKey)
    {
        using var cancellation = new CancellationTokenSource();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = _localization.GetString("PasswordChange.Preparing") };
        var bar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 1 };
        var content = new StackPanel { Spacing = 12, MinWidth = 360 };
        content.Children.Add(bar);
        content.Children.Add(status);

        bool finished = false;
        var dialog = new ContentDialog
        {
            Title = _localization.GetString("PasswordChange.ProgressTitle"),
            Content = content,
            CloseButtonText = _localization.GetString("PasswordChange.Cancel"),
            XamlRoot = ShellNavigation.XamlRoot,
        };
        dialog.CloseButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (!cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
                status.Text = _localization.GetString("PasswordChange.Cancelling");
            }
        };
        dialog.Closing += (_, args) =>
        {
            if (!finished)
            {
                args.Cancel = true;
            }
        };

        var progress = new Progress<PasswordChangeProgress>(update =>
        {
            if (finished || cancellation.IsCancellationRequested)
            {
                return;
            }

            bar.IsIndeterminate = false;
            bar.Value = (double)update.DatabasesReEncrypted / update.DatabaseCount;
            if (update.DatabasesReEncrypted == update.DatabaseCount)
            {
                // Phase 2 switches the copies in and is not cancelled (PASSWORD_CHANGE_PROTOCOL.md §4).
                status.Text = _localization.GetString("PasswordChange.Switching");
                dialog.CloseButtonText = string.Empty;
                return;
            }

            status.Text = FillText(
                _localization.GetString("PasswordChange.Progress"),
                update.DatabasesReEncrypted,
                update.DatabaseCount);
        });

        Task<ContentDialogResult> shown = ShowRelocationDialogAsync(dialog);
        try
        {
            await Task.Run(() => ChangeWhenDatabasesAreFreeAsync(
                dataRootPath,
                storageId,
                currentKey,
                newKey,
                progress,
                cancellation.Token));
            return ("PasswordChange.Completed", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            return ("PasswordChange.Cancelled", InfoBarSeverity.Informational);
        }
        catch (PasswordChangeRefusedException refused)
        {
            return (refused.Refusal switch
            {
                PasswordChangeRefusal.PendingOperation => "PasswordChange.Refused.Pending",
                PasswordChangeRefusal.InsufficientSpace => "PasswordChange.Refused.Space",
                PasswordChangeRefusal.DatabaseBusy => "PasswordChange.Refused.Busy",
                _ => "PasswordChange.Refused.Database",
            }, InfoBarSeverity.Error);
        }
        finally
        {
            finished = true;
            dialog.Hide();
            await shown;
        }
    }

    /// <summary>
    /// Right after the lock an operation of the closed session may still hold a database for a
    /// moment; the exclusive open refuses before anything is written, and the change is tried again.
    /// </summary>
    private static async Task ChangeWhenDatabasesAreFreeAsync(
        string dataRootPath,
        Guid storageId,
        MasterKeyLease currentKey,
        MasterKeyLease newKey,
        IProgress<PasswordChangeProgress> progress,
        CancellationToken cancellationToken)
    {
        var service = new ProtectedStoragePasswordChangeService();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await service.ChangeAsync(
                        dataRootPath,
                        storageId,
                        currentKey.DangerousGetMemory(),
                        newKey.DangerousGetMemory(),
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (PasswordChangeRefusedException refused) when (
                refused.Refusal == PasswordChangeRefusal.DatabaseBusy &&
                attempt < PasswordChangeBusyAttempts)
            {
                await Task.Delay(PasswordChangeBusyDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SavePasswordHintAsync(string hint)
    {
        ApplicationSettings updated = _settings with { PasswordHint = hint };
        try
        {
            await _settingsStore.SaveAsync(updated);
            _settings = updated;
            RefreshLifecycleUi();
        }
        catch
        {
            // The password already changed; an unsaved optional hint must not report it as failed.
        }
    }

    private void ShowPasswordChangeMessage(InfoBar bar, string key, InfoBarSeverity severity)
    {
        bar.Severity = severity;
        bar.Message = _localization.GetString(key);
        bar.IsOpen = true;
    }

    private sealed record PasswordChangeRequest(
        string CurrentPassword,
        string NewPassword,
        string Confirmation,
        string Hint);
}
