using System.Windows;
using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.Ipc;

namespace Dpt.App.Views;

public partial class BootHealthWindow : Window
{
    public BootHealthWindow(BootHealthViewModel vm)
    {
        InitializeComponent(); DataContext = vm;
        Loaded += async (_, _) => await vm.RefreshAsync();
        Closing += (_, e) => e.Cancel = vm.IsRunning;
        Closed += (_, _) => vm.Dispose();
    }
    private BootHealthViewModel Vm => (BootHealthViewModel)DataContext;
    private async void OnRefresh(object sender, RoutedEventArgs e) => await Vm.RefreshAsync();
    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (!Vm.CanRun) return;
        var plan = Vm.BuildPlan();
        Vm.IsRunning = true; RepairPanel.IsEnabled = CloseButton.IsEnabled = false;
        try
        {
            ResultText.Text = "Waiting for administrator permission…";
            var result = await new DiskWorkerLauncher(AppServices.Current).RunAsync(plan, new Progress<DiskProgress>(p => ResultText.Text = p.Message));
            ResultText.Text = result.Summary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { ResultText.Text = "Boot repair could not complete: " + ex.Message; }
        finally { Vm.IsRunning = false; Vm.Confirmation = ""; RunButton.IsEnabled = false; CloseButton.IsEnabled = true; }
    }
    public static Task ShowAsync()
    {
        new BootHealthWindow(new BootHealthViewModel()) { Owner = Application.Current.MainWindow }.ShowDialog();
        return Task.CompletedTask;
    }
}
