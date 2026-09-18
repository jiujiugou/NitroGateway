using CommunityToolkit.Mvvm.ComponentModel;

using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>设备下拉选项</summary>
public sealed record DeviceOption(Guid Id, string Name);

/// <summary>点位下拉选项</summary>
public sealed record PointOption(Guid Id, string Name, string Address);

public sealed record NavItem(string Title, string Glyph, ObservableObject ViewModel);

public sealed class RingObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>从头部批量移除指定数量元素（单次 Reset 通知）。</summary>
    public void TrimFront(int count)
    {
        if (count <= 0)
            return;
        var remove = Math.Min(count, Count);
        ((List<T>)Items).RemoveRange(0, remove);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void Replace(IEnumerable<T> items)
    {
        var list = (List<T>)Items;
        list.Clear();
        list.AddRange(items);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
