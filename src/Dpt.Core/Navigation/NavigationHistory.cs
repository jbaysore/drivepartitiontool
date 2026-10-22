namespace Dpt.Core.Navigation;

/// <summary>Browser-style back/forward history for one tab.</summary>
public sealed class NavigationHistory<T>
{
    private readonly List<T> _entries = [];
    private int _index = -1;

    public T? Current => _index >= 0 ? _entries[_index] : default;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;
    public IReadOnlyList<T> Entries => _entries;

    /// <summary>Records a new location, discarding any forward entries.</summary>
    public void Navigate(T location)
    {
        if (_index >= 0 && EqualityComparer<T>.Default.Equals(_entries[_index], location)) return;
        if (_index < _entries.Count - 1) _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(location);
        _index = _entries.Count - 1;
    }

    public T GoBack()
    {
        if (!CanGoBack) throw new InvalidOperationException("No back entry.");
        return _entries[--_index];
    }

    public T GoForward()
    {
        if (!CanGoForward) throw new InvalidOperationException("No forward entry.");
        return _entries[++_index];
    }

    /// <summary>Replaces the current entry without adding history (e.g. after a rename of the current folder).</summary>
    public void ReplaceCurrent(T location)
    {
        if (_index < 0) Navigate(location);
        else _entries[_index] = location;
    }
}
