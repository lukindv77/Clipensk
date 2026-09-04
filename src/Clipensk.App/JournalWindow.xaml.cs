using Clipensk.Core.Application;
using Clipensk.Core.Input;
using Clipensk.Core.Localization;
using Clipensk.Core.Settings;
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
    private ApplicationSettings _settings;
    private bool _allowClose;

    public JournalWindow(
        ILocalizationService localization,
        IApplicationSettingsStore settingsStore,
        IGlobalHotKeyService hotKeyService,
        ApplicationSettings settings,
        ProtectedApplicationLifecycle lifecycle)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _hotKeyService = hotKeyService ?? throw new ArgumentNullException(nameof(hotKeyService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

        InitializeComponent();
        AppWindow.Closing += OnAppWindowClosing;

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

        LockTitle.Text = _localization.GetString("Lock.Title");
        LockBody.Text = _localization.GetString("Lock.Body");
        PasswordHintTitle.Text = _localization.GetString("Lock.PasswordHint");
        PasswordEntry.PlaceholderText = _localization.GetString("Lock.PasswordPlaceholder");
        UnlockButton.Content = _localization.GetString("Lock.Unlock");

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
            ApplicationSettings updated = _settings with { DataRootPath = validatedPath };

            await _settingsStore.SaveAsync(updated);

            _settings = updated;
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

    private void OnUnlockClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordEntry.Password))
        {
            LockInfo.Severity = InfoBarSeverity.Error;
            LockInfo.Message = _localization.GetString("Lock.PasswordRequired");
            LockInfo.IsOpen = true;
            return;
        }

        if (!_lifecycle.TryBeginUnlock())
        {
            PasswordEntry.Password = string.Empty;
            return;
        }

        try
        {
            // На этом tranche пароль намеренно не передаётся и не сохраняется:
            // криптографический unlock provider будет реализован отдельным решением.
            LockInfo.Severity = InfoBarSeverity.Warning;
            LockInfo.Message = _localization.GetString("Lock.CryptoNotReady");
            LockInfo.IsOpen = true;
        }
        finally
        {
            PasswordEntry.Password = string.Empty;
            _lifecycle.CancelUnlock();
            RefreshNavigationAvailability();
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
        PasswordHintValue.Text = string.IsNullOrWhiteSpace(_settings.PasswordHint)
            ? _localization.GetString("Lock.PasswordHintEmpty")
            : _settings.PasswordHint;
        LockPanel.Visibility = Visibility.Visible;
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
