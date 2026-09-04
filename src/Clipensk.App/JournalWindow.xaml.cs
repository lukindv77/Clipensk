using Clipensk.Core.Application;
using Clipensk.Core.Input;
using Clipensk.Core.Localization;
using Clipensk.Core.Security;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Clipensk.App;

public sealed partial class JournalWindow : Window
{
    private readonly ILocalizationService _localization;
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly IGlobalHotKeyService _hotKeyService;
    private readonly ProtectedApplicationLifecycle _lifecycle;
    private readonly IProtectedStorageCredentialService _credentialService;
    private readonly IProtectedStorageDatabaseService _databaseService;
    private ApplicationSettings _settings;
    private ProtectedStorageCredentialState _credentialState;
    private MasterKeyLease? _masterKeyLease;
    private bool _allowClose;

    public JournalWindow(
        ILocalizationService localization,
        IApplicationSettingsStore settingsStore,
        IGlobalHotKeyService hotKeyService,
        ApplicationSettings settings,
        ProtectedApplicationLifecycle lifecycle,
        IProtectedStorageCredentialService credentialService,
        IProtectedStorageDatabaseService databaseService,
        ProtectedStorageCredentialState credentialState)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _hotKeyService = hotKeyService ?? throw new ArgumentNullException(nameof(hotKeyService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _credentialService = credentialService ?? throw new ArgumentNullException(nameof(credentialService));
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
        _credentialState = credentialState;

        InitializeComponent();
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnJournalWindowClosed;

        InitializeLocalizedText();
        InitializeHotKeyEditor();
        RefreshLifecycleUi();
    }

    public void ShowJournal()
    {
        if (!_lifecycle.IsDataRootConfigured)
        {
            ShowFirstRunPanel();
        }
        else if (!_lifecycle.CanAccessProtectedData)
        {
            ShowLockPanel();
        }
        else
        {
            ShellNavigation.SelectedItem = JournalItem;
            ShowPage("journal");
        }

        AppWindow.Show();
        Activate();
    }

    public void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    private void InitializeLocalizedText()
    {
        Title = _localization.GetString("App.Title");
        JournalItem.Content = _localization.GetString("Navigation.Journal");
        ApplicationsItem.Content = _localization.GetString("Navigation.Applications");
        MaintenanceItem.Content = _localization.GetString("Navigation.Maintenance");
        SettingsItem.Content = _localization.GetString("Navigation.Settings");
        AboutItem.Content = _localization.GetString("Navigation.About");

        FirstRunTitle.Text = _localization.GetString("FirstRun.Title");
        FirstRunBody.Text = _localization.GetString("FirstRun.Body");
        ChooseDataRootButton.Content = _localization.GetString("FirstRun.ChooseDataRoot");

        PasswordHintTitle.Text = _localization.GetString("Lock.PasswordHint");
        PasswordEntry.PlaceholderText = _localization.GetString("Lock.PasswordPlaceholder");
        PasswordSetupHintLabel.Text = _localization.GetString("Lock.SetupHint");
        PasswordHintEditor.PlaceholderText = _localization.GetString("Lock.SetupHintPlaceholder");
        PasswordConfirmationLabel.Text = _localization.GetString("Lock.ConfirmPassword");
        PasswordConfirmationEntry.PlaceholderText = _localization.GetString("Lock.ConfirmPasswordPlaceholder");

        SettingsTitle.Text = _localization.GetString("Page.Settings.Title");
        DataRootTitle.Text = _localization.GetString("Settings.DataRoot.Title");
        HotKeyTitle.Text = _localization.GetString("Settings.HotKey.Title");
        HotKeyKeyLabel.Text = _localization.GetString("Settings.HotKey.Key");
        ApplyHotKeyButton.Content = _localization.GetString("Settings.HotKey.Apply");
    }

    private void InitializeHotKeyEditor()
    {
        IReadOnlyList<HotKeyOption> options = BuildHotKeyOptions();
        HotKeyKey.ItemsSource = options;

        if (_settings.JournalHotKey is not { } gesture)
        {
            HotKeyKey.SelectedIndex = 0;
            return;
        }

        ControlModifier.IsChecked = gesture.Modifiers.HasFlag(HotKeyModifiers.Control);
        AltModifier.IsChecked = gesture.Modifiers.HasFlag(HotKeyModifiers.Alt);
        ShiftModifier.IsChecked = gesture.Modifiers.HasFlag(HotKeyModifiers.Shift);
        WindowsModifier.IsChecked = gesture.Modifiers.HasFlag(HotKeyModifiers.Windows);

        HotKeyOption? selected = options.FirstOrDefault(option => option.VirtualKey == gesture.VirtualKey);
        if (selected is null)
        {
            selected = new HotKeyOption($"VK 0x{gesture.VirtualKey:X2}", gesture.VirtualKey);
            HotKeyKey.ItemsSource = options.Append(selected).ToArray();
        }

        HotKeyKey.SelectedItem = selected;
    }

    private async void OnChooseDataRootClicked(object sender, RoutedEventArgs e)
    {
        ChooseDataRootButton.IsEnabled = false;
        DataRootInfo.IsOpen = false;

        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");

            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            string validatedPath = await ValidateDataRootAsync(folder.Path);
            ProtectedStorageCredentialState credentialState =
                await _credentialService.GetStateAsync(validatedPath);
            if (credentialState == ProtectedStorageCredentialState.Invalid)
            {
                throw new InvalidDataException("Криптографические метаданные выбранного каталога повреждены или не поддерживаются.");
            }

            ApplicationSettings updated = _settings with { DataRootPath = validatedPath };
            await _settingsStore.SaveAsync(updated);

            _settings = updated;
            _credentialState = credentialState;
            _lifecycle.CompleteFirstRunConfiguration();
            DataRootValue.Text = validatedPath;

            LockInfo.Severity = InfoBarSeverity.Success;
            LockInfo.Message = _localization.GetString("FirstRun.SavedLocked");
            LockInfo.IsOpen = true;

            RefreshLifecycleUi();
        }
        catch (Exception)
        {
            DataRootInfo.Severity = InfoBarSeverity.Error;
            DataRootInfo.Message = _localization.GetString("FirstRun.ValidationFailed");
            DataRootInfo.IsOpen = true;
            ShowFirstRunPanel();
        }
        finally
        {
            ChooseDataRootButton.IsEnabled = true;
        }
    }

    private async void OnUnlockClicked(object sender, RoutedEventArgs e)
    {
        if (_credentialState == ProtectedStorageCredentialState.Invalid ||
            string.IsNullOrWhiteSpace(_settings.DataRootPath))
        {
            ShowInvalidCryptoMetadata();
            return;
        }

        string password = PasswordEntry.Password;
        string confirmation = PasswordConfirmationEntry.Password;

        if (string.IsNullOrEmpty(password))
        {
            LockInfo.Severity = InfoBarSeverity.Error;
            LockInfo.Message = _localization.GetString("Lock.PasswordRequired");
            LockInfo.IsOpen = true;
            return;
        }

        if (_credentialState == ProtectedStorageCredentialState.Uninitialized &&
            !string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            LockInfo.Severity = InfoBarSeverity.Error;
            LockInfo.Message = _localization.GetString("Lock.PasswordMismatch");
            LockInfo.IsOpen = true;
            return;
        }

        if (!_lifecycle.TryBeginUnlock())
        {
            PasswordEntry.Password = string.Empty;
            PasswordConfirmationEntry.Password = string.Empty;
            return;
        }

        bool unlockCompleted = false;
        MasterKeyLease? acquiredKey = null;
        UnlockButton.IsEnabled = false;
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

            if (result.WasInitialized)
            {
                string hint = PasswordHintEditor.Text.Trim();
                ApplicationSettings updated = _settings with { PasswordHint = hint };
                try
                {
                    await _settingsStore.SaveAsync(updated);
                    _settings = updated;
                }
                catch
                {
                    // Ошибка сохранения необязательной подсказки не должна уничтожать уже созданный crypto profile.
                }
            }

            ProtectedStorageDatabaseResult storageResult =
                await _databaseService.InitializeOrValidateAsync(
                    _settings.DataRootPath,
                    result.StorageId,
                    acquiredKey.DangerousGetMemory(),
                    allowInitialize: !result.IsStorageInitialized);

            if (!storageResult.IsSuccess)
            {
                ShowStorageFailure(storageResult.Status);
                return;
            }

            if (!result.IsStorageInitialized)
            {
                await _credentialService.MarkStorageInitializedAsync(
                    _settings.DataRootPath,
                    result.StorageId);
            }

            _lifecycle.CompleteUnlock();
            unlockCompleted = true;

            _masterKeyLease?.Dispose();
            _masterKeyLease = acquiredKey;
            acquiredKey = null;

            RefreshLifecycleUi();
        }
        catch (Exception)
        {
            LockInfo.Severity = InfoBarSeverity.Error;
            LockInfo.Message = _localization.GetString("Lock.UnlockFailed");
            LockInfo.IsOpen = true;
        }
        finally
        {
            acquiredKey?.Dispose();
            PasswordEntry.Password = string.Empty;
            PasswordConfirmationEntry.Password = string.Empty;
            password = string.Empty;
            confirmation = string.Empty;

            if (!unlockCompleted && _lifecycle.LockState == ApplicationLockState.Unlocking)
            {
                _lifecycle.CancelUnlock();
            }

            if (!_lifecycle.CanAccessProtectedData)
            {
                RefreshCredentialUi();
            }
        }
    }

    private async void OnApplyHotKeyClicked(object sender, RoutedEventArgs e)
    {
        if (HotKeyKey.SelectedItem is not HotKeyOption option)
        {
            return;
        }

        HotKeyModifiers modifiers = HotKeyModifiers.None;
        if (ControlModifier.IsChecked == true) modifiers |= HotKeyModifiers.Control;
        if (AltModifier.IsChecked == true) modifiers |= HotKeyModifiers.Alt;
        if (ShiftModifier.IsChecked == true) modifiers |= HotKeyModifiers.Shift;
        if (WindowsModifier.IsChecked == true) modifiers |= HotKeyModifiers.Windows;

        var candidate = new HotKeyGesture(modifiers, option.VirtualKey);
        HotKeyGesture? previous = _settings.JournalHotKey;

        try
        {
            _hotKeyService.Register(candidate);

            ApplicationSettings updated = _settings with { JournalHotKey = candidate };
            try
            {
                await _settingsStore.SaveAsync(updated);
            }
            catch
            {
                if (previous is not null)
                {
                    _hotKeyService.Register(previous);
                }
                else
                {
                    _hotKeyService.Unregister();
                }

                throw;
            }

            _settings = updated;
            HotKeyInfo.Severity = InfoBarSeverity.Success;
            HotKeyInfo.Message = _localization.GetString("Settings.HotKey.Saved");
            HotKeyInfo.IsOpen = true;
        }
        catch (Exception)
        {
            HotKeyInfo.Severity = InfoBarSeverity.Error;
            HotKeyInfo.Message = _localization.GetString("Settings.HotKey.Failed");
            HotKeyInfo.IsOpen = true;
        }
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        if (!_lifecycle.IsDataRootConfigured && !string.Equals(tag, "about", StringComparison.Ordinal))
        {
            ShowFirstRunPanel();
            return;
        }

        if (!_lifecycle.CanAccessProtectedData &&
            (string.Equals(tag, "journal", StringComparison.Ordinal) ||
             string.Equals(tag, "applications", StringComparison.Ordinal)))
        {
            ShowLockPanel();
            return;
        }

        ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        HideContentPanels();

        bool isSettings = string.Equals(tag, "settings", StringComparison.Ordinal);
        if (isSettings)
        {
            DataRootValue.Text = _settings.DataRootPath ?? _localization.GetString("Settings.DataRoot.NotConfigured");
            SettingsPanel.Visibility = Visibility.Visible;
            return;
        }

        PlaceholderPanel.Visibility = Visibility.Visible;

        (string titleKey, string bodyKey) = tag switch
        {
            "applications" => ("Page.Applications.Title", "Page.Applications.Title"),
            "maintenance" => ("Page.Maintenance.Title", "Page.Maintenance.Title"),
            "about" => ("Page.About.Title", "Page.About.Title"),
            _ => ("Journal.Title", "Journal.Empty"),
        };

        PageTitle.Text = _localization.GetString(titleKey);
        PageBody.Text = _localization.GetString(bodyKey);
    }

    private void RefreshLifecycleUi()
    {
        RefreshNavigationAvailability();
        DataRootValue.Text = _settings.DataRootPath ?? _localization.GetString("Settings.DataRoot.NotConfigured");
        PasswordHintValue.Text = string.IsNullOrWhiteSpace(_settings.PasswordHint)
            ? _localization.GetString("Lock.PasswordHintEmpty")
            : _settings.PasswordHint;

        if (!_lifecycle.IsDataRootConfigured)
        {
            ShowFirstRunPanel();
        }
        else if (!_lifecycle.CanAccessProtectedData)
        {
            ShowLockPanel();
        }
        else
        {
            ShellNavigation.SelectedItem = JournalItem;
            ShowPage("journal");
        }
    }

    private void RefreshNavigationAvailability()
    {
        bool protectedAccess = _lifecycle.CanAccessProtectedData;
        bool safeShell = _lifecycle.CanUseSafeShell;

        JournalItem.IsEnabled = protectedAccess;
        ApplicationsItem.IsEnabled = protectedAccess;
        MaintenanceItem.IsEnabled = safeShell;
        SettingsItem.IsEnabled = safeShell;
        AboutItem.IsEnabled = true;
    }

    private void RefreshCredentialUi()
    {
        bool isSetup = _credentialState == ProtectedStorageCredentialState.Uninitialized;
        bool isInvalid = _credentialState == ProtectedStorageCredentialState.Invalid;

        LockTitle.Text = _localization.GetString(isSetup ? "Lock.SetupTitle" : "Lock.Title");
        LockBody.Text = _localization.GetString(isSetup ? "Lock.SetupBody" : "Lock.Body");
        UnlockButton.Content = _localization.GetString(isSetup ? "Lock.InitializeAndUnlock" : "Lock.Unlock");

        PasswordHintDisplayPanel.Visibility = isSetup ? Visibility.Collapsed : Visibility.Visible;
        PasswordSetupHintPanel.Visibility = isSetup ? Visibility.Visible : Visibility.Collapsed;
        PasswordConfirmationPanel.Visibility = isSetup ? Visibility.Visible : Visibility.Collapsed;

        PasswordEntry.IsEnabled = !isInvalid;
        PasswordConfirmationEntry.IsEnabled = !isInvalid;
        PasswordHintEditor.IsEnabled = !isInvalid;
        UnlockButton.IsEnabled = !isInvalid;

        PasswordHintValue.Text = string.IsNullOrWhiteSpace(_settings.PasswordHint)
            ? _localization.GetString("Lock.PasswordHintEmpty")
            : _settings.PasswordHint;

        if (isInvalid)
        {
            ShowInvalidCryptoMetadata();
        }
    }

    private void ShowFirstRunPanel()
    {
        ShellNavigation.SelectedItem = null;
        HideContentPanels();
        FirstRunPanel.Visibility = Visibility.Visible;
    }

    private void ShowLockPanel()
    {
        ShellNavigation.SelectedItem = null;
        HideContentPanels();
        RefreshCredentialUi();
        LockPanel.Visibility = Visibility.Visible;
    }

    private void ShowInvalidCryptoMetadata()
    {
        LockInfo.Severity = InfoBarSeverity.Error;
        LockInfo.Message = _localization.GetString("Lock.InvalidMetadata");
        LockInfo.IsOpen = true;
    }

    private void ShowStorageFailure(ProtectedStorageDatabaseStatus status)
    {
        string key = status switch
        {
            ProtectedStorageDatabaseStatus.EncryptionEngineUnavailable => "Lock.EncryptionEngineUnavailable",
            ProtectedStorageDatabaseStatus.MissingOrPartialStorage => "Lock.StorageMissingOrPartial",
            ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity => "Lock.StorageIdentityInvalid",
            _ => "Lock.StorageOpenFailed",
        };

        LockInfo.Severity = InfoBarSeverity.Error;
        LockInfo.Message = _localization.GetString(key);
        LockInfo.IsOpen = true;
    }

    private void HideContentPanels()
    {
        FirstRunPanel.Visibility = Visibility.Collapsed;
        LockPanel.Visibility = Visibility.Collapsed;
        PlaceholderPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }

    private void OnJournalWindowClosed(object sender, WindowEventArgs args)
    {
        _masterKeyLease?.Dispose();
        _masterKeyLease = null;
    }

    private static async Task<string> ValidateDataRootAsync(string selectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);

        string normalizedPath = Path.GetFullPath(selectedPath);
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException(normalizedPath);
        }

        string probePath = Path.Combine(normalizedPath, $".clipensk-write-probe-{Guid.NewGuid():N}.tmp");

        try
        {
            await using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous);

            stream.WriteByte(0x43);
            await stream.FlushAsync();
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }

        return normalizedPath;
    }

    private static IReadOnlyList<HotKeyOption> BuildHotKeyOptions()
    {
        var options = new List<HotKeyOption>();

        for (uint key = 0x41; key <= 0x5A; key++)
        {
            options.Add(new HotKeyOption(((char)key).ToString(), key));
        }

        for (uint key = 0x30; key <= 0x39; key++)
        {
            options.Add(new HotKeyOption(((char)key).ToString(), key));
        }

        for (uint index = 0; index < 12; index++)
        {
            options.Add(new HotKeyOption($"F{index + 1}", 0x70 + index));
        }

        return options;
    }

    private sealed record HotKeyOption(string DisplayName, uint VirtualKey);
}
