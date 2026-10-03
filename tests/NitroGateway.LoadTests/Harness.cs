using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Persistence;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.LoadTests;

/// <summary>
/// 单场景压测宿主：用真实采集链路（DeviceCollector→Pipeline→DataDispatcher→MeasurementWriteHost）
/// 装配 DI，仅把 IDeviceReader / IDeviceManager / 健康监控 / Store / Buffer 替换为可控替身。
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    private readonly string? _dbPath;

    private Harness(
        ServiceProvider provider,
        FakeDeviceReader reader,
        FakeMeasurementStore? fakeStore,
        string? dbPath,
        MeasurementWriteHost writeHost,
        SinkDispatcher sinks)
    {
        Provider = provider;
        Reader = reader;
        FakeStore = fakeStore;
        _dbPath = dbPath;
        WriteHost = writeHost;
        Sinks = sinks;
    }

    public ServiceProvider Provider { get; }

    public FakeDeviceReader Reader { get; }

    public FakeMeasurementStore? FakeStore { get; }

    public MeasurementWriteHost WriteHost { get; }

    public SinkDispatcher Sinks { get; }

    public static Harness Create(LoadOptions options, IReadOnlyList<Device> devices, int concurrency)
    {
        var reader = new FakeDeviceReader(devices);

        FakeMeasurementStore? fakeStore = null;
        IMeasurementStore store;
        string? dbPath = null;
        if (options.Mode == StoreMode.Fake)
        {
            fakeStore = new FakeMeasurementStore();
            store = fakeStore;
        }
        else
        {
            dbPath = Path.Combine(Path.GetTempPath(), $"nitro-load-{Guid.NewGuid():N}.db");
            var conn = $"Data Source={dbPath}";
            MigrationRunner.Run(conn);
            store = new SqliteMeasurementStore(conn);
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Collection:IntervalMs"] = "1000",
                ["Collection:MaxConcurrency"] = concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Collection:DeadbandHeartbeatMs"] = "300000",
                ["Forwarder:Channels"] = "mqtt",
                ["Site:Id"] = "loadtest"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddSingleton<IDeviceManager>(new FakeDeviceManager(devices));
        services.AddSingleton<IDeviceHealthMonitor>(new FakeDeviceHealthMonitor());
        services.AddSingleton<IForwardBuffer>(new NullForwardBuffer());
        services.AddSingleton<IMeasurementStore>(store);
        services.AddNitroCollection(configuration);
        // 最后注册：覆盖 AddNitroCollection 里的真实 DeviceReader（GetRequiredService 取最后一个）
        services.AddSingleton<IDeviceReader>(reader);

        var provider = services.BuildServiceProvider();
        return new Harness(
            provider,
            reader,
            fakeStore,
            dbPath,
            provider.GetRequiredService<MeasurementWriteHost>(),
            provider.GetRequiredService<SinkDispatcher>());
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await WriteHost.StartAsync(ct);
        await Sinks.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await Sinks.StopAsync(ct);
        await WriteHost.StopAsync(ct);
    }

    /// <summary>当前已落库点数：假 Store 读计数，真实 SQLite 查 COUNT(*)。</summary>
    public long StorePointCount()
    {
        if (FakeStore is not null)
            return FakeStore.PointCount;
        return QuerySqliteCount(_dbPath!);
    }

    private static long QuerySqliteCount(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM measurements";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        if (_dbPath is not null)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(_dbPath + suffix); } catch { /* 临时文件清理失败忽略 */ }
            }
        }
    }
}
