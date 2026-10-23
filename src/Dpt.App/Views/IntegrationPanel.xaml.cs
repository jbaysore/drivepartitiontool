using System.Windows;
using System.Windows.Controls;
using Dpt.App.Services;

namespace Dpt.App.Views;

public partial class IntegrationPanel : UserControl
{
    public IntegrationPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => RestoreButton.IsEnabled = AppServices.Current.FolderAssociation.HasSavedState;
    }
    private void OnRestore(object sender, RoutedEventArgs e)
    {
        try
        {
            var services = AppServices.Current;
            var note = services.FolderAssociation.SetEnabled(false);
            var settings = services.Settings.Clone(); settings.DefaultFolderHandler = services.FolderAssociation.IsEnabled;
            services.SaveSettings(settings);
            ResultText.Text = note ?? "The previous folder defaults were restored.";
            RestoreButton.IsEnabled = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        { ResultText.Text = "Folder settings could not be fully restored: " + ex.Message; }
    }
}
