using Clipensk.Core.Application;
using Clipensk.Core.Input;
using Clipensk.Core.Localization;
using Clipensk.Core.Settings;
using Clipensk.Infrastructure.Localization;
using Clipensk.Infrastructure.Settings;
using Clipensk.Windows.Input;
using Microsoft.UI.Xaml;

namespace Clipensk.App;

public partial class App : Application
{
    private JournalWindow? _window;
    private IGlobalHotKeyService? _hotKeyService;
    private ProtectedApplicationLifecycle? _lifecycle;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        ILocalizationService localization = new BuiltInRussianLocalizationService();
        var settingsStore = new JsonApplicationSettingsStore(SettingsPathProvider.GetDefaultSettingsPath());
        ApplicationSettings settings = await settingsStore.LoadAsync();

        _lifecycle = new ProtectedApplicationLifecycle(
            isDataRootConfigured: !string.IsNullOrWhiteSpace(settings.DataRootPath));

        _hotKeyService = new GlobalHotKeyService();
        _window = new JournalWindow(localization, settingsStore, _hotKeyService, settings, _lifecycle);
        _hotKeyService.Pressed += OnJournalHotKeyPressed;
        _window.Closed += OnWindowClosed;

        if (settings.JournalHotKey is { } gesture)
        {
            try
            {
                _hotKeyService.Register(gesture);
            }
            catch
            {
                // Сохранённая комбинация могла стать недоступной после изменения системы.
                // Приложение остаётся работоспособным, пользователь может задать новую комбинацию в настройках.
            }
        }

        _window.Activate();
    }

    private void OnJournalHotKeyPressed(object? sender, EventArgs e)
    {
        _window?.DispatcherQueue.TryEnqueue(() => _window.ShowJournal());
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_hotKeyService is not null)
        {
            _hotKeyService.Pressed -= OnJournalHotKeyPressed;
            _hotKeyService.Dispose();
            _hotKeyService = null;
        }

        _lifecycle = null;
    }
}
