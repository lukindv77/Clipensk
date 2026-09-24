using Clipensk.Core.Security;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class PasswordConfirmationTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("secret", "")]
    [InlineData("secret", "s")]
    [InlineData("secret", "secr")]
    [InlineData("secret", "secret")]
    public void ConfirmationThatCanStillMatch_IsNotMismatch(string password, string confirmation)
    {
        Assert.False(PasswordConfirmation.IsMismatch(password, confirmation));
    }

    [Theory]
    [InlineData("secret", "x")]
    [InlineData("secret", "secx")]
    [InlineData("secret", "Secret")]
    [InlineData("secret", "secret!")]
    [InlineData("", "a")]
    [InlineData("secret", "secre ")]
    public void ConfirmationThatCannotMatch_IsMismatch(string password, string confirmation)
    {
        Assert.True(PasswordConfirmation.IsMismatch(password, confirmation));
    }
}
