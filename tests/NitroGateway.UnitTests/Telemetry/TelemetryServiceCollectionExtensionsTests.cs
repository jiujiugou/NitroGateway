using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Telemetry;
using NitroGateway.Telemetry.Tracing;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace NitroGateway.UnitTests.Telemetry;

/// <summary>
/// AddNitroTelemetry 追踪接线冒烟测试（ADR-056）：
/// Enabled + 非 None 导出器 → TracerProvider 注册且全局 ActivitySource 被采样；
/// 关闭 / None → 不注册 TracerProvider（保持 dormant，与 ADR-009 预留状态一致）。
/// </summary>
public class TelemetryServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string key, string? value)[] items)
        => new ConfigurationBuilder().AddInMemoryCollection(
            items.ToDictionary(i => i.key, i => i.value)).Build();

    private static async Task<ServiceProvider> StartHostedAsync(IServiceCollection services)
    {
        var sp = services.BuildServiceProvider();
        foreach (var hosted in sp.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
        return sp;
    }

    private static async Task<ServiceProvider> StartFileProviderAsync(
        string dir, params (string key, string? value)[] overrides)
    {
        var items = new List<(string key, string? value)>
        {
            ("Telemetry:Tracing:Enabled", "true"),
            ("Telemetry:Tracing:Exporter", "File"),
            ("Telemetry:Tracing:LogDirectory", dir)
        };
        items.AddRange(overrides);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroTelemetry(Config(items.ToArray()), "test-service");

        var sp = services.BuildServiceProvider();
        foreach (var hosted in sp.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
        return sp;
    }

    private static void ExportSpans(TracerProvider provider, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using (var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.Forward))
            {
                activity?.SetTag(GatewayActivityTags.DeviceId, $"d-{i}");
            }
        }
        provider.ForceFlush();
        provider.Dispose();
    }

    [Fact]
    public async Task Enabled_RegistersTracerProvider_AndSamplesGlobalSource()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroTelemetry(Config(
            ("Telemetry:Tracing:Enabled", "true"),
            ("Telemetry:Tracing:Exporter", "Console")), "test-service");

        await using var sp = await StartHostedAsync(services);
        try
        {
            var provider = sp.GetRequiredService<TracerProvider>();
            Assert.NotNull(provider);

            // 已把 GatewayActivitySource 接入 TracerProvider：StartActivity 应返回被采样 span
            using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.CollectRound);
            Assert.NotNull(activity);
            Assert.True(activity.IsAllDataRequested);
            Assert.Equal(GatewayActivities.CollectRound, activity.OperationName);
        }
        finally
        {
            foreach (var hosted in sp.GetServices<IHostedService>())
            {
                await hosted.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Enabled_SpanCarriesTags_FromConstants()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroTelemetry(Config(
            ("Telemetry:Tracing:Enabled", "true"),
            ("Telemetry:Tracing:Exporter", "Console")), "test-service");

        await using var sp = await StartHostedAsync(services);
        try
        {
            using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.CollectDevice);
            activity?.SetTag(GatewayActivityTags.DeviceId, "d-1");

            Assert.NotNull(activity);
            Assert.Equal("d-1", activity.GetTagItem(GatewayActivityTags.DeviceId));
        }
        finally
        {
            foreach (var hosted in sp.GetServices<IHostedService>())
            {
                await hosted.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Disabled_DoesNotRegisterTracerProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroTelemetry(Config(("Telemetry:Tracing:Enabled", "false")), "test-service");

        await using var sp = await StartHostedAsync(services);
        try
        {
            Assert.Null(sp.GetService<TracerProvider>());
        }
        finally
        {
            foreach (var hosted in sp.GetServices<IHostedService>())
            {
                await hosted.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task ExporterNone_DoesNotRegisterTracerProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroTelemetry(Config(("Telemetry:Tracing:Exporter", "None")), "test-service");

        await using var sp = await StartHostedAsync(services);
        try
        {
            Assert.Null(sp.GetService<TracerProvider>());
        }
        finally
        {
            foreach (var hosted in sp.GetServices<IHostedService>())
            {
                await hosted.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task ExporterFile_WritesFinishedSpans_ToJsonl()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nitro-traces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroTelemetry(Config(
            ("Telemetry:Tracing:Enabled", "true"),
            ("Telemetry:Tracing:Exporter", "File"),
            ("Telemetry:Tracing:LogDirectory", dir)), "test-service");

        var sp = services.BuildServiceProvider();
        try
        {
            foreach (var hosted in sp.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None);
            }

            var provider = sp.GetRequiredService<TracerProvider>();
            using (var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.Forward))
            {
                Assert.NotNull(activity);
                activity!.SetTag(GatewayActivityTags.DeviceId, "d-1");
            }

            // SimpleActivityExportProcessor 在 span 结束时同步导出；ForceFlush + Dispose 关闭写入器保证落盘
            provider.ForceFlush();
            provider.Dispose();

            var file = Directory.GetFiles(dir, "traces-*.jsonl").Single();
            var json = File.ReadAllText(file);
            Assert.Contains("\"name\":\"Forward\"", json);
            Assert.Contains("\"device.id\":\"d-1\"", json);
            Assert.Contains("\"trace_id\":\"", json);
            Assert.Contains("\"status\":\"Unset\"", json);

            foreach (var hosted in sp.GetServices<IHostedService>())
            {
                await hosted.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            sp.Dispose();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExporterFile_RotatesBySize_WhenMaxFileBytesExceeded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nitro-traces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sp = await StartFileProviderAsync(dir, ("Telemetry:Tracing:MaxFileBytes", "64"));
        try
        {
            ExportSpans(sp.GetRequiredService<TracerProvider>(), 3);

            // 单文件超 64B 即滚动：3 条 span 应分成 >=2 个分段文件
            var files = Directory.GetFiles(dir, "traces-*.jsonl");
            Assert.True(files.Length >= 2, $"expected >=2 segments after size rotation, got {files.Length}");
            Assert.Contains(files, f => f.EndsWith("-0001.jsonl", StringComparison.Ordinal));
        }
        finally
        {
            sp.Dispose();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExporterFile_PurgesExpiredDayFiles_ByRetentionDays()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nitro-traces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var stale = Path.Combine(dir, $"traces-{DateTime.Today.AddDays(-10):yyyyMMdd}.jsonl");
        File.WriteAllText(stale, "{\"name\":\"stale\"}\n");
        var today = Path.Combine(dir, $"traces-{DateTime.Today:yyyyMMdd}.jsonl");
        File.WriteAllText(today, "{\"name\":\"today\"}\n");

        var sp = await StartFileProviderAsync(dir, ("Telemetry:Tracing:MaxRetainedDays", "1"));
        try
        {
            ExportSpans(sp.GetRequiredService<TracerProvider>(), 1);

            // 10 天前的整文件应被按天保留清理；今天的文件保留
            Assert.False(File.Exists(stale), "expired day file should be purged by MaxRetainedDays");
            Assert.True(File.Exists(today), "current day file should be kept");
        }
        finally
        {
            sp.Dispose();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExporterFile_RespectsMaxTotalBytes_Cap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nitro-traces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sp = await StartFileProviderAsync(dir,
            ("Telemetry:Tracing:MaxRetainedDays", "0"),
            ("Telemetry:Tracing:MaxTotalBytes", "300"),
            ("Telemetry:Tracing:MaxFileBytes", "64"));
        try
        {
            ExportSpans(sp.GetRequiredService<TracerProvider>(), 4);

            // 4 个分段总大小超 300B → 目录总量清理应删除最旧分段，且当前正在写的文件保留
            var files = Directory.GetFiles(dir, "traces-*.jsonl");
            Assert.True(files.Length >= 1, "current file must survive total-cap purge");
            Assert.True(files.Length < 4, $"total-cap purge should remove at least one segment, got {files.Length}");
            Assert.DoesNotContain(files, f => f.EndsWith("-0000.jsonl", StringComparison.Ordinal));
        }
        finally
        {
            sp.Dispose();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
