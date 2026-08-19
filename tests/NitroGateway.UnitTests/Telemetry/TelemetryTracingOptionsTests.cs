using Microsoft.Extensions.Configuration;
using NitroGateway.Telemetry;
using Xunit;

namespace NitroGateway.UnitTests.Telemetry;

/// <summary>
/// Telemetry:Tracing 配置解析测试（ADR-056）。
/// 解析逻辑是追踪启停的关键开关：默认启用 OTLP，缺失/非法值回退默认，不因配置错误阻断启动。
/// </summary>
public class TelemetryTracingOptionsTests
{
    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Resolve_NullSection_ReturnsDefaults()
    {
        var o = TelemetryTracingOptions.Resolve(null);

        Assert.True(o.Enabled);
        Assert.Equal(TracingExporterKind.Otlp, o.Exporter);
        Assert.Null(o.Endpoint);
        Assert.Equal(TracingProtocolKind.Grpc, o.Protocol);
        Assert.Equal("nitrogateway", o.ServiceName);
    }

    [Fact]
    public void Resolve_EmptySection_ReturnsDefaults()
    {
        var o = TelemetryTracingOptions.Resolve(Build(new()).GetSection("Telemetry:Tracing"));

        Assert.True(o.Enabled);
        Assert.Equal(TracingExporterKind.Otlp, o.Exporter);
    }

    [Fact]
    public void Resolve_DisablesTracing_WhenEnabledFalse()
    {
        var o = TelemetryTracingOptions.Resolve(Build(new()
        {
            ["Telemetry:Tracing:Enabled"] = "false"
        }).GetSection("Telemetry:Tracing"));

        Assert.False(o.Enabled);
    }

    [Fact]
    public void Resolve_SelectsConsoleExporter()
    {
        var o = TelemetryTracingOptions.Resolve(Build(new()
        {
            ["Telemetry:Tracing:Exporter"] = "Console"
        }).GetSection("Telemetry:Tracing"));

        Assert.Equal(TracingExporterKind.Console, o.Exporter);
    }

    [Fact]
    public void Resolve_NoneKeepsDormant()
    {
        var o = TelemetryTracingOptions.Resolve(Build(new()
        {
            ["Telemetry:Tracing:Exporter"] = "none"
        }).GetSection("Telemetry:Tracing"));

        Assert.Equal(TracingExporterKind.None, o.Exporter);
    }

    [Fact]
    public void Resolve_SelectsFileExporter_WithLogDirectory()
    {
        var o = TelemetryTracingOptions.Resolve(Build(new()
        {
            ["Telemetry:Tracing:Exporter"] = "File",
            ["Telemetry:Tracing:LogDirectory"] = @"C:\tmp\traces"
        }).GetSection("Telemetry:Tracing"));

        Assert.Equal(TracingExporterKind.File, o.Exporter);
        Assert.Equal(@"C:\tmp\traces", o.LogDirectory);
    }

    [Fact]
    public void Resolve_FileWithoutLogDirectory_DefaultsToLogsTraces()
    {
        var o = TelemetryTracingOptions.Resolve(Build(new()
        {
            ["Telemetry:Tracing:Exporter"] = "File"
        }).GetSection("Telemetry:Tracing"));

        Assert.Equal(TracingExporterKind.File, o.Exporter);
        Assert.Equal("logs/traces", o.LogDirectory);
    }

    [Fact]
    public void Resolve_File_ParsesRetentionLimits()
    {
        var o = TelemetryTracingOptions.Resolve(Build(new()
        {
            ["Telemetry:Tracing:Exporter"] = "File",
            ["Telemetry:Tracing:MaxRetainedDays"] = "3",
            ["Telemetry:Tracing:MaxFileBytes"] = "1048576",
            ["Telemetry:Tracing:MaxTotalBytes"] = "104857600"
        }).GetSection("Telemetry:Tracing"));

        Assert.Equal(3, o.MaxRetainedDays);
        Assert.Equal(1048576, o.MaxFileBytes);
        Assert.Equal(104857600, o.MaxTotalBytes);
    }

    [Fact]
    public void Resolve_File_MissingRetentionLimits_UseDefaults()
    {
        var o = TelemetryTracingOptions.Resolve(Build(new()
        {
            ["Telemetry:Tracing:Exporter"] = "File"
        }).GetSection("Telemetry:Tracing"));

        Assert.Equal(7, o.MaxRetainedDays);
        Assert.Equal(10 * 1024 * 1024, o.MaxFileBytes);
        Assert.Equal(512 * 1024 * 1024, o.MaxTotalBytes);
    }

    [Fact]
    public void Resolve_File_InvalidRetentionLimits_FallBackToDefaults()
    {
        var o = TelemetryTracingOptions.Resolve(Build(new()
        {
            ["Telemetry:Tracing:Exporter"] = "File",
            ["Telemetry:Tracing:MaxRetainedDays"] = "abc",
            ["Telemetry:Tracing:MaxFileBytes"] = "",
            ["Telemetry:Tracing:MaxTotalBytes"] = "not-a-number"
        }).GetSection("Telemetry:Tracing"));

        Assert.Equal(7, o.MaxRetainedDays);
        Assert.Equal(10 * 1024 * 1024, o.MaxFileBytes);
        Assert.Equal(512 * 1024 * 1024, o.MaxTotalBytes);
    }

    [Fact]
    public void Resolve_OverridesEndpointProtocolAndServiceName()
    {
        var o = TelemetryTracingOptions.Resolve(Build(new()
        {
            ["Telemetry:Tracing:Endpoint"] = "http://jaeger:4318",
            ["Telemetry:Tracing:Protocol"] = "HttpProtobuf",
            ["Telemetry:Tracing:ServiceName"] = "edge-x"
        }).GetSection("Telemetry:Tracing"));

        Assert.Equal("http://jaeger:4318", o.Endpoint);
        Assert.Equal(TracingProtocolKind.HttpProtobuf, o.Protocol);
        Assert.Equal("edge-x", o.ServiceName);
    }

    [Fact]
    public void Resolve_InvalidValues_FallBackToDefaults()
    {
        var o = TelemetryTracingOptions.Resolve(Build(new()
        {
            ["Telemetry:Tracing:Enabled"] = "not-a-bool",
            ["Telemetry:Tracing:Exporter"] = "Bogus",
            ["Telemetry:Tracing:Protocol"] = "Bogus"
        }).GetSection("Telemetry:Tracing"));

        Assert.True(o.Enabled);
        Assert.Equal(TracingExporterKind.Otlp, o.Exporter);
        Assert.Equal(TracingProtocolKind.Grpc, o.Protocol);
    }
}
