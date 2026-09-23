using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

/// <summary>
/// "Start the current database anew" on the lock screen, for a storage whose <c>current.db</c> was
/// lost (<c>docs/CURRENT_RESTART.md</c>). Offered only when <c>current.db</c> is missing and other
/// storage databases remain; runs before a session, with the password entered and a confirmation.
/// </summary>
public sealed partial class JournalWindow
{
    private void OnCurrentRestartButtonLoaded(object sender, RoutedEventArgs e)
    {
        RefreshCurrentRestartButton();
    }

    private async void OnCurrentRestartClicked(object sender, RoutedEventArgs e)
    {
        if (_credentialState != ProtectedStorageCredentialState.Ready ||
            string.IsNullOrWhiteSpace(_settings.DataRootPath))
        {
            if (_credentialState == ProtectedStorageCredentialState.Invalid)
            {
                ShowInvalidCryptoMetadata();
            }
            return;
        }

        string password = PasswordEntry.Password;
        if (string.IsNullOrEmpty(password))
        {
            LockInfo.Severity = InfoBarSeverity.Error;
            LockInfo.Message = _localization.GetString("Lock.PasswordRequired");
            LockInfo.IsOpen = true;
            return;
        }

        string dataRootPath = _settings.DataRootPath;
        MasterKeyLease? acquiredKey = null;
        UnlockButton.IsEnabled = false;
        LockInfo.IsOpen = false;

        try
        {
            // The password is checked against the databases that remain (docs/CRYPTOGRAPHY.md §4).
            ProtectedStorageUnlockResult result = await _credentialService.UnlockOrInitializeAsync(
                dataRootPath,
                password,
                allowInitialize: false);
            if (!result.IsSuccess)
            {
                ShowUnlockFailure(result);
                return;
            }

            acquiredKey = result.MasterKey
                ?? throw new InvalidDataException("Credential service не вернул ключ хранилища.");
            _credentialState = ProtectedStorageCredentialState.Ready;

            if (!await ConfirmCurrentRestartAsync())
            {
                return;
            }

            LockInfo.Severity = InfoBarSeverity.Informational;
            LockInfo.Message = _localization.GetString("Lock.CurrentRestart.Running");
            LockInfo.IsOpen = true;

            ProtectedCurrentRestartResult restart = await new ProtectedCurrentRestartService().RestartAsync(
                dataRootPath,
                result.StorageId,
                acquiredKey.DangerousGetMemory(),
                DateOnly.FromDateTime(DateTime.Now));

            if (restart.IsSuccess)
            {
                LockInfo.Severity = InfoBarSeverity.Success;
                LockInfo.Message = _localization.GetString("Lock.CurrentRestart.Completed");
            }
            else if (restart.CurrentCreated)
            {
                LockInfo.Severity = InfoBarSeverity.Warning;
                LockInfo.Message = _localization.GetString("Lock.CurrentRestart.CatalogFailed");
            }
            else
            {
                ShowStorageFailure(restart.Status);
                return;
            }

            LockInfo.IsOpen = true;
        }
        catch
        {
            LockInfo.Severity = InfoBarSeverity.Error;
            LockInfo.Message = _localization.GetString("Lock.CurrentRestart.Failed");
            LockInfo.IsOpen = true;
        }
        finally
        {
            acquiredKey?.Dispose();
            PasswordEntry.Password = string.Empty;
            PasswordConfirmationEntry.Password = string.Empty;
            password = string.Empty;
            UnlockButton.IsEnabled = _credentialState != ProtectedStorageCredentialState.Invalid;
            RefreshCurrentRestartButton();
            RefreshStorageCatalogRecoveryButton();
        }
    }

    private async Task<bool> ConfirmCurrentRestartAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ShellNavigation.XamlRoot,
            Title = _localization.GetString("Lock.CurrentRestart.Title"),
            Content = new TextBlock
            {
                Text = _localization.GetString("Lock.CurrentRestart.Body"),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = _localization.GetString("Lock.CurrentRestart.Confirm"),
            CloseButtonText = _localization.GetString("Lock.CatalogRecovery.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private void RefreshCurrentRestartButton()
    {
        CurrentRestartButton.Content = _localization.GetString("Lock.CurrentRestart.Action");
        CurrentRestartButton.Visibility =
            _credentialState == ProtectedStorageCredentialState.Ready && IsCurrentMissing()
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>Whether <c>current.db</c> is gone while other databases of the storage remain.</summary>
    private bool IsCurrentMissing()
    {
        if (string.IsNullOrWhiteSpace(_settings.DataRootPath))
        {
            return false;
        }

        try
        {
            string root = Path.GetFullPath(_settings.DataRootPath);
            return !File.Exists(Path.Combine(root, StorageDatabaseFiles.CurrentRelativePath)) &&
                   StorageDatabaseFiles.EnumerateExisting(root).Count > 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
