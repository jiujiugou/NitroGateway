using CommunityToolkit.Mvvm.ComponentModel;

using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>设备下拉选项</summary>
public sealed record DeviceOption(Guid Id, string Name);

/// <summary>点位下拉选项</summary>
public sealed record PointOption(Guid Id, string Name, string Address);

/// <summary>左侧导航项（标题 + Segoe MDL2 图标字形 + 页面 ViewModel，ADR-037 S10）。</summary>
public sealed record NavItem(string Title, string Glyph, ObservableObject ViewModel);

/// <summary>
/// 环形裁剪集合（ADR-037 S12）：从头部批量移除最旧元素，只发一次 Reset 通知。
/// ObservableCollection 无 RemoveRange，逐项 RemoveAt(0) 会产生 N 次 CollectionChanged
/// 与 O(n·k) 搬移；这里直接操作底层 List 并单次通知，LiveCharts 只重排一次。
/// </summary>
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
}
