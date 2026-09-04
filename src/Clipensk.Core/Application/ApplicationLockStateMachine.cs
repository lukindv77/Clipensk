using System.Threading;

namespace Clipensk.Core.Application;

public sealed class ApplicationLockStateMachine
{
    private int _state = (int)ApplicationLockState.Locked;

    public ApplicationLockState Current => (ApplicationLockState)Volatile.Read(ref _state);

    public bool TryBeginUnlock()
    {
        return Interlocked.CompareExchange(
            ref _state,
            (int)ApplicationLockState.Unlocking,
            (int)ApplicationLockState.Locked) == (int)ApplicationLockState.Locked;
    }

    public void CompleteUnlock()
    {
        TransitionRequired(ApplicationLockState.Unlocking, ApplicationLockState.Unlocked);
    }

    public void CancelUnlock()
    {
        TransitionRequired(ApplicationLockState.Unlocking, ApplicationLockState.Locked);
    }

    public bool TryBeginLock()
    {
        return Interlocked.CompareExchange(
            ref _state,
            (int)ApplicationLockState.Locking,
            (int)ApplicationLockState.Unlocked) == (int)ApplicationLockState.Unlocked;
    }

    public void CompleteLock()
    {
        TransitionRequired(ApplicationLockState.Locking, ApplicationLockState.Locked);
    }

    private void TransitionRequired(ApplicationLockState expected, ApplicationLockState next)
    {
        int previous = Interlocked.CompareExchange(ref _state, (int)next, (int)expected);
        if (previous != (int)expected)
        {
            throw new InvalidOperationException(
                $"Недопустимый переход состояния Clipensk: ожидалось {expected}, фактически {((ApplicationLockState)previous)}.");
        }
    }
}
