using Clipensk.Core.Localization;
using Clipensk.Infrastructure.Localization;
using Microsoft.UI.Xaml;

namespace Clipensk.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ILocalizationService localization = new BuiltInRussianLocalizationService();
        _window = new JournalWindow(localization);
        _window.Activate();
    }
}
