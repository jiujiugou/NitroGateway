using Microsoft.Extensions.Configuration;
using NitroGateway.Webapi.Deployment;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-035 第 0 步：Deployment:Mode 解析——缺省 Gateway（兼容现有部署）、
/// Center 显式声明、未知值启动即抛错（防止中心漏配/拼写错误导致误跑采集）。
/// </summary>
public sealed class DeploymentModeTests
{
    [Fact]
    public void Parse_missing_or_empty_defaults_to_gateway()
    {
        Assert.Equal(DeploymentMode.Gateway, DeploymentModeParser.Parse(Config()));
        Assert.Equal(DeploymentMode.Gateway, DeploymentModeParser.Parse(Config("Deployment:Mode", "  ")));
    }

    [Fact]
    public void Parse_center_is_case_insensitive()
    {
        Assert.Equal(DeploymentMode.Center, DeploymentModeParser.Parse(Config("Deployment:Mode", "Center")));
        Assert.Equal(DeploymentMode.Center, DeploymentModeParser.Parse(Config("Deployment:Mode", "center")));
    }

    [Fact]
    public void Parse_gateway_explicit()
    {
        Assert.Equal(DeploymentMode.Gateway, DeploymentModeParser.Parse(Config("Deployment:Mode", "Gateway")));
    }

    [Fact]
    public void Parse_unknown_value_throws()
    {
        // 未知值启动即失败，防止中心漏配/拼错导致误跑采集（ADR-034 根因）
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeploymentModeParser.Parse(Config("Deployment:Mode", "CENTRE")));
        Assert.Contains("Gateway 或 Center", ex.Message);
    }

    private static IConfiguration Config(string? key = null, string? value = null)
    {
        var builder = new ConfigurationBuilder();
        if (key is not null)
            builder.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
        return builder.Build();
    }
}
