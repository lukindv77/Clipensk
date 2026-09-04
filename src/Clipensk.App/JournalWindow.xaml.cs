using Clipensk.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow : Window
{
    private readonly ILocalizationService _localization;

    public JournalWindow(ILocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        InitializeComponent();

        Title = _localization.GetString("App.Title");
        JournalItem.Content = _localization.GetString("Navigation.Journal");
        ApplicationsItem.Content = _localization.GetString("Navigation.Applications");
        MaintenanceItem.Content = _localization.GetString("Navigation.Maintenance");
        SettingsItem.Content = _localization.GetString("Navigation.Settings");
        AboutItem.Content = _localization.GetString("Navigation.About");

        ShellNavigation.SelectedItem = JournalItem;
        ShowPage("journal");
    }

    public void ShowJournal()
    {
        ShellNavigation.SelectedItem = JournalItem;
        ShowPage("journal");
        Activate();
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag)
        {
            ShowPage(tag);
        }
    }

    private void ShowPage(string tag)
    {
        (string titleKey, string bodyKey) = tag switch
        {
            "applications" => ("Page.Applications.Title", "Page.Applications.Title"),
            "maintenance" => ("Page.Maintenance.Title", "Page.Maintenance.Title"),
            "settings" => ("Page.Settings.Title", "Page.Settings.Title"),
            "about" => ("Page.About.Title", "Page.About.Title"),
            _ => ("Journal.Title", "Journal.Empty"),
        };

        PageTitle.Text = _localization.GetString(titleKey);
        PageBody.Text = _localization.GetString(bodyKey);
    }
}
