using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
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
        protected override MasterNodeManager CreateMasterNodeManager(
            IServerInternal server, ApplicationConfiguration configuration)
        {
            var simulation = new SimulationNodeManager(server, configuration);
            return new MasterNodeManager(server, configuration, null, new INodeManager[] { simulation });
        }
    }

    private sealed class SimulationNodeManager : CustomNodeManager2
    {
        private const string NamespaceUri = "urn:test:simulation";

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
            parent.AddChild(variable);
            nodes.Add(variable);
        }
    }
}
