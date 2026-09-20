namespace Clipensk.Core.Security;

/// <summary>
/// Edge-triggered decision for automatic locking on user inactivity, per
/// <c>docs/REQUIREMENTS.md</c> §3: "Автоматическая блокировка — опция, по умолчанию отключена."
///
/// This fires exactly once per continuous idle period that reaches the configured threshold, and
/// re-arms only once activity resumes (idle duration drops back below the threshold). Without that
/// re-arming, a caller that polls this on a timer would ask the lifecycle to lock on every tick
/// while the machine stays idle past the threshold — harmless against an already-locked lifecycle,
/// but the wrong contract for a trigger: "lock" should mean "an idle period just crossed the line,"
/// not "the machine happens to still be idle right now."
///
/// This type is deliberately platform-agnostic: it takes the measured idle duration as input rather
/// than reading it itself, so the actual Windows idle-time query
/// (<c>GetLastInputInfo</c>) stays in <c>Clipensk.Windows</c> and this rule can be tested without it.
/// </summary>
public sealed class IdleAutoLockTrigger
{
    private readonly object _gate = new();
    private readonly TimeSpan _threshold;
    private bool _armed = true;

    public IdleAutoLockTrigger(TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold),
                "Auto-lock idle threshold must be positive.");
        }

        _threshold = threshold;
    }

    /// <summary>
    /// Reports whether this poll should trigger a lock. <paramref name="idleDuration"/> is the
    /// continuous duration the user has been inactive, as measured at the moment of the call.
    ///
    /// Safe to call from a timer callback while <see cref="Reset"/> is called from an unrelated
    /// unlock-completion callback on a different thread; both are serialized internally.
    /// </summary>
    public bool ShouldLock(TimeSpan idleDuration)
    {
        if (idleDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleDuration),
                "Idle duration cannot be negative.");
        }

        lock (_gate)
        {
            if (idleDuration < _threshold)
            {
                _armed = true;
                return false;
            }

            if (!_armed)
            {
                return false;
            }

            _armed = false;
            return true;
        }
    }

    /// <summary>Re-arms the trigger, as if activity had just resumed. Used after an unlock.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _armed = true;
        }
    }
}
