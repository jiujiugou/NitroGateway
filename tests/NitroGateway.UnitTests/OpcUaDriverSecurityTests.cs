using NitroGateway.Protocols.OpcUa;
using Opc.Ua;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-073 D2/D3（层4 连接安全，AC-1/AC-2 单测）：
/// 建连前的端点选择 = 显式档位手工过滤，无任何隐式 None 回退；
/// None 仅 <see cref="OpcUaSecurityParameters.Parse"/> 显式声明（NoneExplicit）才被选中；
/// 未声明档位时默认安全优先（选非 None 且 SecurityLevel 最高者）。
/// 纯逻辑测 <see cref="OpcUaSecurityParameters.SelectEndpoint"/>，无需真实服务器。
/// </summary>
public class OpcUaDriverSecurityTests
{
    private const string NonePolicy = "http://opcfoundation.org/UA/SecurityPolicy#None";
    private const string Basic128Rsa15 = "http://opcfoundation.org/UA/SecurityPolicy#Basic128Rsa15";
    private const string Basic256Sha256 = "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256";

    private static EndpointDescription E(string url, MessageSecurityMode mode, string? policy, byte level) => new()
    {
        EndpointUrl = url,
        SecurityMode = mode,
        SecurityPolicyUri = policy ?? NonePolicy,
        SecurityLevel = level
    };

    private static OpcUaSecurityRequirement Requirement(params (string Key, object Value)[] items)
    {
        var result = OpcUaSecurityParameters.Parse(new Dictionary<string, object>(
            items.Select(i => new KeyValuePair<string, object>(i.Key, i.Value))));
        Assert.True(result.IsValid, "预期参数合法: " + string.Join("; ", result.Errors));
        return result.Requirement!;
    }

    // ── D2 默认安全优先：未声明档位 → 选非 None 中 SecurityLevel 最高者 ──

    [Fact]
    public void SelectEndpoint_SafeDefault_PicksHighestLevelSecure()
    {
        var endpoints = new EndpointDescription[]
        {
            E("opc.tcp://h:4840", MessageSecurityMode.None, NonePolicy, 0),
            E("opc.tcp://h:4840/secureA", MessageSecurityMode.Sign, Basic128Rsa15, 1),
            E("opc.tcp://h:4840/secureB", MessageSecurityMode.SignAndEncrypt, Basic256Sha256, 2)
        };
        var sel = OpcUaSecurityParameters.SelectEndpoint(endpoints, Requirement());
        Assert.Null(sel.Error);
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, sel.Endpoint!.SecurityMode);
        Assert.Equal(Basic256Sha256, sel.Endpoint.SecurityPolicyUri);
    }

    [Fact]
    public void SelectEndpoint_SafeDefault_SameLevel_TieBreakByEndpointUrl()
    {
        var endpoints = new EndpointDescription[]
        {
            E("opc.tcp://h:4840/z", MessageSecurityMode.SignAndEncrypt, Basic256Sha256, 2),
            E("opc.tcp://h:4840/a", MessageSecurityMode.SignAndEncrypt, Basic256Sha256, 2)
        };
        var sel = OpcUaSecurityParameters.SelectEndpoint(endpoints, Requirement());
        Assert.Equal("opc.tcp://h:4840/a", sel.Endpoint!.EndpointUrl);
    }

    // ── D3 无隐式 None：仅 None 且未显式声明 → 明确配置错误（附提示）──

    [Fact]
    public void SelectEndpoint_OnlyNoneNoExplicit_ReturnsErrorWithHint()
    {
        var endpoints = new EndpointDescription[]
        {
            E("opc.tcp://h:4840", MessageSecurityMode.None, NonePolicy, 0)
        };
        var sel = OpcUaSecurityParameters.SelectEndpoint(endpoints, Requirement());
        Assert.Null(sel.Endpoint);
        Assert.NotNull(sel.Error);
        Assert.Contains("None", sel.Error);
        Assert.Contains("SecurityPolicy=None", sel.Error);
    }

    [Fact]
    public void SelectEndpoint_OnlyNoneWithExplicitNone_PicksNone()
    {
        var endpoints = new EndpointDescription[]
        {
            E("opc.tcp://h:4840", MessageSecurityMode.None, NonePolicy, 0)
        };
        var sel = OpcUaSecurityParameters.SelectEndpoint(
            endpoints, Requirement(("SecurityPolicy", "None")));
        Assert.Null(sel.Error);
        Assert.Equal(MessageSecurityMode.None, sel.Endpoint!.SecurityMode);
    }

    [Fact]
    public void SelectEndpoint_ExplicitNoneButNoNoneEndpoint_ReturnsError()
    {
        var endpoints = new EndpointDescription[]
        {
            E("opc.tcp://h:4840/secure", MessageSecurityMode.SignAndEncrypt, Basic256Sha256, 2)
        };
        var sel = OpcUaSecurityParameters.SelectEndpoint(
            endpoints, Requirement(("SecurityMode", "None")));
        Assert.Null(sel.Endpoint);
        Assert.NotNull(sel.Error);
    }

    // ── D2 显式档位手工过滤（SecurityPolicy + SecurityMode）──

    [Fact]
    public void SelectEndpoint_ExplicitPolicyAndMode_FiltersByBoth()
    {
        var endpoints = new EndpointDescription[]
        {
            E("opc.tcp://h:4840/none", MessageSecurityMode.None, NonePolicy, 0),
            E("opc.tcp://h:4840/wrongPolicy", MessageSecurityMode.SignAndEncrypt, Basic128Rsa15, 3),
            E("opc.tcp://h:4840/wrongMode", MessageSecurityMode.Sign, Basic256Sha256, 3),
            E("opc.tcp://h:4840/match", MessageSecurityMode.SignAndEncrypt, Basic256Sha256, 2)
        };
        var sel = OpcUaSecurityParameters.SelectEndpoint(
            endpoints, Requirement(("SecurityPolicy", "Basic256Sha256"), ("SecurityMode", "SignAndEncrypt")));
        Assert.Null(sel.Error);
        Assert.Equal("opc.tcp://h:4840/match", sel.Endpoint!.EndpointUrl);
    }

    [Fact]
    public void SelectEndpoint_ExplicitModeOnly_FiltersByMode()
    {
        var endpoints = new EndpointDescription[]
        {
            E("opc.tcp://h:4840/sign", MessageSecurityMode.Sign, Basic256Sha256, 2),
            E("opc.tcp://h:4840/encrypt", MessageSecurityMode.SignAndEncrypt, Basic256Sha256, 3)
        };
        var sel = OpcUaSecurityParameters.SelectEndpoint(
            endpoints, Requirement(("SecurityMode", "Sign")));
        Assert.Null(sel.Error);
        Assert.Equal(MessageSecurityMode.Sign, sel.Endpoint!.SecurityMode);
    }

    [Fact]
    public void SelectEndpoint_ExplicitPolicyNoMatch_ReturnsErrorWithAvailableList()
    {
        var endpoints = new EndpointDescription[]
        {
            E("opc.tcp://h:4840/none", MessageSecurityMode.None, NonePolicy, 0),
            E("opc.tcp://h:4840/other", MessageSecurityMode.Sign, Basic128Rsa15, 2)
        };
        var sel = OpcUaSecurityParameters.SelectEndpoint(
            endpoints, Requirement(("SecurityPolicy", "Basic256Sha256")));
        Assert.Null(sel.Endpoint);
        Assert.NotNull(sel.Error);
        Assert.Contains("Basic128Rsa15", sel.Error); // 附可用端点清单便于用户改配
    }

    // ── 边界：无端点 / 空列表 ──

    [Fact]
    public void SelectEndpoint_EmptyEndpoints_ReturnsError()
    {
        var sel = OpcUaSecurityParameters.SelectEndpoint(
            Array.Empty<EndpointDescription>(), Requirement(("SecurityPolicy", "None")));
        Assert.Null(sel.Endpoint);
        Assert.NotNull(sel.Error);
    }
}
