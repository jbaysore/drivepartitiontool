using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dpt.App.Services;

/// <summary>One running or finished activity (scan, file operation, disk operation) shown in the title-bar pill and flyout.</summary>
public sealed partial class ActivityItem : ObservableObject
{
    private readonly Action? _cancel;
    private readonly Action<bool>? _pause;

    public ActivityItem(string title, Action? cancel = null, Action<bool>? pause = null)
    {
        Title = title;
        _cancel = cancel;
        _pause = pause;
        Started = DateTime.Now;
    }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _progress = -1; // <0 = indeterminate
    [ObservableProperty] private bool _isRunning = true;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _hasError;

    public DateTime Started { get; }
    public bool CanCancel => _cancel is not null;
    public bool CanPause => _pause is not null;

    public string ProgressText => Progress < 0 ? "" : $"{Progress * 100:0}%";

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressText));

    [RelayCommand]
    private void Cancel()
    {
        _cancel?.Invoke();
        Detail = "Cancelling…";
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        _pause?.Invoke(IsPaused);
    }

    public void Complete(string? detail = null, bool error = false)
    {
        IsRunning = false;
        HasError = error;
        Progress = error ? Progress : 1;
        if (detail is not null) Detail = detail;
    }
}

/// <summary>Collects activities from every part of the app (index status, file operations, disk worker).</summary>
public sealed partial class ActivityHub : ObservableObject
{
    private const int KeepFinished = 20;

    public ObservableCollection<ActivityItem> Items { get; } = [];

    /// <summary>The most recent running activity, shown in the title-bar pill.</summary>
    [ObservableProperty] private ActivityItem? _current;

    public ActivityItem Start(string title, Action? cancel = null, Action<bool>? pause = null)
    {
        var item = new ActivityItem(title, cancel, pause);
        OnUi(() =>
        {
            Items.Insert(0, item);
            while (Items.Count > KeepFinished && Items.LastOrDefault(i => !i.IsRunning) is { } old) Items.Remove(old);
            Current = item;
        });
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActivityItem.IsRunning) && !item.IsRunning)
                OnUi(() => Current = Items.FirstOrDefault(i => i.IsRunning));
        };
        return item;
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }
}
