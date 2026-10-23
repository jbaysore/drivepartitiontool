using System.Windows;
using Dpt.App.Services;
using Dpt.App.ViewModels;
using Dpt.Ipc;
using Dpt.Storage;
using Microsoft.Win32;

namespace Dpt.App.Views;

public partial class WindowsCopyWindow : Window
{
    public WindowsCopyWindow(WindowsCopyViewModel vm)
    {
        InitializeComponent(); DataContext = vm;
        Closing += (_, e) => e.Cancel = vm.IsRunning;
        Closed += (_, _) => vm.Dispose();
    }
    private WindowsCopyViewModel Vm => (WindowsCopyViewModel)DataContext;
    private async void OnLoad(object sender, RoutedEventArgs e) => await Vm.LoadAsync();

    private async void OnBackup(object sender, RoutedEventArgs e)
    {
        if (Vm.IsRunning) return;
        var picker = new OpenFolderDialog { Title = "Copy excluded personal folders to another drive" };
        if (picker.ShowDialog(this) != true) return;
        string backupFolder;
        try
        {
            backupFolder = WindowsCopyBackup.ResolveFolder(picker.FolderName);
            var disks = await Task.Run(() => StorageInventory.GetDisks());
            if (WindowsCopyBackup.Problem(backupFolder, disks, Vm.Source!.Disk.Number, Vm.Destination?.Disk.Number ?? -1) is { } problem)
            { AppServices.Current.FileOps.Dialogs.ShowError("Choose another drive", problem); return; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { AppServices.Current.FileOps.Dialogs.ShowError("Could not check the backup drive", ex.Message); return; }
        var sourceRoot = Vm.Source!.Root;
        var paths = Vm.ExcludedPersonalPaths;
        Vm.IsRunning = true;
        SelectionPanel.IsEnabled = ContentGrid.IsEnabled = ConfirmationBox.IsEnabled = BackupButton.IsEnabled = CancelButton.IsEnabled = false;
        try
        {
            foreach (var path in paths)
            {
                var relative = Path.GetRelativePath(sourceRoot, path);
                var parent = Path.Combine(backupFolder, Path.GetDirectoryName(relative) ?? "");
                Directory.CreateDirectory(parent);
                var result = await AppServices.Current.FileOps.CopyAsync([path], parent, "Windows-copy backup");
                if (result is null || result.Failed.Count > 0 || result.Cancelled) { ResultText.Text = "The personal-data copy did not finish. Review the activity details before continuing."; return; }
            }
            ResultText.Text = "The excluded personal folders were copied. Review the destination preview before starting Windows copy.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ResultText.Text = "The personal-data copy stopped: " + ex.Message; }
        finally
        {
            Vm.IsRunning = false;
            Vm.Confirmation = "";
            SelectionPanel.IsEnabled = ContentGrid.IsEnabled = ConfirmationBox.IsEnabled = CancelButton.IsEnabled = true;
            BackupButton.IsEnabled = Vm.HasExcludedPersonal;
        }
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (!Vm.CanRun) return;
        var plan = Vm.BuildPlan();
        Vm.IsRunning = true;
        SelectionPanel.IsEnabled = ContentGrid.IsEnabled = ConfirmationBox.IsEnabled = BackupButton.IsEnabled = CancelButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        ResultText.Text = "Waiting for administrator permission…";
        try
        {
            var result = await new DiskWorkerLauncher(AppServices.Current).RunAsync(plan, new Progress<DiskProgress>(p =>
            { ResultText.Text = p.Message; if (p.Fraction > 0) { Progress.IsIndeterminate = false; Progress.Value = p.Fraction; } }));
            ResultText.Text = result.Succeeded ? "Copy written. " + Vm.Outcome : result.Summary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { ResultText.Text = "The copy could not complete: " + ex.Message + " Review the destination before using it."; }
        finally
        {
            Progress.IsIndeterminate = false;
            Vm.IsRunning = false;
            Vm.Confirmation = ""; // A completed/stopped run cannot be repeated with stale fingerprints.
            RunButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
        }
    }

    public static async Task ShowAsync()
    {
        var disks = await Task.Run(() => StorageInventory.GetDisks());
        new WindowsCopyWindow(new WindowsCopyViewModel(disks)) { Owner = Application.Current.MainWindow }.ShowDialog();
    }
}
