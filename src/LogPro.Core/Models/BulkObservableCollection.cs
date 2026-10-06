using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LogPro.Models;

/// <summary>Batched notifications compatible with WPF's ListCollectionView.</summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        var batch = items.ToList();
        if (batch.Count == 0) return;
        CheckReentrancy();
        if (Items is List<T> list) list.AddRange(batch);
        else foreach (var item in batch) Items.Add(item);
        NotifyReset();
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
