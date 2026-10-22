using System.Windows;
using System.Windows.Controls;
using Dpt.App.ViewModels;
using Dpt.Data;

namespace Dpt.App.Views;

public partial class NavigationPane : UserControl
{
    public NavigationPane() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private static Pin? PinOf(object sender) => (sender as FrameworkElement)?.DataContext as Pin;

    private void OnPinNewTab(object sender, RoutedEventArgs e)
    {
        if (PinOf(sender) is { } pin) Vm?.OpenFolderInNewTabCommand.Execute(pin.Path);
    }

    private void OnUnpin(object sender, RoutedEventArgs e)
    {
        if (PinOf(sender) is { } pin) Vm?.UnpinCommand.Execute(pin.Path);
    }
}
