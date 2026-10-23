using System.Windows;
using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.Ipc;

namespace Dpt.App.Views;

public partial class PartitionActionWindow : Window
{
    private bool _running;

    public PartitionActionWindow(PartitionActionViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Closing += (_, e) => e.Cancel = _running;
    }

    private PartitionActionViewModel Vm => (PartitionActionViewModel)DataContext;

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (!Vm.CanRun) return;
        _running = true;
        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "Waiting for administrator permission…";
        var progress = new Progress<DiskProgress>(p =>
        {
            ProgressText.Text = p.Message;
            if (p.Fraction > 0)
            {
                Progress.IsIndeterminate = false;
                Progress.Value = p.Fraction;
            }
        });
        var result = await new DiskWorkerLauncher(AppServices.Current).RunAsync(Vm.BuildPlan(), progress);
        _running = false;
        Progress.IsIndeterminate = false;
        Progress.Value = result.Succeeded ? 1 : 0;
        ProgressText.Text = result.Summary;
        CancelButton.Content = "Close";
        CancelButton.IsEnabled = true;
        if (result.Succeeded) DialogResult = true;
    }

    public static Task<bool> ShowAsync(PartitionActionViewModel vm)
    {
        var w = new PartitionActionWindow(vm) { Owner = Application.Current.MainWindow };
        return Task.FromResult(w.ShowDialog() == true);
    }
}
