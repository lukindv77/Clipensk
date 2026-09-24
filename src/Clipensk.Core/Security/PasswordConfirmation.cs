namespace Clipensk.Core.Security;

/// <summary>
/// Tells while the user types whether a new password's confirmation can still match it, so a
/// mismatch is shown at once instead of only after the button is pressed (smoke finding З1,
/// 2026-09-24). A confirmation still being typed correctly — a prefix of the password — is not a
/// mismatch yet; one that differs at any character, or is complete but not equal, is.
/// </summary>
public static class PasswordConfirmation
{
    public static bool IsMismatch(string password, string confirmation)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(confirmation);

        if (confirmation.Length == 0)
        {
            return false;
        }

        return confirmation.Length < password.Length
            ? !password.StartsWith(confirmation, StringComparison.Ordinal)
            : !string.Equals(password, confirmation, StringComparison.Ordinal);
    }
}
