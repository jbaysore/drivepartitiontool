using System.Windows.Controls;

namespace Dpt.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) => HomeSearchBox.Focus();
    }
}
