using Clipensk.Core.Localization;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ExternalOverlayLocalizationServiceTests
{
    [Fact]
    public void Constructor_NullFallback_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ExternalOverlayLocalizationService(null!));
    }

    [Fact]
    public void GetString_NoOverlaySet_ReturnsFallback()
    {
        var service = new ExternalOverlayLocalizationService(new StubLocalizationService());

        Assert.Equal("fallback:Journal.Title", service.GetString("Journal.Title"));
    }

    [Fact]
    public void GetString_KeyInOverlay_ReturnsOverlayValue()
    {
        var service = new ExternalOverlayLocalizationService(new StubLocalizationService());
        service.SetOverlay(new Dictionary<string, string> { ["Journal.Title"] = "Journal" });

        Assert.Equal("Journal", service.GetString("Journal.Title"));
    }

    [Fact]
    public void GetString_KeyNotInOverlay_FallsBackToInnerService()
    {
        var service = new ExternalOverlayLocalizationService(new StubLocalizationService());
        service.SetOverlay(new Dictionary<string, string> { ["Journal.Title"] = "Journal" });

        Assert.Equal("fallback:Journal.Empty", service.GetString("Journal.Empty"));
    }

    [Fact]
    public void SetOverlay_Null_ClearsOverlayBackToFallback()
    {
        var service = new ExternalOverlayLocalizationService(new StubLocalizationService());
        service.SetOverlay(new Dictionary<string, string> { ["Journal.Title"] = "Journal" });

        service.SetOverlay(null);

        Assert.Equal("fallback:Journal.Title", service.GetString("Journal.Title"));
    }

    [Fact]
    public void SetOverlay_CalledAgain_ReplacesPreviousOverlayRatherThanMerging()
    {
        var service = new ExternalOverlayLocalizationService(new StubLocalizationService());
        service.SetOverlay(new Dictionary<string, string>
        {
            ["Journal.Title"] = "Journal",
            ["Journal.Empty"] = "Empty",
        });

        service.SetOverlay(new Dictionary<string, string> { ["Journal.Title"] = "Journal v2" });

        Assert.Equal("Journal v2", service.GetString("Journal.Title"));
        Assert.Equal("fallback:Journal.Empty", service.GetString("Journal.Empty"));
    }

    [Fact]
    public void GetString_NullOrWhiteSpaceKey_Throws()
    {
        var service = new ExternalOverlayLocalizationService(new StubLocalizationService());

        Assert.Throws<ArgumentException>(() => service.GetString(" "));
    }

    private sealed class StubLocalizationService : ILocalizationService
    {
        public string GetString(string key) => $"fallback:{key}";
    }
}
