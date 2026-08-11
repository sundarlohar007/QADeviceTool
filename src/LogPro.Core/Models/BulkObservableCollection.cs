using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LogPro.Models;

/// <summary>
/// An ObservableCollection with AddRange support that suppresses multiple CollectionChanged events.
/// This prevents UI thread locks during heavy log bursts.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _isBulkUpdate;

    public void AddRange(IEnumerable<T> items)
    {
        if (items is not List<T> list) list = items.ToList();
        if (list.Count == 0) return;

        _isBulkUpdate = true;
        var startIndex = Items.Count;
        foreach (var item in list)
        {
            Items.Add(item);
        }
        _isBulkUpdate = false;
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        // Ranged Add (not Reset) — preserves bound selection during log bursts (NEW-10).
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, list, startIndex));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_isBulkUpdate)
        {
            base.OnCollectionChanged(e);
        }
    }

    public void RemoveRange(int index, int count)
    {
        if (index < 0 || count <= 0 || index + count > Items.Count)
            return;
        var removed = new List<T>(count);
        _isBulkUpdate = true;
        for (int i = 0; i < count; i++)
        {
            removed.Add(Items[index]);
            Items.RemoveAt(index);
        }
        _isBulkUpdate = false;
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }
}
