using System.Threading;
using Clipensk.Core.Application;
using Clipensk.Core.Security;
using Clipensk.Windows.Security;

namespace Clipensk.App;

public partial class App
{
    private static readonly TimeSpan AutoLockPollInterval = TimeSpan.FromSeconds(15);

    private Timer? _autoLockTimer;
    private IdleAutoLockTrigger? _autoLockTrigger;
    private int _autoLockTriggerMinutes;

    private void StartAutoLockMonitor() =>
        _autoLockTimer = new Timer(
            _ => CheckAutoLock(),
            state: null,
            AutoLockPollInterval,
            AutoLockPollInterval);

    private void StopAutoLockMonitor()
    {
        _autoLockTimer?.Dispose();
        _autoLockTimer = null;
        _autoLockTrigger = null;
    }

    /// <summary>
    /// Runs on the timer's thread pool thread. Only the idle-time read and the pure trigger
    /// decision happen here; the actual lock, which touches <see cref="JournalWindow"/> fields, is
    /// marshaled onto the window's dispatcher — the same boundary every other cross-thread
    /// interaction with the window already respects (hotkey presses, clipboard delivery).
    /// </summary>
    private void CheckAutoLock()
    {
        JournalWindow? window = _window;
        ProtectedApplicationLifecycle? lifecycle = _lifecycle;
        if (window is null || lifecycle is null || !lifecycle.CanAccessProtectedData)
        {
            return;
        }

        if (!window.AutoLockEnabled ||
            window.AutoLockAfterMinutes is not int minutes ||
            minutes <= 0)
        {
            return;
        }

        if (_autoLockTrigger is null || _autoLockTriggerMinutes != minutes)
        {
            _autoLockTrigger = new IdleAutoLockTrigger(TimeSpan.FromMinutes(minutes));
            _autoLockTriggerMinutes = minutes;
        }

        TimeSpan idleDuration;
        try
        {
            idleDuration = WindowsIdleTimeReader.GetIdleDuration();
        }
        catch
        {
            // Idle detection is a convenience on top of the fully manual "lock now" action; a
            // failure to read it must never crash the resident process.
            return;
        }

        if (!_autoLockTrigger.ShouldLock(idleDuration))
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(() => window.TryLockNow());
    }
}
