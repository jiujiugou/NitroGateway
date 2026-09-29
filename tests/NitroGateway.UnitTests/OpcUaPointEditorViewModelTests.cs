using NitroGateway.Desktop.Services.Connectivity;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-070 层次1（桌面端）：OPC UA 点位「左树右表单」。
/// 覆盖：根层加载、变量节点点选回填（地址/类型/权限/名称）、对象节点忽略、
/// 服务器类型不支持时的警告与保留、对象节点懒加载子层、浏览失败提示。
/// </summary>
public sealed class OpcUaPointEditorViewModelTests
{
    private static BrowseNode Node(string id, string name, bool variable, string type = "", string access = "") => new()
    {
        NodeId = id,
        Name = name,
        TypeName = type,
        IsVariable = variable,
        Access = access
    };

    private static PointEditor Form() => new() { Id = Guid.NewGuid(), ProtocolName = "OPC UA" };

    [Fact]
    public async Task InitialLoad_populates_root_nodes_and_status()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] =
        [
            Node("ns=0;i=85", "Objects", false),
            Node("ns=3;i=1001", "Counter", true, "Int32", "Read")
        ];
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);

        await vm.InitialLoad;

        Assert.Equal(2, vm.Nodes.Count);
        Assert.False(vm.IsBrowsing);
        Assert.Equal([""], browser.Parents);   // 根层：parent 缺省为空串（Objects 目录）
        Assert.False(string.IsNullOrWhiteSpace(vm.BrowseStatusText));
    }

    [Fact]
    public async Task Selecting_variable_node_backfills_address_type_access_and_name()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] = [Node("ns=6;s=MyLevel", "MyLevel", true, "Double", "ReadWrite")];
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);
        await vm.InitialLoad;

        vm.SelectedNode = vm.Nodes[0];

        Assert.Equal("ns=6;s=MyLevel", vm.Form.Address);
        Assert.Equal("MyLevel", vm.Form.Name);
        Assert.Equal(DataType.Double, vm.Form.DataType);
        Assert.Equal(PointAccess.ReadWrite, vm.Form.Access);
        Assert.Null(vm.TypeWarning);
        Assert.False(vm.HasTypeWarning);
    }

    [Fact]
    public async Task Selecting_object_node_does_not_backfill()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] = [Node("ns=0;i=85", "Objects", false)];
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);
        await vm.InitialLoad;

        vm.SelectedNode = vm.Nodes[0];

        Assert.Equal("", vm.Form.Address);
        Assert.Null(vm.TypeWarning);
    }

    [Fact]
    public async Task Unsupported_server_type_warns_and_keeps_current_data_type()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] = [Node("ns=5;s=ImagePNG", "ImagePNG", true, "Unknown", "Read")];
        var form = Form();
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), form, browser);
        await vm.InitialLoad;

        vm.SelectedNode = vm.Nodes[0];

        Assert.Equal(DataType.Float, vm.Form.DataType); // 保留表单默认，等待用户手动选择
        Assert.Equal("ns=5;s=ImagePNG", vm.Form.Address);
        Assert.True(vm.HasTypeWarning);
        Assert.Contains("不在网关支持列表", vm.TypeWarning);
    }

    [Fact]
    public async Task Write_only_access_maps_to_WriteOnly()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] = [Node("ns=5;s=AccessLevelCurrentWrite", "AccessLevelCurrentWrite", true, "Int32", "Write")];
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);
        await vm.InitialLoad;

        vm.SelectedNode = vm.Nodes[0];

        Assert.Equal(PointAccess.WriteOnly, vm.Form.Access);
    }

    [Fact]
    public async Task Expanding_object_node_lazily_loads_children()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] = [Node("ns=3;s=85/0:Simulation", "Simulation", false)];
        browser.ByParent["ns=3;s=85/0:Simulation"] = [Node("ns=3;i=1001", "Counter", true, "Int32", "Read")];
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);
        await vm.InitialLoad;

        var root = vm.Nodes[0];
        root.IsExpanded = true;

        await TestWait.UntilAsync(() => root.Children.Any(c => c.NodeId == "ns=3;i=1001"));
        Assert.Contains("ns=3;s=85/0:Simulation", browser.Parents);

        vm.SelectedNode = root.Children.Single(c => c.NodeId == "ns=3;i=1001");
        Assert.Equal("ns=3;i=1001", vm.Form.Address);
        Assert.Equal(DataType.Int32, vm.Form.DataType);
    }

    [Fact]
    public async Task Browse_failure_reports_status_and_leaves_tree_empty()
    {
        var browser = new FakeBrowser
        {
            Failure = OperationResult<IReadOnlyList<BrowseNode>>.Failure(OperationalError.Communication("连接失败：拒绝访问"))
        };
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);

        await vm.InitialLoad;

        Assert.Empty(vm.Nodes);
        Assert.Contains("浏览失败", vm.BrowseStatusText);
    }

    [Fact]
    public async Task IsBrowsing_is_true_while_root_load_is_in_flight()
    {
        var browser = new FakeBrowser { Gate = new TaskCompletionSource() };
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);

        // 根层浏览挂起期间：UI 应处于加载态
        Assert.True(vm.IsBrowsing);

        browser.Gate.SetResult();
        await vm.InitialLoad;

        Assert.False(vm.IsBrowsing);
    }

    [Fact]
    public async Task LoadRoots_rerun_replaces_previous_nodes()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] = [Node("a", "A", false)];
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);
        await vm.InitialLoad;
        Assert.Single(vm.Nodes);

        browser.ByParent[""] = [Node("b", "B", false), Node("c", "C", false)];
        await vm.LoadRootsCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Nodes.Count);
        Assert.Equal("B", vm.Nodes[0].Name);
    }

    [Fact]
    public async Task Selecting_node_does_not_overwrite_existing_name()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] = [Node("ns=6;s=MyLevel", "MyLevel", true, "Double", "Read")];
        var form = Form();
        form.Name = "用户手填名称";
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), form, browser);
        await vm.InitialLoad;

        vm.SelectedNode = vm.Nodes[0];

        Assert.Equal("用户手填名称", vm.Form.Name);
        Assert.Equal("ns=6;s=MyLevel", vm.Form.Address);
    }

    [Fact]
    public async Task Type_warning_clears_after_selecting_a_supported_type()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] =
        [
            Node("ns=5;s=ImagePNG", "ImagePNG", true, "Unknown", "Read"),
            Node("ns=3;i=1001", "Counter", true, "Int32", "Read")
        ];
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);
        await vm.InitialLoad;

        vm.SelectedNode = vm.Nodes[0];
        Assert.True(vm.HasTypeWarning);

        vm.SelectedNode = vm.Nodes[1];
        Assert.False(vm.HasTypeWarning);
        Assert.Equal(DataType.Int32, vm.Form.DataType);
    }

    [Fact]
    public async Task Unknown_access_level_falls_back_to_ReadOnly()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] = [Node("ns=5;s=X", "X", true, "Double", "None")];
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);
        await vm.InitialLoad;

        vm.SelectedNode = vm.Nodes[0];

        Assert.Equal(PointAccess.ReadOnly, vm.Form.Access);
    }

    // ── 树节点：占位/懒加载/标签 ──

    [Fact]
    public async Task Object_node_shows_placeholder_until_expanded_then_replaces_it()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] = [Node("obj", "Simulation", false)];
        browser.ByParent["obj"] = [Node("v", "Counter", true, "Int32", "Read")];
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);
        await vm.InitialLoad;

        var root = vm.Nodes[0];
        // 未展开：占位项存在（TreeView 因此显示展开箭头），且不是真实子节点
        Assert.Single(root.Children);
        Assert.Equal("", root.Children[0].NodeId);

        root.IsExpanded = true;
        await TestWait.UntilAsync(() => root.Children.Any(c => c.NodeId == "v"));

        Assert.Single(root.Children); // 占位项已被真实子节点替换
    }

    [Fact]
    public async Task Expanding_object_node_twice_requests_children_once()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] = [Node("obj", "Simulation", false)];
        browser.ByParent["obj"] = [Node("v", "Counter", true, "Int32", "Read")];
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);
        await vm.InitialLoad;

        var root = vm.Nodes[0];
        root.IsExpanded = true;
        await TestWait.UntilAsync(() => root.Children.Any(c => c.NodeId == "v"));

        root.IsExpanded = false;
        root.IsExpanded = true;
        await Task.Delay(50);

        Assert.Equal(1, browser.Parents.Count(p => p == "obj"));
    }

    [Fact]
    public async Task Variable_node_never_loads_children()
    {
        var browser = new FakeBrowser();
        browser.ByParent[""] = [Node("ns=6;s=MyLevel", "MyLevel", true, "Double", "Read")];
        var vm = new OpcUaPointEditorViewModel(Guid.NewGuid(), Form(), browser);
        await vm.InitialLoad;

        var leaf = vm.Nodes[0];
        Assert.Empty(leaf.Children);

        leaf.IsExpanded = true;
        await Task.Delay(20);

        Assert.Empty(leaf.Children);
        Assert.Equal([""], browser.Parents); // 只发生过根层请求
    }

    [Fact]
    public void Node_labels_fall_back_for_empty_type_and_map_access()
    {
        var node = new OpcUaTreeNode("ns=1;s=X", "X", "", true, "ReadWrite", _ => Task.CompletedTask);

        Assert.Equal("未知类型", node.TypeNameLabel);
        Assert.Equal("读写", node.AccessLabel);
        Assert.Equal("X   [未知类型 / 读写]", node.Display);
    }

    private sealed class FakeBrowser : IOpcUaNodeBrowser
    {
        public Dictionary<string, IReadOnlyList<BrowseNode>> ByParent { get; } = new();

        public OperationResult<IReadOnlyList<BrowseNode>>? Failure { get; set; }

        public List<string> Parents { get; } = [];

        /// <summary>非 null 时挂起浏览，用于断言「加载中」状态。</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(
            Guid deviceId, string parentNodeId, CancellationToken ct = default)
        {
            Parents.Add(parentNodeId);
            if (Gate is not null)
                await Gate.Task;
            if (Failure is not null)
                return Failure;

            var nodes = ByParent.TryGetValue(parentNodeId, out var value)
                ? value
                : (IReadOnlyList<BrowseNode>)Array.Empty<BrowseNode>();
            return OperationResult<IReadOnlyList<BrowseNode>>.Success(nodes);
        }
    }
}
