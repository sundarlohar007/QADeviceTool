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
        _isBulkUpdate = true;
        foreach (var item in items)
        {
            Items.Add(item);
        }
        _isBulkUpdate = false;
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
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
        _isBulkUpdate = true;
        for (int i = 0; i < count; i++)
        {
            Items.RemoveAt(index);
        }
        _isBulkUpdate = false;
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
