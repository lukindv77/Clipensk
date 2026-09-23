using Clipensk.Core.Platform;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class SupportedWindowsVersionTests
{
    [Theory]
    [InlineData("10.0.22000.0")]
    [InlineData("10.0.22631.4317")]
    [InlineData("10.0.26100.1")]
    [InlineData("11.0.0.0")]
    public void Windows11AndLater_IsSupported(string version)
    {
        Assert.True(SupportedWindowsVersion.IsSupported(Version.Parse(version)));
    }

    [Theory]
    [InlineData("10.0.19045.5011")]
    [InlineData("10.0.21999.0")]
    [InlineData("10.0.17763.0")]
    [InlineData("6.3.9600.0")]
    public void Windows10AndEarlier_IsNotSupported(string version)
    {
        Assert.False(SupportedWindowsVersion.IsSupported(Version.Parse(version)));
    }
}
