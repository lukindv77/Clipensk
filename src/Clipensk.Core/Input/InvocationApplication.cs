namespace Clipensk.Core.Input;

public readonly record struct InvocationApplication(
    uint ProcessId,
    string? ExecutablePath,
    string? ApplicationUserModelId = null);
