using System.Windows;
using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.Ipc;
using Dpt.Storage;

namespace Dpt.App.Views;

public partial class CloneWindow : Window
{
    private bool _running;

    public CloneWindow(CloneViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Closing += (_, e) => e.Cancel = _running;
    }

    private CloneViewModel Vm => (CloneViewModel)DataContext;

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
        ProgressText.Text = result.Succeeded ? "Done. " + Vm.Outcome : result.Summary;
        CancelButton.Content = "Close";
        CancelButton.IsEnabled = true;
    }

    public static Task ShowAsync(DiskInfo? source)
    {
        new CloneWindow(new CloneViewModel(source)) { Owner = Application.Current.MainWindow }.ShowDialog();
        return Task.CompletedTask;
    }
}
