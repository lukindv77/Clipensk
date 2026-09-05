using Clipensk.Core.Input;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class InvocationApplicationTests
{
    [Fact]
    public void LegacyRuntimeMetadataConstructionLeavesAumidUnknown()
    {
        var invocation = new InvocationApplication(4242, @"C:\Apps\Invoker.exe");

        Assert.Equal((uint)4242, invocation.ProcessId);
        Assert.Equal(@"C:\Apps\Invoker.exe", invocation.ExecutablePath);
        Assert.Null(invocation.ApplicationUserModelId);
    }

    [Fact]
    public void PackagedRuntimeMetadataCanCarryAumidWithoutChangingProcessMetadata()
    {
        var invocation = new InvocationApplication(
            4242,
            @"C:\Program Files\WindowsApps\Invoker.exe",
            "Contoso.Invoker_abc123!App");

        Assert.Equal((uint)4242, invocation.ProcessId);
        Assert.Equal(@"C:\Program Files\WindowsApps\Invoker.exe", invocation.ExecutablePath);
        Assert.Equal("Contoso.Invoker_abc123!App", invocation.ApplicationUserModelId);
    }
}
