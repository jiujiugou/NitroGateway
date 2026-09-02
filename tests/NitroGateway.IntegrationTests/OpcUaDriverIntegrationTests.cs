using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols.OpcUa;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using Xunit;

// 与 OpcUaDriver.cs 一致：SDK 1.5 的同步 Validate / CertificateValidator 标记过时但稳定可用，
// 压 CS0618 避免噪音（升级 SDK 大版本时再迁移）。
#pragma warning disable CS0618

namespace NitroGateway.IntegrationTests;

/// <summary>
/// ADR-019：OPC UA 驱动进程内冒烟测试。本机未装 Prosys、Docker daemon 不可用，
/// 改用 OPC Foundation Server SDK（1.5.378.145）自起进程内服务器，零外部依赖。
/// 覆盖全流程：连接 → 读取 → 写入 → Ping → 断链（主动 Disconnect）→ 重连 →
/// 服务器硬断链（Stop）→ 重启（同端口）→ 重连。
///
/// <para>服务器地址空间：ns=2 下模拟变量 i=1001 Int32 / i=1002 Float / i=1003 Bool / i=1004 String，
/// 读写权限 CurrentReadOrWrite，模拟真实 PLC 点位表（与 PointList 批量生成默认起始地址 ns=2;i=1001 呼应）。</para>
/// </summary>
public sealed class OpcUaDriverIntegrationTests
{
    [Fact]
    public async Task Connect_ReadWrite_Ping_DisconnectReconnect_Works()
    {
        await using var scope = await SimulationServerScope.StartAsync();
        var driver = CreateDriver(scope.Port);

        var connect = await driver.ConnectAsync();
        Assert.True(connect.IsSuccess, connect.Error?.Message);
        try
        {
            var intPoint = Point("Int1", "ns=2;i=1001", DataType.Int32);
            var floatPoint = Point("Float1", "ns=2;i=1002", DataType.Float);

            // 读初始值（服务端预设）
            var r = await driver.ReadAsync(intPoint, CancellationToken.None);
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.Equal(42, r.Value!.Value);

            // 写 Int32 → 回读验证
            var w = await driver.WriteAsync(intPoint, 12345, CancellationToken.None);
            Assert.True(w.IsSuccess, w.Error?.Message);
            r = await driver.ReadAsync(intPoint, CancellationToken.None);
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.Equal(12345, r.Value!.Value);

            // 写 Float → 回读验证
            w = await driver.WriteAsync(floatPoint, 22.5, CancellationToken.None);
            Assert.True(w.IsSuccess, w.Error?.Message);
            r = await driver.ReadAsync(floatPoint, CancellationToken.None);
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.Equal(22.5, r.Value!.Value);

            // 链路 Ping
            var ping = await driver.PingAsync();
            Assert.True(ping.IsSuccess, ping.Error?.Message);

            // 断链（主动）→ 重连 → 回读（写过的值仍保留，证明会话重建成功）
            await driver.DisconnectAsync();
            var reconnect = await driver.ConnectAsync();
            Assert.True(reconnect.IsSuccess, reconnect.Error?.Message);
            r = await driver.ReadAsync(intPoint, CancellationToken.None);
            Assert.True(r.IsSuccess, r.Error?.Message);
            Assert.Equal(12345, r.Value!.Value);
        }
        finally
        {
            await driver.DisconnectAsync();
        }
    }

    [Fact]
    public async Task ServerStop_HardDisconnect_ServerRestart_DriverReconnects()
    {
        await using var scope = await SimulationServerScope.StartAsync();
        var driver = CreateDriver(scope.Port);
        var point = Point("Int1", "ns=2;i=1001", DataType.Int32);

        Assert.True((await driver.ConnectAsync()).IsSuccess);
        Assert.True((await driver.ReadAsync(point, CancellationToken.None)).IsSuccess);

        // 服务器硬断链（Stop）：读应立即失败并复位 Faulted
        await scope.StopServerAsync();
        var fail = await driver.ReadAsync(point, CancellationToken.None);
        Assert.False(fail.IsSuccess);

        // 服务器同端口重启 → 驱动重连 → 回读
        await scope.StartServerAsync();
        var reconnect = await driver.ConnectAsync();
        Assert.True(reconnect.IsSuccess, reconnect.Error?.Message);
        var r = await driver.ReadAsync(point, CancellationToken.None);
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(42, r.Value!.Value);

        await driver.DisconnectAsync();
    }

    /// <summary>
    /// AC-2/AC-4（ADR-072 层3 会话自愈）：服务器硬断链后，KeepAlive 检测触发
    /// <c>SessionReconnectHandler</c> 自愈；断链窗口内驱动保持 <c>Connected</c>（D5），
    /// 服务器同端口重启后**不调用 ConnectAsync** 即自动恢复读取（无需上层整轮重建）。
    /// </summary>
    [Fact]
    public async Task ServerStop_KeepAliveSelfHeal_RecoversWithoutManualReconnect()
    {
        await using var scope = await SimulationServerScope.StartAsync();
        var driver = CreateDriver(scope.Port);
        var point = Point("Int1", "ns=2;i=1001", DataType.Int32);
        Assert.True((await driver.ConnectAsync()).IsSuccess);
        Assert.True((await driver.EnsureSubscriptionAsync([point], 200)).IsSuccess);
        Assert.True(driver.IsSubscriptionActive);

        try
        {
            // 服务器硬断链：此后不再手动 ConnectAsync，自愈接管"已连接后的断线"（D2）
            await scope.StopServerAsync();

            // 等 KeepAlive 检测到断链并启动自愈（最多 15s）；期间不做读（避免读失败先置 Faulted
            // 抢在自愈前），驱动状态保持 Connected
            var started = await WaitUntilAsync(TimeSpan.FromSeconds(15), () => driver.IsReconnectActiveForTesting);
            Assert.True(started, "KeepAlive 未在预期时间内触发会话自愈");
            Assert.Equal(DriverState.Connected, driver.State);

            // 服务器同端口重启 → 轮询读直至恢复（自愈完成自动续采），全程不调用 ConnectAsync
            await scope.StartServerAsync();
            var recovered = await WaitUntilAsync(TimeSpan.FromSeconds(30), async () =>
            {
                var r = await driver.ReadAsync(point, CancellationToken.None);
                return r.IsSuccess;
            });
            Assert.True(recovered, "服务器重启后会话自愈未在预期时间内恢复读取");
            Assert.Equal(DriverState.Connected, driver.State);
        }
        finally
        {
            await driver.DisconnectAsync();
        }
    }

    // ── ADR-070 层次1：节点浏览（Browse）──

    [Fact]
    public async Task Browse_Root_ReturnsSimulationFolder()
    {
        await using var scope = await SimulationServerScope.StartAsync();
        var driver = CreateDriver(scope.Port);
        Assert.True((await driver.ConnectAsync()).IsSuccess);
        try
        {
            // parent 缺省 = Objects 目录（i=85）：应能看到 ns=2 下的 Simulation 文件夹
            var result = await driver.BrowseAsync("", CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error?.Message);

            var sim = result.Value!.FirstOrDefault(n => n.NodeId == "ns=2;i=5001");
            Assert.NotNull(sim);
            Assert.Equal("Simulation", sim!.Name);
            Assert.False(sim.IsVariable);
            Assert.Equal("", sim.TypeName);
            Assert.Equal("", sim.Access);
        }
        finally
        {
            await driver.DisconnectAsync();
        }
    }

    [Fact]
    public async Task Browse_Folder_ReturnsVariablesWithTypeAndAccess()
    {
        await using var scope = await SimulationServerScope.StartAsync();
        var driver = CreateDriver(scope.Port);
        Assert.True((await driver.ConnectAsync()).IsSuccess);
        try
        {
            // 浏览 Simulation 文件夹（i=5001）→ 4 个变量；NodeId 序列化格式可直接回填点位地址
            var result = await driver.BrowseAsync("ns=2;i=5001", CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error?.Message);

            var nodes = result.Value!;
            Assert.Equal(4, nodes.Count);

            var intVar = nodes.First(n => n.NodeId == "ns=2;i=1001");
            Assert.Equal("Int32Var", intVar.Name);
            Assert.True(intVar.IsVariable);
            Assert.Equal("Int32", intVar.TypeName);
            Assert.Equal("ReadWrite", intVar.Access);

            Assert.Equal("Float", nodes.First(n => n.NodeId == "ns=2;i=1002").TypeName);
            Assert.Equal("Bool", nodes.First(n => n.NodeId == "ns=2;i=1003").TypeName);
            Assert.Equal("String", nodes.First(n => n.NodeId == "ns=2;i=1004").TypeName);
        }
        finally
        {
            await driver.DisconnectAsync();
        }
    }

    [Fact]
    public async Task Browse_InvalidParent_ReturnsValidationError_AndKeepsConnected()
    {
        await using var scope = await SimulationServerScope.StartAsync();
        var driver = CreateDriver(scope.Port);
        Assert.True((await driver.ConnectAsync()).IsSuccess);
        try
        {
            // 非法父地址 → OperationResult.Validation（Error.Code = "ValidationError"），不置 Faulted
            var result = await driver.BrowseAsync("not-an-address", CancellationToken.None);
            Assert.True(result.IsFailure);
            Assert.Equal("ValidationError", result.Error!.Code);
            Assert.Equal(DriverState.Connected, driver.State);

            // 浏览失败不影响会话：仍可正常读取
            var r = await driver.ReadAsync(Point("Int1", "ns=2;i=1001", DataType.Int32), CancellationToken.None);
            Assert.True(r.IsSuccess, r.Error?.Message);
        }
        finally
        {
            await driver.DisconnectAsync();
        }
    }

    // ── ADR-071：OPC UA Subscription / MonitoredItem 推送采集 ──

    /// <summary>AC-2：按 enabled 点位创建订阅，首次收到各点位初始值（服务端当前值推送）。</summary>
    [Fact]
    public async Task Subscription_Create_ReceivesInitialValues()
    {
        await using var scope = await SimulationServerScope.StartAsync();
        var driver = CreateDriver(scope.Port);
        Assert.True((await driver.ConnectAsync()).IsSuccess);

        var intPoint = Point("Int1", "ns=2;i=1001", DataType.Int32);
        var floatPoint = Point("Float1", "ns=2;i=1002", DataType.Float);
        var received = new ConcurrentQueue<RawPointValue>();
        var gotBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.ValuesReceived += values =>
        {
            foreach (var v in values) received.Enqueue(v);
            gotBatch.TrySetResult();
            return Task.CompletedTask;
        };

        try
        {
            var ensure = await driver.EnsureSubscriptionAsync([intPoint, floatPoint], 100);
            Assert.True(ensure.IsSuccess, ensure.Error?.Message);
            Assert.True(driver.IsSubscriptionActive);

            await gotBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(received, v => v.Point.Id == intPoint.Id && v.Value!.Equals(42));
            Assert.Contains(received, v => v.Point.Id == floatPoint.Id && (double)v.Value! == 3.14);
        }
        finally
        {
            await driver.DisconnectAsync();
        }
    }

    /// <summary>AC-2：服务端改值（经订阅发布周期）→ 客户端收到变更通知（Good 转 RawPointValue）。</summary>
    [Fact]
    public async Task Subscription_ServerValueChange_ReceivesNotification()
    {
        await using var scope = await SimulationServerScope.StartAsync();
        var driver = CreateDriver(scope.Port);
        Assert.True((await driver.ConnectAsync()).IsSuccess);

        var intPoint = Point("Int1", "ns=2;i=1001", DataType.Int32);
        var got777 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.ValuesReceived += values =>
        {
            foreach (var v in values)
            {
                if (v.Point.Id == intPoint.Id && v.Value!.Equals(777))
                    got777.TrySetResult();
            }
            return Task.CompletedTask;
        };

        try
        {
            Assert.True((await driver.EnsureSubscriptionAsync([intPoint], 100)).IsSuccess);

            // 服务端直接改值 + ClearChangeMasks（GitHub OPCFoundation#1809：不调则订阅客户端收不到通知）
            scope.SetVariableValue(1001, 777);

            await got777.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(driver.IsSubscriptionActive);
        }
        finally
        {
            await driver.DisconnectAsync();
        }
    }

    /// <summary>AC-3：服务端置 Bad → 客户端收到非 Good 通知但不产值；恢复 Good 后再次产值（证明过滤而非订阅失效）。</summary>
    [Fact]
    public async Task Subscription_BadStatus_DoesNotProduceValue_AndRecoversOnGood()
    {
        await using var scope = await SimulationServerScope.StartAsync();
        var driver = CreateDriver(scope.Port);
        Assert.True((await driver.ConnectAsync()).IsSuccess);

        var intPoint = Point("Int1", "ns=2;i=1001", DataType.Int32);
        var received = new ConcurrentQueue<RawPointValue>();
        var gotInitial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotRecover = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.ValuesReceived += values =>
        {
            foreach (var v in values)
            {
                received.Enqueue(v);
                if (v.Point.Id == intPoint.Id && v.Value!.Equals(42))
                    gotInitial.TrySetResult();
                if (v.Point.Id == intPoint.Id && v.Value!.Equals(999))
                    gotRecover.TrySetResult();
            }
            return Task.CompletedTask;
        };

        try
        {
            Assert.True((await driver.EnsureSubscriptionAsync([intPoint], 100)).IsSuccess);
            await gotInitial.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var countBeforeBad = received.Count(v => v.Point.Id == intPoint.Id);

            // 服务端置 Bad（模拟点位故障）：即便订阅收到非 Good 通知，驱动也不得产值
            scope.SetVariableBad(1001);
            await Task.Delay(800); // 越过一个发布周期
            Assert.Equal(countBeforeBad, received.Count(v => v.Point.Id == intPoint.Id));

            // 恢复 Good + 新值 → 订阅仍存活并产值（Bad 时无值是因为过滤，而非订阅失效）
            scope.SetVariableValue(1001, 999);
            await gotRecover.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(received, v => v.Point.Id == intPoint.Id && v.Value!.Equals(999));
        }
        finally
        {
            await driver.DisconnectAsync();
        }
    }

    /// <summary>AC-5：重复 Ensure/Stop 幂等不抛；订阅期间读写经同一闸门仍可用；Disconnect 删除订阅。</summary>
    [Fact]
    public async Task Subscription_RepeatEnsureStop_Idempotent_AndDisconnectDeletes()
    {
        await using var scope = await SimulationServerScope.StartAsync();
        var driver = CreateDriver(scope.Port);
        Assert.True((await driver.ConnectAsync()).IsSuccess);
        var point = Point("Int1", "ns=2;i=1001", DataType.Int32);

        try
        {
            Assert.True((await driver.EnsureSubscriptionAsync([point], 100)).IsSuccess);
            Assert.True(driver.IsSubscriptionActive);

            // 同签名重复 Ensure → 幂等复用，不重建不抛
            Assert.True((await driver.EnsureSubscriptionAsync([point], 100)).IsSuccess);
            Assert.True(driver.IsSubscriptionActive);

            // 订阅生效期间 Read/Write 继续可用（同一 _gate 串行）
            var read = await driver.ReadAsync(point);
            Assert.True(read.IsSuccess, read.Error?.Message);
            var write = await driver.WriteAsync(point, 123);
            Assert.True(write.IsSuccess, write.Error?.Message);

            // Stop 幂等
            Assert.True((await driver.StopSubscriptionAsync()).IsSuccess);
            Assert.False(driver.IsSubscriptionActive);
            Assert.True((await driver.StopSubscriptionAsync()).IsSuccess);

            // 重新激活 → Disconnect 删除订阅并复位状态
            Assert.True((await driver.EnsureSubscriptionAsync([point], 100)).IsSuccess);
            Assert.True(driver.IsSubscriptionActive);
            await driver.DisconnectAsync();
            Assert.False(driver.IsSubscriptionActive);
        }
        finally
        {
            await driver.DisconnectAsync();
        }
    }

    // ── Helpers ──

    private static DevicePoint Point(string name, string address, DataType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Address = address,
        DataType = type
    };

    private static OpcUaDriver CreateDriver(int port) => new(
        new DeviceConnection
        {
            Endpoint = $"opc.tcp://127.0.0.1:{port}",
            ConnectTimeoutMs = 5000,
            RequestTimeoutMs = 5000
        },
        NullLogger<OpcUaDriver>.Instance);

    private static async Task<bool> WaitUntilAsync(TimeSpan timeout, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(250);
        }
        return condition();
    }

    private static async Task<bool> WaitUntilAsync(TimeSpan timeout, Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(250);
        }
        return await condition();
    }

    /// <summary>
    /// 进程内 OPC UA 服务器作用域：动态端口 + 独立 PKI 目录，支持停止后同端口重启。
    /// </summary>
    public sealed class SimulationServerScope : IAsyncDisposable
    {
        private readonly int _port;
        private readonly string _pkiRoot;
        private SimulationServer? _server;

        public int Port => _port;

        private SimulationServerScope(int port, string pkiRoot)
        {
            _port = port;
            _pkiRoot = pkiRoot;
            foreach (var dir in new[] { "own", "trusted", "issuers", "rejected" })
                Directory.CreateDirectory(Path.Combine(_pkiRoot, dir));
        }

        public static async Task<SimulationServerScope> StartAsync()
        {
            var pkiRoot = Path.Combine(Path.GetTempPath(), "nitro-opcua-pki-" + Guid.NewGuid().ToString("N"));
            var scope = new SimulationServerScope(FindFreePort(), pkiRoot);
            await scope.StartServerAsync();
            return scope;
        }

        public async Task StartServerAsync()
        {
            var config = BuildConfiguration();
            await config.Validate(ApplicationType.Server);
            // ADR-019：进程内 Server 必须先生成应用证书。None 安全策略下 CreateSession 仍会走
            // CertificateValidator.ValidateDomains → GetDomainsFromCertificate(serverCertificate)，
            // 无证书时为 null → NRE → 被包装成 BadUnexpectedError[80010000]（实测根因）。
            var app = new ApplicationInstance
            {
                ApplicationName = "NitroGateway Simulation",
                ApplicationType = ApplicationType.Server,
                ApplicationConfiguration = config
            };
            await app.CheckApplicationInstanceCertificates(silent: true);
            var server = new SimulationServer();
            await server.StartAsync(config, CancellationToken.None);
            _server = server;
        }

        public async Task StopServerAsync()
        {
            if (_server is null) return;
            var server = _server;
            _server = null;
            await server.StopAsync(CancellationToken.None);
        }

        /// <summary>服务端直接改变量值并触发订阅通知（ADR-071 订阅集成测试用）。</summary>
        public void SetVariableValue(uint id, object value) => _server?.NodeManager?.SetVariableValue(id, value);

        /// <summary>服务端直接将变量置 Bad（模拟点位故障），ADR-071 非 Good 不产值测试用。</summary>
        public void SetVariableBad(uint id) => _server?.NodeManager?.SetVariableBad(id);

        public async ValueTask DisposeAsync()
        {
            await StopServerAsync();
            try { Directory.Delete(_pkiRoot, true); } catch { }
        }

        private ApplicationConfiguration BuildConfiguration()
        {
            var host = Dns.GetHostName();
            return new ApplicationConfiguration
            {
                ApplicationName = "NitroGateway Simulation",
                ApplicationUri = Utils.Format("urn:{0}:NitroGatewaySimulation", host),
                ProductUri = "urn:test:product",
                ApplicationType = ApplicationType.Server,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(_pkiRoot, "own"),
                        SubjectName = "CN=NitroGatewaySimulation, DC=localhost"
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(_pkiRoot, "trusted")
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(_pkiRoot, "issuers")
                    },
                    RejectedCertificateStore = new CertificateStoreIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(_pkiRoot, "rejected")
                    },
                    AutoAcceptUntrustedCertificates = true,
                    AddAppCertToTrustedStore = true,
                    MinimumCertificateKeySize = 2048
                },
                TransportQuotas = new TransportQuotas
                {
                    OperationTimeout = 10000,
                    MaxMessageSize = 65536,
                    MaxStringLength = 65536
                },
                ServerConfiguration = new ServerConfiguration
                {
                    BaseAddresses = { $"opc.tcp://127.0.0.1:{_port}" },
                    SecurityPolicies =
                    {
                        new ServerSecurityPolicy
                        {
                            SecurityMode = MessageSecurityMode.None,
                            SecurityPolicyUri = SecurityPolicies.None
                        }
                    },
                    UserTokenPolicies =
                    {
                        new UserTokenPolicy(UserTokenType.Anonymous)
                    }
                },
                CertificateValidator = new CertificateValidator()
            };
        }

        private static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    /// <summary>仿真服务器：注入自定义 NodeManager，暴露 ns=2 下的读写模拟变量。</summary>
    private sealed class SimulationServer : StandardServer
    {
        public SimulationNodeManager? NodeManager { get; private set; }

        protected override MasterNodeManager CreateMasterNodeManager(
            IServerInternal server, ApplicationConfiguration configuration)
        {
            var simulation = new SimulationNodeManager(server, configuration);
            NodeManager = simulation;
            return new MasterNodeManager(server, configuration, null, new INodeManager[] { simulation });
        }
    }

    private sealed class SimulationNodeManager : CustomNodeManager2
    {
        private const string NamespaceUri = "urn:test:simulation";
        private readonly Dictionary<uint, BaseDataVariableState> _variables = [];

        public SimulationNodeManager(IServerInternal server, ApplicationConfiguration configuration)
            : base(server, configuration, new[] { NamespaceUri })
        {
        }

        protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
        {
            var nodes = new NodeStateCollection();

            var folder = new FolderState(null)
            {
                NodeId = new NodeId(5001, NamespaceIndex),
                BrowseName = new QualifiedName("Simulation", NamespaceIndex),
                DisplayName = new LocalizedText("Simulation")
            };
            folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            nodes.Add(folder);

            AddVariable(nodes, folder, 1001, "Int32Var", DataTypeIds.Int32, new Variant(42));
            AddVariable(nodes, folder, 1002, "FloatVar", DataTypeIds.Float, new Variant(3.14f));
            AddVariable(nodes, folder, 1003, "BoolVar", DataTypeIds.Boolean, new Variant(true));
            AddVariable(nodes, folder, 1004, "StringVar", DataTypeIds.String, new Variant("hello"));

            return nodes;
        }

        private void AddVariable(NodeStateCollection nodes, NodeState parent, uint id, string name, NodeId dataType, Variant value)
        {
            var variable = new BaseDataVariableState(parent)
            {
                NodeId = new NodeId(id, NamespaceIndex),
                BrowseName = new QualifiedName(name, NamespaceIndex),
                DisplayName = new LocalizedText(name),
                DataType = dataType,
                Value = value,
                AccessLevel = AccessLevels.CurrentReadOrWrite,
                UserAccessLevel = AccessLevels.CurrentReadOrWrite,
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow
            };
            _variables[id] = variable;
            parent.AddChild(variable);
            nodes.Add(variable);
        }

        /// <summary>服务端直接改值并清变更掩码（模拟 PLC 更新）。GitHub OPCFoundation#1809：
        /// 直接改节点后必须调 <see cref="NodeState.ClearChangeMasks"/>，否则订阅客户端收不到通知。</summary>
        public void SetVariableValue(uint id, object value)
        {
            if (!_variables.TryGetValue(id, out var variable))
                return;
            variable.Value = new Variant(value);
            variable.StatusCode = StatusCodes.Good;
            variable.Timestamp = DateTime.UtcNow;
            variable.ClearChangeMasks(SystemContext, false);
        }

        /// <summary>服务端直接将变量置 Bad（模拟点位故障）；订阅客户端应收到非 Good 通知而驱动不产值。</summary>
        public void SetVariableBad(uint id)
        {
            if (!_variables.TryGetValue(id, out var variable))
                return;
            variable.StatusCode = StatusCodes.BadOutOfService;
            variable.Timestamp = DateTime.UtcNow;
            variable.ClearChangeMasks(SystemContext, false);
        }
    }
}
