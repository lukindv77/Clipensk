namespace Clipensk.Core.Input;

public sealed record HotKeyGesture(HotKeyModifiers Modifiers, uint VirtualKey)
{
    public void Validate()
    {
        if (VirtualKey == 0)
        {
            throw new InvalidOperationException("Горячая клавиша должна содержать основную клавишу.");
        }

        if (Modifiers == HotKeyModifiers.None)
        {
            throw new InvalidOperationException("Глобальная горячая клавиша Clipensk должна содержать хотя бы один модификатор.");
        }
    }
}
