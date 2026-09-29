using CommunityToolkit.Mvvm.ComponentModel;

using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>设备下拉选项</summary>
public sealed record DeviceOption(Guid Id, string Name);

/// <summary>点位下拉选项</summary>
public sealed record PointOption(Guid Id, string Name, string Address);

/// <summary>
/// 侧栏导航节点：一级目录（<see cref="IsGroup"/> = true，仅承载二级项、不可选中）或页面项（携带 ViewModel）。
/// 对齐 web <c>App.vue</c> 的 nav-group / nav-item 两级结构（如「协议」目录统辖 Modbus/S7 与 OPC UA 两个分区）。
/// </summary>
public sealed partial class NavNode : ObservableObject
{
    private NavNode(string title, string glyph, ObservableObject? viewModel)
    {
        Title = title;
        Glyph = glyph;
        ViewModel = viewModel;
    }

    /// <summary>一级目录（分组标题）：点击仅展开/收起，不切换内容</summary>
    public static NavNode Group(string title, string glyph) => new(title, glyph, null);

    /// <summary>页面项（顶级或二级）</summary>
    public static NavNode Page(string title, string glyph, ObservableObject viewModel) => new(title, glyph, viewModel);

    public string Title { get; }

    public string Glyph { get; }

    /// <summary>页面 ViewModel；一级目录为 null</summary>
    public ObservableObject? ViewModel { get; }

    /// <summary>二级项（一级目录为空）</summary>
    public ObservableCollection<NavNode> Children { get; } = [];

    /// <summary>是否为一级目录（不可选中）</summary>
    public bool IsGroup => ViewModel is null;

    /// <summary>目录展开态（仅一级目录有意义）</summary>
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>选中态：由 <see cref="MainViewModel"/> 统一维护，保证同时只有一个页面高亮</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>选中请求回调（由 MainViewModel 注入；树控件双向绑定 IsSelected 时触发）</summary>
    internal Action<NavNode>? SelectionRequested { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
            SelectionRequested?.Invoke(this);
    }

    /// <summary>追加二级项并返回自身（便于链式构建）</summary>
    public NavNode With(params NavNode[] children)
    {
        foreach (var child in children)
            Children.Add(child);
        return this;
    }
}

/// <summary>
/// 设备列表页的协议分区。Web 端把设备管理拆成「Modbus / S7 设备」与「OPC UA 设备」两个独立页面
/// （web DeviceListView.vue:28 / OpcUaDeviceList.vue:50 各按协议过滤）；桌面端复用同一套
/// <c>DevicesView</c>，由本范围决定列表过滤与列显隐（OPC UA 用端点/安全档位，通用用从站）。
/// </summary>
public sealed record DeviceListScope(string Title, string Subtitle, bool OpcUaOnly)
{
    /// <summary>Modbus / S7 分区：通用连接参数与批量点位生成。</summary>
    public static readonly DeviceListScope Generic =
        new("Modbus / S7 设备", "现场 Modbus / S7 设备与采集点位配置", false);

    /// <summary>OPC UA 分区：opc.tcp 端点、安全档位与地址空间点选。</summary>
    public static readonly DeviceListScope OpcUa =
        new("OPC UA 设备", "OPC UA 服务器端点、安全与地址空间点位", true);

    private const string OpcUaProtocolName = "OPC UA";

    /// <summary>协议名是否属于本分区（与后端 ProtocolIdentifier.Name 口径一致）。</summary>
    public bool Matches(string protocolName) =>
        string.Equals(protocolName, OpcUaProtocolName, StringComparison.OrdinalIgnoreCase) == OpcUaOnly;
}

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
