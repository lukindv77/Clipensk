using Clipensk.Core.Application;
using Clipensk.Core.Input;
using Clipensk.Core.Localization;
using Clipensk.Core.Security;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Infrastructure.Localization;
using Clipensk.Infrastructure.Security;
using Clipensk.Infrastructure.Settings;
using Clipensk.Storage.Databases;
using Clipensk.Windows;
using Microsoft.UI.Xaml;

namespace Clipensk.App;

public partial class App : Application
{
    private JournalWindow? _window;
    private ResidentWindowsHost? _residentWindowsHost;
    private IGlobalHotKeyService? _hotKeyService;
    private ProtectedApplicationLifecycle? _lifecycle;
    private IProtectedStorageCredentialService? _credentialService;
    private IProtectedStorageDatabaseService? _databaseService;
    private InvocationApplication? _journalInvocationApplication;

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

        _credentialService = new FileProtectedStorageCredentialService();
        _databaseService = new ProtectedStorageDatabaseService();

        ProtectedStorageCredentialState credentialState = ProtectedStorageCredentialState.Uninitialized;
        if (!string.IsNullOrWhiteSpace(settings.DataRootPath))
        {
            try
            {
                credentialState = await _credentialService.GetStateAsync(settings.DataRootPath);
            }
            catch
            {
                // Любая ошибка чтения криптографических метаданных должна оставлять приложение заблокированным.
                credentialState = ProtectedStorageCredentialState.Invalid;
            }
        }

        _residentWindowsHost = new ResidentWindowsHost();
        _hotKeyService = _residentWindowsHost.HotKeyService;
        _lifecycle.ProtectedDataAccessChanged += OnProtectedDataAccessChanged;
        _window = new JournalWindow(
            localization,
            settingsStore,
            _hotKeyService,
            settings,
            _lifecycle,
            _credentialService,
            _databaseService,
            credentialState);
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

    private void OnJournalHotKeyPressed(object? sender, JournalHotKeyPressedEventArgs e)
    {
        _journalInvocationApplication = e.InvocationApplication;
        _window?.DispatcherQueue.TryEnqueue(() => _window.ShowJournal());
    }

    private void OnProtectedDataAccessChanged(bool canAccessProtectedData)
    {
        ResidentWindowsHost? host = _residentWindowsHost;
        if (host is null)
        {
            return;
        }

        try
        {
            if (canAccessProtectedData)
            {
                host.StartClipboardMonitoring();
            }
            else if (host.IsClipboardMonitoring)
            {
                host.StopClipboardMonitoring();
            }
        }
        catch
        {
            // Ошибка Windows clipboard listener не должна повреждать состояние блокировки.
            // При ошибке мониторинг остаётся выключенным до следующего перехода доступа.
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_hotKeyService is not null)
        {
            _hotKeyService.Pressed -= OnJournalHotKeyPressed;
            _hotKeyService = null;
        }

        if (_lifecycle is not null)
        {
            _lifecycle.ProtectedDataAccessChanged -= OnProtectedDataAccessChanged;
        }

        _residentWindowsHost?.Dispose();
        _residentWindowsHost = null;
        _databaseService = null;
        _credentialService = null;
        _lifecycle = null;
        _journalInvocationApplication = null;
    }
}
