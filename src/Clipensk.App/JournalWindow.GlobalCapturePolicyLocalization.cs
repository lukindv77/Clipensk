using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void OnEditGlobalPolicyButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.Content = PolicyText("Edit");
        }
    }
}
