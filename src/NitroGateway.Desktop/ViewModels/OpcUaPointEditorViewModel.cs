using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NitroGateway.Desktop.Services.Connectivity;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// OPC UA 点位编辑表单：左=服务器地址空间浏览树（懒加载），右=点位映射配置。
/// 对齐 web <c>OpcUaPointTable.vue</c>（左树右表单）：点选变量节点自动回填 NodeId / 数据类型 / 权限；
/// 服务器类型不在网关支持的 11 种内时给出警告并允许手动选择相近类型。
/// </summary>
public sealed partial class OpcUaPointEditorViewModel : ObservableObject, IDisposable
{
    private readonly Guid _deviceId;
    private readonly IOpcUaNodeBrowser _browser;

    /// <summary>点位映射表单（复用 <see cref="PointEditor"/> 的字段/校验/ToPoint）。</summary>
    public PointEditor Form { get; }

    /// <summary>服务器地址空间根节点（parent 缺省 = Objects 目录）。</summary>
    public ObservableCollection<OpcUaTreeNode> Nodes { get; } = [];

    [ObservableProperty] private OpcUaTreeNode? _selectedNode;
    [ObservableProperty] private bool _isBrowsing;
    [ObservableProperty] private string _browseStatusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTypeWarning))]
    private string? _typeWarning;

    public bool HasTypeWarning => !string.IsNullOrEmpty(TypeWarning);

    /// <summary>首屏根层浏览任务（窗口加载即开始；测试可 await 以确保树已就绪）。</summary>
    public Task InitialLoad { get; }

    public OpcUaPointEditorViewModel(Guid deviceId, PointEditor form, IOpcUaNodeBrowser browser)
    {
        _deviceId = deviceId;
        Form = form;
        _browser = browser;
        InitialLoad = LoadRootsAsync();
    }

    /// <summary>加载服务器地址空间根层（刷新按钮也复用本命令）。</summary>
    [RelayCommand]
    private async Task LoadRootsAsync()
    {
        IsBrowsing = true;
        BrowseStatusText = "正在浏览服务器地址空间…";
        try
        {
            var result = await _browser.BrowseAsync(_deviceId, "", CancellationToken.None);
            if (result.IsFailure)
            {
                BrowseStatusText = $"浏览失败：{result.Error!.Message}";
                return;
            }

            Nodes.Clear();
            foreach (var node in result.Value!)
                Nodes.Add(CreateNode(node));
            BrowseStatusText = $"根节点 {Nodes.Count} 个；点击变量节点回填地址/类型/权限";
        }
        finally
        {
            IsBrowsing = false;
        }
    }

    private OpcUaTreeNode CreateNode(BrowseNode node) =>
        new(node.NodeId, node.Name, node.TypeName, node.IsVariable, node.Access, LoadChildrenAsync);

    private async Task LoadChildrenAsync(OpcUaTreeNode parent)
    {
        var result = await _browser.BrowseAsync(_deviceId, parent.NodeId, CancellationToken.None);
        parent.Children.Clear();
        if (result.IsFailure)
        {
            BrowseStatusText = $"浏览「{parent.Name}」失败：{result.Error!.Message}";
            return;
        }

        foreach (var child in result.Value!)
            parent.Children.Add(CreateNode(child));
    }

    /// <summary>点选变量节点回填表单（对齐 web OpcUaPointTable.onNodeClick）。</summary>
    partial void OnSelectedNodeChanged(OpcUaTreeNode? value)
    {
        if (value is null || !value.IsVariable)
            return;

        Form.Address = value.NodeId;
        if (string.IsNullOrWhiteSpace(Form.Name))
            Form.Name = value.Name;

        Form.Access = value.Access switch
        {
            "ReadWrite" => PointAccess.ReadWrite,
            "Write" => PointAccess.WriteOnly,
            _ => PointAccess.ReadOnly
        };

        // 服务器 DataType 可能超出网关支持的 11 种（Browse 把其余统一映射为 "Unknown"）。
        // 可映射 → 回填；不可映射 → 保留原类型并警告，由用户手动选择相近类型。
        if (Enum.TryParse<DataType>(value.TypeName, ignoreCase: true, out var dataType))
        {
            Form.DataType = dataType;
            TypeWarning = null;
        }
        else
        {
            TypeWarning = $"服务器数据类型「{(string.IsNullOrWhiteSpace(value.TypeName) ? "Unknown" : value.TypeName)}」" +
                          "不在网关支持列表（Bool/Byte/Int16/UInt16/Int32/UInt32/Int64/UInt64/Float/Double/String），请手动选择相近类型。";
        }
    }

    public bool Validate() => Form.Validate();

    public void Dispose()
    {
        // 浏览树无长生命周期资源；保留接口便于窗口关闭时统一清理
    }
}

/// <summary>
/// 地址空间树节点：对象节点首次展开时懒加载子节点（<see cref="Children"/> 先放占位项以显示展开箭头）。
/// </summary>
public sealed partial class OpcUaTreeNode : ObservableObject
{
    private static readonly OpcUaTreeNode Placeholder = new(isPlaceholder: true);

    private readonly Func<OpcUaTreeNode, Task> _loadChildren;
    private readonly bool _isPlaceholder;
    private bool _loaded;
    private bool _loading;

    private OpcUaTreeNode(bool isPlaceholder)
    {
        NodeId = "";
        Name = "加载中…";
        TypeName = "";
        IsVariable = false;
        Access = "";
        _loadChildren = _ => Task.CompletedTask;
        _isPlaceholder = isPlaceholder;
    }

    public OpcUaTreeNode(
        string nodeId, string name, string typeName, bool isVariable, string access,
        Func<OpcUaTreeNode, Task> loadChildren)
    {
        NodeId = nodeId;
        Name = name;
        TypeName = typeName;
        IsVariable = isVariable;
        Access = access;
        _loadChildren = loadChildren;

        // 对象节点先放占位项，否则 TreeViewItem 因无子项不显示展开箭头
        if (!isVariable)
            Children.Add(Placeholder);
    }

    public string NodeId { get; }
    public string Name { get; }
    public string TypeName { get; }
    public bool IsVariable { get; }
    public string Access { get; }

    public ObservableCollection<OpcUaTreeNode> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    /// <summary>树中显示文本：变量节点带类型与权限，对象节点仅名称。</summary>
    public string Display => IsVariable
        ? $"{Name}   [{TypeNameLabel} / {AccessLabel}]"
        : Name;

    public string TypeNameLabel => string.IsNullOrWhiteSpace(TypeName) ? "未知类型" : TypeName;

    public string AccessLabel => Access switch
    {
        "ReadWrite" => "读写",
        "Write" => "只写",
        "Read" => "只读",
        _ => Access
    };

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            _ = EnsureChildrenAsync();
    }

    private async Task EnsureChildrenAsync()
    {
        if (IsVariable || _isPlaceholder || _loaded || _loading)
            return;

        _loading = true;
        _loaded = true;
        try
        {
            await _loadChildren(this);
        }
        finally
        {
            _loading = false;
        }
    }
}
