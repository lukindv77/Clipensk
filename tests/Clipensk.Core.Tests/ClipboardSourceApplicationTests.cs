using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardSourceApplicationTests
{
    [Fact]
    public void LegacyRuntimeMetadataConstructionLeavesAumidUnknown()
    {
        var source = new ClipboardSourceApplication(4242, @"C:\Apps\Source.exe");

        Assert.Equal((uint)4242, source.ProcessId);
        Assert.Equal(@"C:\Apps\Source.exe", source.ExecutablePath);
        Assert.Null(source.ApplicationUserModelId);
    }

    [Fact]
    public void PackagedRuntimeMetadataCanCarryAumidWithoutChangingProcessMetadata()
    {
        var source = new ClipboardSourceApplication(
            4242,
            @"C:\Program Files\WindowsApps\Source.exe",
            "Contoso.Source_abc123!App");

        Assert.Equal((uint)4242, source.ProcessId);
        Assert.Equal(@"C:\Program Files\WindowsApps\Source.exe", source.ExecutablePath);
        Assert.Equal("Contoso.Source_abc123!App", source.ApplicationUserModelId);
    }
}
