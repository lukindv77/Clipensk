using Clipensk.Core.Localization;
using Clipensk.Infrastructure.Settings;
using Clipensk.Windows.Interop;

namespace Clipensk.App;

public partial class App
{
    private WindowsSingleInstanceGuard? _singleInstanceGuard;

    /// <summary>
    /// Enforces one running Clipensk per Windows user before any resident runtime exists, per
    /// explicit product decision. It must run before the hotkey, clipboard listener, tray icon and
    /// protected storage are composed: two instances of one user would otherwise fight over the
    /// same global hotkey and the same <c>current.db</c>.
    ///
    /// Returns <c>false</c> when this process must not continue starting.
    /// </summary>
    private bool TryClaimSingleInstance(ILocalizationService localization)
    {
        if (WindowsSingleInstanceGuard.TryAcquire(
                SettingsPathProvider.GetInstanceLockPath(),
                out WindowsSingleInstanceGuard? guard) &&
            guard is not null)
        {
            _singleInstanceGuard = guard;
            return true;
        }

        // Hand the request to the instance that already holds the lock. That only works within one
        // terminal-services session; for a second session of the same user there is no window to
        // bring forward, so say so rather than exiting with no explanation at all.
        if (!WindowsSingleInstanceGuard.TryActivateRunningInstance())
        {
            WindowsSingleInstanceGuard.ShowAlreadyRunningMessage(
                localization.GetString("SingleInstance.AlreadyRunning"),
                localization.GetString("App.Title"));
        }

        return false;
    }

    private void ReleaseSingleInstance()
    {
        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;
    }

    private void OnActivationRequested()
    {
        JournalWindow? window = _window;
        window?.DispatcherQueue.TryEnqueue(() =>
        {
            window.SetJournalInvocationApplicationHint(null);
            window.ShowJournal();
        });
    }
}
