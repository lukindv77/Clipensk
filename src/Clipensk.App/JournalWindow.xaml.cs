using Clipensk.Core.Input;
using Clipensk.Core.Localization;
using Clipensk.Core.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow : Window
{
    private readonly ILocalizationService _localization;
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly IGlobalHotKeyService _hotKeyService;
    private ApplicationSettings _settings;
    private bool _allowClose;

    public JournalWindow(
        ILocalizationService localization,
        IApplicationSettingsStore settingsStore,
        IGlobalHotKeyService hotKeyService,
        ApplicationSettings settings)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _hotKeyService = hotKeyService ?? throw new ArgumentNullException(nameof(hotKeyService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        InitializeComponent();
        AppWindow.Closing += OnAppWindowClosing;

        InitializeLocalizedText();
        InitializeHotKeyEditor();

        ShellNavigation.SelectedItem = JournalItem;
        ShowPage("journal");
    }

    public void ShowJournal()
    {
        ShellNavigation.SelectedItem = JournalItem;
        ShowPage("journal");
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

        SettingsTitle.Text = _localization.GetString("Page.Settings.Title");
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
        if (args.SelectedItemContainer?.Tag is string tag)
        {
            ShowPage(tag);
        }
    }

    private void ShowPage(string tag)
    {
        bool isSettings = string.Equals(tag, "settings", StringComparison.Ordinal);
        SettingsPanel.Visibility = isSettings ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPanel.Visibility = isSettings ? Visibility.Collapsed : Visibility.Visible;

        if (isSettings)
        {
            return;
        }

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

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
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
