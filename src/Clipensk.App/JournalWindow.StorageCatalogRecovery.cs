using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void OnStorageCatalogRecoveryButtonLoaded(object sender, RoutedEventArgs e)
    {
        RefreshStorageCatalogRecoveryButton();
    }

    private async void OnStorageCatalogRecoveryClicked(object sender, RoutedEventArgs e)
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

        MasterKeyLease? acquiredKey = null;
        UnlockButton.IsEnabled = false;
        StorageCatalogRecoveryButton.IsEnabled = false;
        LockInfo.IsOpen = false;

        try
        {
            ProtectedStorageUnlockResult result = await _credentialService.UnlockOrInitializeAsync(
                _settings.DataRootPath,
                password);
            if (!result.IsSuccess)
            {
                if (result.Status == ProtectedStorageUnlockStatus.InvalidMetadata)
                {
                    _credentialState = ProtectedStorageCredentialState.Invalid;
                    ShowInvalidCryptoMetadata();
                }
                else
                {
                    LockInfo.Severity = InfoBarSeverity.Error;
                    LockInfo.Message = _localization.GetString("Lock.InvalidPassword");
                    LockInfo.IsOpen = true;
                }
                return;
            }

            acquiredKey = result.MasterKey
                ?? throw new InvalidDataException("Credential service не вернул MasterKey.");
            _credentialState = ProtectedStorageCredentialState.Ready;

            if (!result.IsStorageInitialized)
            {
                LockInfo.Severity = InfoBarSeverity.Informational;
                LockInfo.Message = StorageCatalogRecoveryText(
                    "NotInitialized",
                    "Защищённое хранилище ещё не завершило первоначальную инициализацию. Используйте «Разблокировать»; восстановление Catalog здесь не выполняется.");
                LockInfo.IsOpen = true;
                return;
            }

            ProtectedStorageDatabaseResult validation =
                await _databaseService.InitializeOrValidateAsync(
                    _settings.DataRootPath,
                    result.StorageId,
                    acquiredKey.DangerousGetMemory(),
                    allowInitialize: false);

            if (validation.IsSuccess)
            {
                LockInfo.Severity = InfoBarSeverity.Success;
                LockInfo.Message = StorageCatalogRecoveryText(
                    "NotNeeded",
                    "Current и storage-catalog.db уже проходят штатную проверку. Восстановление не требуется.");
                LockInfo.IsOpen = true;
                return;
            }

            ProtectedStorageDatabaseResult recovered =
                await TryExplicitStorageCatalogRecoveryAsync(
                    validation,
                    result.StorageId,
                    acquiredKey);
            if (!recovered.IsSuccess)
            {
                ShowStorageFailure(recovered.Status);
                return;
            }

            LockInfo.Severity = InfoBarSeverity.Success;
            LockInfo.Message = StorageCatalogRecoveryText(
                "Completed",
                "storage-catalog.db восстановлен и прошёл штатную проверку. Введите пароль ещё раз и нажмите «Разблокировать».");
            LockInfo.IsOpen = true;
        }
        catch
        {
            LockInfo.Severity = InfoBarSeverity.Error;
            LockInfo.Message = StorageCatalogRecoveryText(
                "Failed",
                "Не удалось восстановить storage-catalog.db. Current и Archive не изменяются до безопасной публикации нового Catalog; проверьте хранилище и повторите операцию.");
            LockInfo.IsOpen = true;
        }
        finally
        {
            acquiredKey?.Dispose();
            PasswordEntry.Password = string.Empty;
            PasswordConfirmationEntry.Password = string.Empty;
            password = string.Empty;
            UnlockButton.IsEnabled = _credentialState != ProtectedStorageCredentialState.Invalid;
            StorageCatalogRecoveryButton.IsEnabled = _credentialState == ProtectedStorageCredentialState.Ready;
            RefreshStorageCatalogRecoveryButton();
        }
    }

    private async Task<ProtectedStorageDatabaseResult> TryExplicitStorageCatalogRecoveryAsync(
        ProtectedStorageDatabaseResult failure,
        Guid storageId,
        MasterKeyLease masterKey)
    {
        if (failure.IsSuccess)
        {
            return failure;
        }

        string dataRootPath = _settings.DataRootPath
            ?? throw new InvalidOperationException("DataRoot не настроен.");
        StorageCatalogRecoveryAction action = ResolveStorageCatalogRecoveryAction(
            dataRootPath,
            failure.Status);
        if (action == StorageCatalogRecoveryAction.None)
        {
            return failure;
        }

        if (!await ConfirmStorageCatalogRecoveryAsync(action))
        {
            return failure;
        }

        LockInfo.Severity = InfoBarSeverity.Informational;
        LockInfo.Message = StorageCatalogRecoveryText(
            "Running",
            "Восстановление storage-catalog.db… Clipensk останется заблокированным до повторной проверки хранилища.");
        LockInfo.IsOpen = true;

        ReadOnlyMemory<byte> key = masterKey.DangerousGetMemory();
        DateOnly currentCalendarDate = DateOnly.FromDateTime(DateTime.Now);

        if (action == StorageCatalogRecoveryAction.RecoverMissingCatalog)
        {
            ProtectedStorageDatabaseResult recovery =
                await new ProtectedStorageCatalogRecoveryService()
                    .RecoverMissingCatalogAsync(
                        dataRootPath,
                        storageId,
                        key,
                        currentCalendarDate);
            if (!recovery.IsSuccess)
            {
                return recovery;
            }
        }
        else
        {
            ProtectedStorageCatalogReplacementResult replacement =
                await new ProtectedStorageCatalogReplacementService()
                    .ReplaceExistingCatalogAsync(
                        dataRootPath,
                        storageId,
                        key,
                        currentCalendarDate);
            if (!replacement.IsSuccess)
            {
                return new ProtectedStorageDatabaseResult(
                    replacement.Status,
                    WasInitialized: false);
            }
        }

        // Recovery never substitutes for normal unlock validation. A rebuilt Catalog must pass
        // the same fail-closed pair validation before a later protected session can be created.
        return await _databaseService.InitializeOrValidateAsync(
            dataRootPath,
            storageId,
            key,
            allowInitialize: false);
    }

    private void RefreshStorageCatalogRecoveryButton()
    {
        bool visible = false;
        bool catalogExists = false;
        if (_credentialState == ProtectedStorageCredentialState.Ready &&
            !string.IsNullOrWhiteSpace(_settings.DataRootPath))
        {
            string currentDirectory = Path.Combine(
                Path.GetFullPath(_settings.DataRootPath),
                "Current");
            visible = File.Exists(Path.Combine(currentDirectory, "current.db"));
            catalogExists = File.Exists(Path.Combine(currentDirectory, "storage-catalog.db"));
        }

        StorageCatalogRecoveryButton.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        StorageCatalogRecoveryButton.Content = catalogExists
            ? StorageCatalogRecoveryText(
                "CheckAction",
                "Проверить / восстановить storage-catalog.db")
            : StorageCatalogRecoveryText(
                "RecoverAction",
                "Восстановить storage-catalog.db");
    }

    private StorageCatalogRecoveryAction ResolveStorageCatalogRecoveryAction(
        string dataRootPath,
        ProtectedStorageDatabaseStatus failureStatus)
    {
        string currentDirectory = Path.Combine(Path.GetFullPath(dataRootPath), "Current");
        string currentPath = Path.Combine(currentDirectory, "current.db");
        string catalogPath = Path.Combine(currentDirectory, "storage-catalog.db");
        bool currentExists = File.Exists(currentPath);
        bool catalogExists = File.Exists(catalogPath);

        if (!currentExists)
        {
            return StorageCatalogRecoveryAction.None;
        }

        return failureStatus switch
        {
            ProtectedStorageDatabaseStatus.MissingOrPartialStorage when !catalogExists =>
                StorageCatalogRecoveryAction.RecoverMissingCatalog,
            ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity when catalogExists =>
                StorageCatalogRecoveryAction.ReplaceExistingCatalog,
            _ => StorageCatalogRecoveryAction.None,
        };
    }

    private async Task<bool> ConfirmStorageCatalogRecoveryAsync(
        StorageCatalogRecoveryAction action)
    {
        bool replaceExisting = action == StorageCatalogRecoveryAction.ReplaceExistingCatalog;
        var dialog = new ContentDialog
        {
            XamlRoot = ShellNavigation.XamlRoot,
            Title = replaceExisting
                ? StorageCatalogRecoveryText(
                    "ReplaceTitle",
                    "Заменить повреждённый каталог хранилища?")
                : StorageCatalogRecoveryText(
                    "RecoverTitle",
                    "Восстановить отсутствующий каталог хранилища?"),
            Content = replaceExisting
                ? StorageCatalogRecoveryText(
                    "ReplaceBody",
                    "storage-catalog.db не прошёл проверку. Clipensk может построить новый каталог только из Current и Archive. Существующий файл будет атомарно заменён и сохранён в Current\\CatalogQuarantine. Current и Archive не изменяются.")
                : StorageCatalogRecoveryText(
                    "RecoverBody",
                    "storage-catalog.db отсутствует. Clipensk может заново построить его только из Current и Archive. Current и Archive не изменяются."),
            PrimaryButtonText = replaceExisting
                ? StorageCatalogRecoveryText("ReplaceAction", "Заменить каталог")
                : StorageCatalogRecoveryText("RecoverAction", "Восстановить каталог"),
            CloseButtonText = StorageCatalogRecoveryText("Cancel", "Отмена"),
            DefaultButton = ContentDialogButton.Close,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private string StorageCatalogRecoveryText(string suffix, string russianFallback)
    {
        string key = $"Lock.CatalogRecovery.{suffix}";
        string localized = _localization.GetString(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? russianFallback
            : localized;
    }

    private enum StorageCatalogRecoveryAction
    {
        None,
        RecoverMissingCatalog,
        ReplaceExistingCatalog,
    }
}
