using System.Collections.Concurrent;

namespace Dpt.App.Infrastructure;

/// <summary>A background STA thread for shell calls (icons, thumbnails) that expect an apartment-threaded caller.</summary>
public sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaWorker(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public static StaWorker Shared { get; } = new("Shell images");

    public void Post(Action action)
    {
        if (!_queue.IsAddingCompleted) _queue.Add(action);
    }

    public Task<T> Run<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { Diagnostics.Log.Error("StaWorker", ex); }
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
