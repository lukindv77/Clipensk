using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
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
        // the same fail-closed pair validation before the protected session can be created.
        return await _databaseService.InitializeOrValidateAsync(
            dataRootPath,
            storageId,
            key,
            allowInitialize: false);
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
