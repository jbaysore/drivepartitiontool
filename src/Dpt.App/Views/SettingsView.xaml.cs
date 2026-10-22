using System.Windows;
using System.Windows.Controls;

namespace Dpt.App.Views;

public partial class SettingsView : UserControl
{
    /// <summary>Optional extra panels (e.g. search service status) contributed by later milestones.</summary>
    public static Func<FrameworkElement?>? ExtraContent { get; set; }

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => ExtraHost.Content = ExtraContent?.Invoke();
    }
}
