using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Rawr.App.Collections;

/// <summary>
/// ObservableCollection with batch replace/add helpers so large folders don't
/// flood WPF with one collection-change notification per photo.
/// </summary>
public sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    public void AddRange(IEnumerable<T> items)
    {
        var list = items as ICollection<T> ?? items.ToList();
        if (list.Count == 0) return;

        _suppressNotifications = true;
        try
        {
            foreach (var item in list)
                Items.Add(item);
        }
        finally
        {
            _suppressNotifications = false;
        }

        RaiseReset();
    }

    public void ReplaceRange(IEnumerable<T> items)
    {
        _suppressNotifications = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
        }
        finally
        {
            _suppressNotifications = false;
        }

        RaiseReset();
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnPropertyChanged(e);
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
