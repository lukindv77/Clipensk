using Clipensk.Core.Application;
using Clipensk.Core.Input;
using Clipensk.Core.Localization;
using Clipensk.Core.Security;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Infrastructure.Settings;
using Clipensk.Windows.Input;
using Clipensk.Windows.Security;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Clipensk.App;

public sealed partial class JournalWindow : Window
{
    private readonly ExternalOverlayLocalizationService _localization;
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly IGlobalHotKeyService _hotKeyService;
    private readonly ProtectedApplicationLifecycle _lifecycle;
    private readonly IProtectedStorageCredentialService _credentialService;
    private readonly IProtectedStorageDatabaseService _databaseService;
    private ApplicationSettings _settings;
    private ProtectedStorageCredentialState _credentialState;
    private ProtectedStorageSessionLease? _protectedStorageSession;
    private bool _allowClose;
    private nint? _journalFocusRestoreTarget;
    private bool _systemPickerOpen;

    public JournalWindow(
        ExternalOverlayLocalizationService localization,
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
        Activated += OnWindowActivated;

        InitializeLocalizedText();
        InitializeHotKeyEditor();
        InitializeJournalPeriodEditor();
        InitializeArchiveRotationEditor();
        InitializeAutoLockEditor();
        InitializeLocalizationEditor();
        InitializeAutostartEditor();
        InitializeAboutPage();
        InitializeGlobalPolicyUi();
        _lifecycle.ProtectedDataAccessChanged += OnGlobalPolicyProtectedAccessChanged;
        RefreshLifecycleUi();
    }

    public void ShowJournal()
    {
        // Captured only on a fresh reveal (hotkey or tray click while hidden), never on a repeat
        // invocation while already visible — otherwise a second hotkey press while the journal is
        // already the foreground window would overwrite the remembered target with Clipensk's own
        // handle, per the product decision to restore focus to whatever had it before the journal
        // opened.
        if (!AppWindow.IsVisible)
        {
            _journalFocusRestoreTarget = WindowsForegroundFocusTracker.CaptureForeground();
        }

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
            bool journalWasSelected = ReferenceEquals(ShellNavigation.SelectedItem, JournalItem);
            ShellNavigation.SelectedItem = JournalItem;
            ShowPage("journal");

            if (journalWasSelected)
            {
                ShowJournalContent();
                EnsureJournalInitialPeriod();
                _ = LoadJournalAsync(reset: true);
            }
        }

        AppWindow.Show();
        Activate();
    }

    /// <summary>
    /// Settings is reachable without unlocking — it is plaintext configuration, not protected data —
    /// so unlike <see cref="ShowJournal"/> this only falls back to the first-run panel, never the
    /// lock screen, mirroring exactly what <c>OnSelectionChanged</c> already does for the
    /// <c>"settings"</c> tag.
    /// </summary>
    public void ShowSettings()
    {
        if (!_lifecycle.IsDataRootConfigured)
        {
            ShowFirstRunPanel();
        }
        else
        {
            ShellNavigation.SelectedItem = SettingsItem;
            ShowPage("settings");
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
        UseDefaultDataRootButton.Content = _localization.GetString("FirstRun.UseDefaultDataRoot");
        DefaultDataRootHint.Text = _localization.GetString("FirstRun.DefaultDataRootHint");
        DefaultDataRootValue.Text = SettingsPathProvider.GetDefaultDataRootPath();

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

        JournalPeriodTitle.Text = _localization.GetString("Settings.JournalPeriod.Title");
        JournalPeriodBody.Text = _localization.GetString("Settings.JournalPeriod.Body");
        JournalPeriodDays.Header = _localization.GetString("Settings.JournalPeriod.Days");
        SaveJournalPeriodButton.Content = _localization.GetString("Settings.JournalPeriod.Save");

        LockSettingsTitle.Text = _localization.GetString("Settings.Lock.Title");
        LockNowButton.Content = _localization.GetString("Settings.Lock.Now");
        AutoLockEnabledCheckBox.Content = _localization.GetString("Settings.Lock.AutoLockEnabled");
        AutoLockAfterMinutesBox.Header = _localization.GetString("Settings.Lock.AutoLockAfterMinutes");
        SaveAutoLockButton.Content = _localization.GetString("Settings.Lock.Save");

        RotationTitle.Text = _localization.GetString("Settings.Rotation.Title");
        RotationBody.Text = _localization.GetString("Settings.Rotation.Body");
        RotationMaxRecords.Header = _localization.GetString("Settings.Rotation.MaxRecords");
        RotationMaxMegabytes.Header = _localization.GetString("Settings.Rotation.MaxMegabytes");
        RotationMaxDays.Header = _localization.GetString("Settings.Rotation.MaxDays");
        RotationModeLabel.Text = _localization.GetString("Settings.Rotation.Mode");
        SaveRotationButton.Content = _localization.GetString("Settings.Rotation.Save");

        LocalizationTitle.Text = _localization.GetString("Settings.Localization.Title");
        LocalizationBody.Text = _localization.GetString("Settings.Localization.Body");
        LocalizationFileComboBox.Header = _localization.GetString("Settings.Localization.ActiveFile");
        LoadLocalizationFileButton.Content = _localization.GetString("Settings.Localization.LoadFile");
        OpenLanguagesFolderButton.Content = _localization.GetString("Settings.Localization.OpenFolder");
        RereadLocalizationButton.Content = _localization.GetString("Settings.Localization.Reread");
        ExportLocalizationTemplateButton.Content = _localization.GetString("Settings.Localization.ExportTemplate");
        SaveLocalizationButton.Content = _localization.GetString("Settings.Localization.Save");

        AutostartTitle.Text = _localization.GetString("Settings.Autostart.Title");
        AutostartEnabledCheckBox.Content = _localization.GetString("Settings.Autostart.Enabled");
        SaveAutostartButton.Content = _localization.GetString("Settings.Autostart.Save");
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
        SetDataRootActionsEnabled(false);
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

            StorageFolder? folder;
            _systemPickerOpen = true;
            try
            {
                folder = await picker.PickSingleFolderAsync();
            }
            finally
            {
                _systemPickerOpen = false;
            }

            if (folder is null)
            {
                return;
            }

            await ConfigureDataRootAsync(folder.Path);
        }
        catch (Exception)
        {
            ReportDataRootFailure();
        }
        finally
        {
            SetDataRootActionsEnabled(true);
        }
    }

    /// <summary>
    /// Configures the per-user default location, per explicit product decision: each Windows user
    /// stores settings and history only where that user already has private access, so the offered
    /// default is inside their own profile rather than anywhere shared.
    /// </summary>
    private async void OnUseDefaultDataRootClicked(object sender, RoutedEventArgs e)
    {
        SetDataRootActionsEnabled(false);
        DataRootInfo.IsOpen = false;

        try
        {
            await ConfigureDataRootAsync(SettingsPathProvider.GetDefaultDataRootPath());
        }
        catch (Exception)
        {
            ReportDataRootFailure();
        }
        finally
        {
            SetDataRootActionsEnabled(true);
        }
    }

    private async Task ConfigureDataRootAsync(string requestedPath)
    {
        // Lock the directory down before anything is written into it, so the very first durable
        // bytes already land somewhere other users of this computer cannot reach.
        DataRootProtectionResult protection = WindowsDataRootProtectionService.Protect(requestedPath);

        string validatedPath = await ValidateDataRootAsync(requestedPath);
        ProtectedStorageCredentialState credentialState =
            await _credentialService.GetStateAsync(validatedPath);
        if (credentialState == ProtectedStorageCredentialState.Invalid)
        {
            throw new InvalidDataException("Хранилище в выбранном каталоге не распознано: заголовки баз повреждены или формат не поддерживается.");
        }

        ApplicationSettings updated = _settings with { DataRootPath = validatedPath };
        await _settingsStore.SaveAsync(updated);

        _settings = updated;
        _credentialState = credentialState;
        _lifecycle.CompleteFirstRunConfiguration();
        DataRootValue.Text = validatedPath;

        // A location whose access rules were not changed is still usable — its contents stay
        // encrypted — but the user must not be left believing other accounts were shut out when
        // they were not, and each reason for that needs saying plainly.
        LockInfo.Severity = protection == DataRootProtectionResult.Protected
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
        LockInfo.Message = _localization.GetString(protection switch
        {
            DataRootProtectionResult.Protected => "FirstRun.SavedLocked",
            DataRootProtectionResult.SkippedNonEmptyDirectory => "FirstRun.SavedWithoutAccessProtection.NonEmpty",
            _ => "FirstRun.SavedWithoutAccessProtection.Unsupported",
        });
        LockInfo.IsOpen = true;

        RefreshLifecycleUi();
    }

    private void ReportDataRootFailure()
    {
        DataRootInfo.Severity = InfoBarSeverity.Error;
        DataRootInfo.Message = _localization.GetString("FirstRun.ValidationFailed");
        DataRootInfo.IsOpen = true;
        ShowFirstRunPanel();
    }

    private void SetDataRootActionsEnabled(bool enabled)
    {
        ChooseDataRootButton.IsEnabled = enabled;
        UseDefaultDataRootButton.IsEnabled = enabled;
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
            // A new storage is started only when the screen asked for a new password; the databases
            // themselves decide otherwise (docs/CRYPTOGRAPHY.md §3, §9).
            ProtectedStorageUnlockResult result = await _credentialService.UnlockOrInitializeAsync(
                _settings.DataRootPath,
                password,
                allowInitialize: _credentialState == ProtectedStorageCredentialState.Uninitialized);

            if (!result.IsSuccess)
            {
                ShowUnlockFailure(result);
                return;
            }

            acquiredKey = result.MasterKey
                ?? throw new InvalidDataException("Credential service не вернул ключ хранилища.");
            if (!result.IsNewStorage)
            {
                _credentialState = ProtectedStorageCredentialState.Ready;
            }

            ProtectedStorageDatabaseResult storageResult =
                await _databaseService.InitializeOrValidateAsync(
                    _settings.DataRootPath,
                    result.StorageId,
                    acquiredKey.DangerousGetMemory(),
                    allowInitialize: result.IsNewStorage);

            if (!storageResult.IsSuccess)
            {
                ShowStorageFailure(storageResult.Status);
                return;
            }

            if (result.IsNewStorage)
            {
                // The storage exists from the moment its databases are published.
                _credentialState = ProtectedStorageCredentialState.Ready;
                string hint = PasswordHintEditor.Text.Trim();
                ApplicationSettings updated = _settings with { PasswordHint = hint };
                try
                {
                    await _settingsStore.SaveAsync(updated);
                    _settings = updated;
                }
                catch
                {
                    // Ошибка сохранения необязательной подсказки не должна мешать разблокировке уже созданного хранилища.
                }
            }

            _lifecycle.CompleteUnlock();

            try
            {
                _protectedStorageSession?.Dispose();
                _protectedStorageSession = ProtectedStorageSessionLease.Create(
                    _lifecycle,
                    _settings.DataRootPath,
                    result.StorageId,
                    acquiredKey);
                acquiredKey = null;
                unlockCompleted = true;
            }
            catch
            {
                if (_lifecycle.LockState == ApplicationLockState.Unlocked &&
                    _lifecycle.TryBeginLock())
                {
                    _lifecycle.CompleteLock();
                }

                throw;
            }

            RefreshLifecycleUi();
            _ = LoadGlobalCapturePolicyAsync();
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

        if (string.Equals(tag, "applications", StringComparison.Ordinal))
        {
            GlobalPolicyPanel.Visibility = Visibility.Visible;
            _ = LoadGlobalCapturePolicyAsync();
            return;
        }

        bool isSettings = string.Equals(tag, "settings", StringComparison.Ordinal);
        if (isSettings)
        {
            DataRootValue.Text = _settings.DataRootPath ?? _localization.GetString("Settings.DataRoot.NotConfigured");
            // Reopening Settings always shows what is persisted, discarding unsaved edits.
            LoadJournalPeriodEditor();
            LoadArchiveRotationEditor();
            LoadAutoLockEditor();
            LoadLocalizationEditor();
            LoadAutostartEditor();
            SettingsPanel.Visibility = Visibility.Visible;
            return;
        }

        if (string.Equals(tag, "about", StringComparison.Ordinal))
        {
            AboutContentPanel.Visibility = Visibility.Visible;
            return;
        }

        PlaceholderPanel.Visibility = Visibility.Visible;
        bool isJournal = string.Equals(tag, "journal", StringComparison.Ordinal);
        OpenGlobalPolicyButton.Visibility = isJournal ? Visibility.Visible : Visibility.Collapsed;
        JournalPolicyStatus.Visibility = isJournal ? Visibility.Visible : Visibility.Collapsed;

        (string titleKey, string bodyKey) = tag switch
        {
            "applications" => ("Page.Applications.Title", "Page.Applications.Title"),
            "maintenance" => ("Page.Maintenance.Title", "Page.Maintenance.Title"),
            _ => ("Journal.Title", "Journal.Empty"),
        };

        PageTitle.Text = _localization.GetString(titleKey);
        PageBody.Text = _localization.GetString(bodyKey);
    }

    private void RefreshLifecycleUi()
    {
        RefreshNavigationAvailability();
        UpdateDataRootRelocationButton();
        UpdateChangePasswordButton();
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
        LockNowButton.IsEnabled = protectedAccess;
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

        RefreshStorageCatalogRecoveryButton();
        RefreshCurrentRestartButton();
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

    private void ShowUnlockFailure(ProtectedStorageUnlockResult result)
    {
        switch (result.Status)
        {
            case ProtectedStorageUnlockStatus.InvalidStorage:
                _credentialState = ProtectedStorageCredentialState.Invalid;
                ShowInvalidCryptoMetadata();
                break;
            case ProtectedStorageUnlockStatus.StorageUnavailable:
                ShowStorageFailure(result.StorageStatus);
                break;
            case ProtectedStorageUnlockStatus.NewPasswordRequired:
                LockInfo.Severity = InfoBarSeverity.Warning;
                LockInfo.Message = _localization.GetString("Lock.PasswordChangeIncomplete");
                LockInfo.IsOpen = true;
                break;
            default:
                LockInfo.Severity = InfoBarSeverity.Error;
                LockInfo.Message = _localization.GetString("Lock.InvalidPassword");
                LockInfo.IsOpen = true;
                break;
        }
    }

    private void ShowStorageFailure(ProtectedStorageDatabaseStatus status)
    {
        string key = status switch
        {
            ProtectedStorageDatabaseStatus.EncryptionEngineUnavailable => "Lock.EncryptionEngineUnavailable",
            ProtectedStorageDatabaseStatus.MissingOrPartialStorage when IsCurrentMissing() => "Lock.CurrentMissing",
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
        GlobalPolicyPanel.Visibility = Visibility.Collapsed;
        AboutContentPanel.Visibility = Visibility.Collapsed;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        HideJournalWindow();
    }

    /// <summary>
    /// Escape hides the journal exactly like the close button, per the product decision
    /// (2026-09-21) to add Escape and click-away auto-hide as their own hide triggers. Attached to
    /// <see cref="ShellNavigation"/> as a <c>KeyboardAccelerator</c> so it fires regardless of which
    /// control inside the window currently has focus.
    /// </summary>
    private void OnEscapeKeyboardAcceleratorInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        HideJournalWindow();
    }

    /// <summary>
    /// Auto-hides the journal when it loses OS-level foreground activation to a different top-level
    /// window — "click away" — per the same 2026-09-21 decision. A <see cref="ContentDialog"/> stays
    /// inside this window's own XamlRoot and never triggers this; only another real top-level window
    /// (another app, or a system picker) does. <see cref="_systemPickerOpen"/> suppresses this while
    /// a <c>FileOpenPicker</c>/<c>FolderPicker</c> is in flight — those pickers are themselves a
    /// separate top-level window, so opening one would otherwise deactivate and hide the journal out
    /// from under the picker.
    /// </summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated
            && AppWindow.IsVisible
            && !_systemPickerOpen
            && !_allowClose)
        {
            HideJournalWindow();
        }
    }

    private void HideJournalWindow()
    {
        AppWindow.Hide();

        WindowsForegroundFocusTracker.TryRestoreForeground(_journalFocusRestoreTarget);
        _journalFocusRestoreTarget = null;
    }

    private void OnJournalWindowClosed(object sender, WindowEventArgs args)
    {
        _policyWindowClosed = true;
        _lifecycle.ProtectedDataAccessChanged -= OnGlobalPolicyProtectedAccessChanged;
        Interlocked.Increment(ref _policyUiGeneration);
        ClearGlobalPolicyUi();
        _protectedStorageSession?.Dispose();
        _protectedStorageSession = null;
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
