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

    public event Action<bool>? ProtectedDataAccessChanged;

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

        bool hadProtectedAccess = CanAccessProtectedData;
        _lockStateMachine.CompleteUnlock();
        NotifyProtectedDataAccessChanged(hadProtectedAccess);
    }

    public void CancelUnlock()
    {
        _lockStateMachine.CancelUnlock();
    }

    public bool TryBeginLock()
    {
        bool hadProtectedAccess = CanAccessProtectedData;
        bool started = _lockStateMachine.TryBeginLock();
        NotifyProtectedDataAccessChanged(hadProtectedAccess);
        return started;
    }

    public void CompleteLock()
    {
        bool hadProtectedAccess = CanAccessProtectedData;
        _lockStateMachine.CompleteLock();
        NotifyProtectedDataAccessChanged(hadProtectedAccess);
    }

    private void NotifyProtectedDataAccessChanged(bool previousValue)
    {
        bool currentValue = CanAccessProtectedData;
        if (currentValue != previousValue)
        {
            ProtectedDataAccessChanged?.Invoke(currentValue);
        }
    }
}
