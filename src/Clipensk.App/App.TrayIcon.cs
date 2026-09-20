using Clipensk.Windows;
using Clipensk.Windows.Interop;

namespace Clipensk.App;

public partial class App
{
    private WindowsTrayIconService? _trayIconService;
    private JournalWindow? _trayIconWindow;

    /// <summary>
    /// Shows the tray icon for the app's lifetime, per the explicit product decision: closing the
    /// window with the X always minimizes to tray (already implemented in
    /// <c>JournalWindow.OnAppWindowClosing</c>) — without this icon there would be no visible sign
    /// Clipensk is still running and no way back in except the journal hotkey.
    /// </summary>
    private void StartTrayIcon(ResidentWindowsHost host, JournalWindow window)
    {
        _trayIconService = host.TrayIconService;
        _trayIconWindow = window;
        _trayIconService.JournalRequested += OnTrayJournalRequested;
        _trayIconService.SettingsRequested += OnTraySettingsRequested;
        _trayIconService.ExitRequested += OnTrayExitRequested;
        _trayIconService.Show();
    }

    private void StopTrayIcon()
    {
        if (_trayIconService is null)
        {
            return;
        }

        _trayIconService.JournalRequested -= OnTrayJournalRequested;
        _trayIconService.SettingsRequested -= OnTraySettingsRequested;
        _trayIconService.ExitRequested -= OnTrayExitRequested;
        _trayIconService = null;
        _trayIconWindow = null;
    }

    private void OnTrayJournalRequested()
    {
        JournalWindow? window = _trayIconWindow;
        window?.DispatcherQueue.TryEnqueue(() =>
        {
            window.SetJournalInvocationApplicationHint(null);
            window.ShowJournal();
        });
    }

    private void OnTraySettingsRequested()
    {
        JournalWindow? window = _trayIconWindow;
        window?.DispatcherQueue.TryEnqueue(window.ShowSettings);
    }

    private void OnTrayExitRequested()
    {
        JournalWindow? window = _trayIconWindow;
        window?.DispatcherQueue.TryEnqueue(window.ExitApplication);
    }
}
