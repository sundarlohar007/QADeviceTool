using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LogPro.Models;

/// <summary>Batched notifications compatible with WPF's ListCollectionView.</summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        var replacement = items.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (var item in replacement) Items.Add(item);
        NotifyReset();
    }
    public void AddRange(IEnumerable<T> items)
    {
        var batch = items.ToList();
        if (batch.Count == 0) return;
        CheckReentrancy();
        // WPF rejects multi-item Add notifications. Individual appends retain
        // virtualized containers; a Reset would rebuild the entire collection view.
        foreach (var item in batch)
        {
            var index = Items.Count;
            Items.Add(item);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }

    public void RemoveRange(int index, int count)
    {
        if (index < 0 || count <= 0 || index > Count - count) return;
        CheckReentrancy();
        if (Items is List<T> list) list.RemoveRange(index, count);
        else for (var i = 0; i < count; i++) Items.RemoveAt(index);
        NotifyReset();
    }

    private void NotifyReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
