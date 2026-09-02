using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NitroGateway.Domain.Devices;
using NitroGateway.Persistence;
using NitroGateway.Persistence.Security;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-073 D5 凭据落库测试（AC-4 / V-2 过滤名 CredentialPersistence）：
/// OPC UA 设备密码落 SQLite <c>ConnectionParams</c> 必须为密文（本实现带 ng1: 前缀），
/// 绝不出现明文；UserName 保留；读库后还原为内存明文供驱动使用；Modbus/S7 参数不受影响。
/// </summary>
public class CredentialPersistenceTests
{
    /// <summary>固定测试主密钥（≥32 字节）</summary>
    private const string TestKey = "test-credential-key-0123456789abcdef";

    /// <summary>临时文件库：按 M003 迁移结构建 devices 表（复用 SqliteDeviceRepositoryTests 同构表）</summary>
    private sealed class TempDeviceDb : IDisposable
    {
        public string ConnectionString { get; }
        private readonly string _path;

        public TempDeviceDb()
        {
            _path = Path.Combine(Path.GetTempPath(), $"ntg-cred-{Guid.NewGuid():N}.db");
            ConnectionString = $"Data Source={_path};Pooling=False";
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE devices (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Description TEXT NULL,
                    ProtocolName TEXT NOT NULL,
                    ProtocolDialect TEXT NULL,
                    Endpoint TEXT NOT NULL,
                    ConnectTimeoutMs INTEGER NOT NULL DEFAULT 3000,
                    RequestTimeoutMs INTEGER NOT NULL DEFAULT 5000,
                    RetryCount INTEGER NOT NULL DEFAULT 3,
                    Status TEXT NOT NULL,
                    ConnectionParams TEXT NULL,
                    UpdatedAt TEXT NOT NULL DEFAULT '',
                    IsDeleted INTEGER NOT NULL DEFAULT 0,
                    SiteId TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE points (
                    Id TEXT PRIMARY KEY,
                    DeviceId TEXT NOT NULL REFERENCES devices(Id),
                    Name TEXT NOT NULL,
                    Address TEXT NOT NULL,
                    Description TEXT NULL,
                    DataType TEXT NOT NULL,
                    Access TEXT NOT NULL,
                    Enabled INTEGER NOT NULL DEFAULT 1,
                    ScanIntervalMs INTEGER NOT NULL DEFAULT 0,
                    Deadband REAL NOT NULL DEFAULT 0,
                    ScaleFactor REAL NOT NULL DEFAULT 1.0,
                    ScaleOffset REAL NOT NULL DEFAULT 0,
                    MinLimit REAL NULL,
                    MaxLimit REAL NULL,
                    UpdatedAt TEXT NOT NULL DEFAULT '',
                    IsDeleted INTEGER NOT NULL DEFAULT 0
                );
                """;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    private static NitroGatewayDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<NitroGatewayDbContext>().UseSqlite(connectionString).Options);

    private static ICredentialProtector Protector() => new AesGcmCredentialProtector(TestKey);

    private static Device NewOpcUaDevice(Guid id, string password, bool withUserName = true)
    {
        var parameters = new Dictionary<string, object>
        {
            ["SecurityPolicy"] = "Basic256Sha256",
            ["SecurityMode"] = "SignAndEncrypt",
            [DeviceParamKey.UserName] = "opcuser",
            [DeviceParamKey.Password] = password
        };
        if (!withUserName) parameters.Remove(DeviceParamKey.UserName);
        return new Device
        {
            Id = id,
            Name = "OPC UA 设备",
            Protocol = ProtocolIdentifier.OpcUa,
            Connection = new DeviceConnection
            {
                Endpoint = "opc.tcp://127.0.0.1:4840",
                Parameters = parameters
            },
            Status = DeviceStatus.Unknown
        };
    }

    private static Device NewModbusDevice(Guid id)
        => new()
        {
            Id = id,
            Name = "Modbus 设备",
            Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
            Connection = new DeviceConnection
            {
                Endpoint = "127.0.0.1:502",
                Parameters = new Dictionary<string, object> { ["UnitId"] = 3 }
            },
            Status = DeviceStatus.Unknown
        };

    /// <summary>AC-4：保存含密码的 OPC UA 设备后，落库 ConnectionParams 不含明文、含密文前缀、UserName 保留。</summary>
    [Fact]
    public async Task Save_OpcUaWithPassword_StoredParamsHasNoPlaintext()
    {
        const string password = "SuperSecret-42";
        using var db = new TempDeviceDb();

        var id = Guid.NewGuid();
        {
            await using var ctx = CreateContext(db.ConnectionString);
            var repo = new SqliteDeviceRepository(ctx, Protector());
            var saved = await repo.SaveAsync(NewOpcUaDevice(id, password));
            Assert.True(saved.IsSuccess, saved.Error?.Message);
        }

        await using var readCtx = CreateContext(db.ConnectionString);
        var entity = await readCtx.Devices.AsNoTracking().FirstAsync(d => d.Id == id);
        Assert.False(string.IsNullOrEmpty(entity.ConnectionParams));
        Assert.DoesNotContain(password, entity.ConnectionParams);      // 绝无明文
        Assert.Contains("ng1:", entity.ConnectionParams);              // 本实现密文前缀
        Assert.Contains("opcuser", entity.ConnectionParams);           // UserName 保留（AC-4）
    }

    /// <summary>AC-4 配套：读库还原为内存明文，驱动路径可用；密码往返一致。</summary>
    [Fact]
    public async Task GetById_OpcUaWithPassword_DecryptsBackToPlaintext()
    {
        const string password = "RoundTrip-88";
        using var db = new TempDeviceDb();
        var id = Guid.NewGuid();
        {
            await using var ctx = CreateContext(db.ConnectionString);
            var repo = new SqliteDeviceRepository(ctx, Protector());
            await repo.SaveAsync(NewOpcUaDevice(id, password));
        }

        await using var ctx2 = CreateContext(db.ConnectionString);
        var repo2 = new SqliteDeviceRepository(ctx2, Protector());
        var result = await repo2.GetByIdAsync(id);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value!.Connection.Parameters.TryGetValue(DeviceParamKey.Password, out var pwd));
        Assert.Equal(password, pwd);
        Assert.True(result.Value.Connection.Parameters.TryGetValue(DeviceParamKey.UserName, out var user));
        // UserName 非秘密值，读取侧按仓库既有约定以 JsonElement 承载（ModbusDriverBase 同口径），
        // 断言取字符串形式，与驱动消费端兼容（OpcUaSecurityParameters.TryReadString 亦容忍两者）。
        Assert.Equal("opcuser", user?.ToString());
    }

    /// <summary>协议隔离：Modbus 设备参数不被加解密触碰（ADR-073 D1：协议参数互不污染）。</summary>
    [Fact]
    public async Task Save_ModbusDevice_ParamsStoredUnchanged()
    {
        using var db = new TempDeviceDb();
        var id = Guid.NewGuid();
        {
            await using var ctx = CreateContext(db.ConnectionString);
            var repo = new SqliteDeviceRepository(ctx, Protector());
            await repo.SaveAsync(NewModbusDevice(id));
        }

        await using var readCtx = CreateContext(db.ConnectionString);
        var entity = await readCtx.Devices.AsNoTracking().FirstAsync(d => d.Id == id);
        Assert.Contains("UnitId", entity.ConnectionParams);
        Assert.DoesNotContain("ng1:", entity.ConnectionParams);
    }

    /// <summary>密钥缺失时的 fail-fast：既有密文无法解密返回分类失败而非明文回写兜底（ADR-073 载荷墙）。</summary>
    [Fact]
    public async Task GetById_StoredCiphertextWithoutKey_FailsNotPlaintextFallback()
    {
        const string password = "NeedsKey-77";
        using var db = new TempDeviceDb();
        var id = Guid.NewGuid();
        {
            await using var ctx = CreateContext(db.ConnectionString);
            var repo = new SqliteDeviceRepository(ctx, Protector());
            await repo.SaveAsync(NewOpcUaDevice(id, password));
        }

        // 用未配置密钥的保护器读取：解密路径抛错 → 归类失败，禁止把密文当明文回写
        await using var ctx2 = CreateContext(db.ConnectionString);
        var repo2 = new SqliteDeviceRepository(ctx2, new AesGcmCredentialProtector((string?)null!));
        var result = await repo2.GetByIdAsync(id);

        Assert.True(result.IsFailure);
        Assert.Contains("CredentialKey", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>保护器单测：Protect/Unprotect 往返、空值、非本格式值不被解密。</summary>
    [Fact]
    public void Protector_RoundTrip_And_NonPrefixedPassthrough()
    {
        var protector = Protector();
        var cipher = protector.Protect("p@ssw0rd");
        Assert.StartsWith("ng1:", cipher);
        Assert.NotEqual("p@ssw0rd", cipher);
        Assert.Equal("p@ssw0rd", protector.Unprotect(cipher));
        Assert.Equal("", protector.Protect(""));            // 空串原样返回
        Assert.Equal("legacy-plain", protector.Unprotect("legacy-plain")); // 非本格式原样返回
    }

    /// <summary>密钥键名常量（与仓储/控制器口径一致，防拼写漂移）</summary>
    private static class DeviceParamKey
    {
        public const string UserName = "UserName";
        public const string Password = "Password";
    }
}
