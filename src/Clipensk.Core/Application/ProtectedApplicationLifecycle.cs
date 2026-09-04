namespace Clipensk.Core.Application;

public sealed class ProtectedApplicationLifecycle
{
    private readonly ApplicationLockStateMachine _lockStateMachine;

    public ProtectedApplicationLifecycle(
        bool isDataRootConfigured,
        ApplicationLockStateMachine? lockStateMachine = null)
    {
        IsDataRootConfigured = isDataRootConfigured;
        _lockStateMachine = lockStateMachine ?? new ApplicationLockStateMachine();
    }

    public bool IsDataRootConfigured { get; private set; }

    public ApplicationLockState LockState => _lockStateMachine.Current;

    public bool CanAccessProtectedData =>
        IsDataRootConfigured && LockState == ApplicationLockState.Unlocked;

    public bool CanUseSafeShell => IsDataRootConfigured;

    public void CompleteFirstRunConfiguration()
    {
        IsDataRootConfigured = true;
    }

    public bool TryBeginUnlock()
    {
        return IsDataRootConfigured && _lockStateMachine.TryBeginUnlock();
    }

    public void CompleteUnlock()
    {
        if (!IsDataRootConfigured)
        {
            throw new InvalidOperationException("Нельзя разблокировать Clipensk до выбора каталога данных.");
        }

        _lockStateMachine.CompleteUnlock();
    }

    public void CancelUnlock()
    {
        _lockStateMachine.CancelUnlock();
    }

    public bool TryBeginLock()
    {
        return _lockStateMachine.TryBeginLock();
    }

    public void CompleteLock()
    {
        _lockStateMachine.CompleteLock();
    }
}
