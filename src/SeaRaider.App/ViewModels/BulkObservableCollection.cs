using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SeaRaider.App.ViewModels;

/// <summary>
/// ObservableCollection that can replace its whole content with a single Reset notification.
/// Re-filtering thousands of store cards one Add at a time would flood the UI with change events.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ResetWith(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
