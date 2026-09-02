using System.Text.Json;
using NitroGateway.Protocols.OpcUa;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-073 D1/D4（层4 安全参数契约与校验）：<see cref="OpcUaSecurityParameters.Parse"/>
/// 空值/类型错误/非法枚举/冲突组合/凭据缺失 → IsValid=false + Errors（绝非 500 的裸异常）；
/// 合法值解析出 <see cref="OpcUaSecurityRequirement"/>（None 仅显式、凭据成对）。
/// </summary>
public class OpcUaParametersValidationTests
{
    private static Dictionary<string, object> Params(params (string Key, object Value)[] items)
    {
        var dict = new Dictionary<string, object>();
        foreach (var (key, value) in items)
            dict[key] = value;
        return dict;
    }

    private static OpcUaSecurityRequirement? Require(Dictionary<string, object> p)
    {
        var result = OpcUaSecurityParameters.Parse(p);
        Assert.True(result.IsValid, "预期校验通过，实际错误: " + string.Join("; ", result.Errors));
        return result.Requirement;
    }

    // ── 默认安全优先：无任何档位声明 → 合法、非 NoneExplicit、无凭据 ──

    [Fact]
    public void Parse_EmptyParams_ValidSafeDefaultNoCredentials()
    {
        var r = Require(Params());
        Assert.False(r!.NoneExplicit);
        Assert.Null(r.PolicyUri);
        Assert.Null(r.Mode);
        Assert.False(r.HasCredentials);
    }

    // ── SecurityPolicy 解析 ──

    [Fact]
    public void Parse_PolicyShortName_ResolvesPolicyUri()
    {
        var r = Require(Params(("SecurityPolicy", "Basic256Sha256")));
        Assert.Equal("Basic256Sha256", r!.PolicyName);
        Assert.EndsWith("#Basic256Sha256", r.PolicyUri!);
        Assert.False(r.NoneExplicit);
    }

    [Fact]
    public void Parse_PolicyFullUri_ResolvesShortName()
    {
        const string uri = "http://opcfoundation.org/UA/SecurityPolicy#Basic128Rsa15";
        var r = Require(Params(("SecurityPolicy", uri)));
        Assert.Equal("Basic128Rsa15", r!.PolicyName);
        Assert.Equal(uri, r.PolicyUri);
    }

    [Fact]
    public void Parse_PolicyCaseInsensitive_Resolves()
    {
        var r = Require(Params(("SecurityPolicy", "basic256sha256")));
        Assert.Equal("Basic256Sha256", r!.PolicyName);
    }

    [Fact]
    public void Parse_PolicyNone_NoneExplicitOnly()
    {
        var r = Require(Params(("SecurityPolicy", "None")));
        Assert.True(r!.NoneExplicit);
        Assert.Null(r.PolicyUri);
    }

    [Fact]
    public void Parse_InvalidPolicy_ReturnsValidationError()
    {
        var result = OpcUaSecurityParameters.Parse(Params(("SecurityPolicy", "Frobnicate")));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("SecurityPolicy"));
    }

    [Fact]
    public void Parse_EmptyPolicyValue_ReturnsValidationError()
    {
        var result = OpcUaSecurityParameters.Parse(Params(("SecurityPolicy", "")));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("不能为空"));
    }

    [Fact]
    public void Parse_PolicyWrongType_ReturnsValidationError()
    {
        var result = OpcUaSecurityParameters.Parse(Params(("SecurityPolicy", 42)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("字符串"));
    }

    // ── SecurityMode 解析 ──

    [Fact]
    public void Parse_ModeSignAndEncrypt_Resolves()
    {
        var r = Require(Params(("SecurityMode", "SignAndEncrypt")));
        Assert.Equal(Opc.Ua.MessageSecurityMode.SignAndEncrypt, r!.Mode);
        Assert.False(r.NoneExplicit);
    }

    [Fact]
    public void Parse_ModeNone_NoneExplicitOnly()
    {
        var r = Require(Params(("SecurityMode", "None")));
        Assert.True(r!.NoneExplicit);
        Assert.Null(r.Mode);
    }

    [Fact]
    public void Parse_InvalidMode_ReturnsValidationError()
    {
        var result = OpcUaSecurityParameters.Parse(Params(("SecurityMode", "ShakenNotStirred")));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("SecurityMode"));
    }

    [Fact]
    public void Parse_EmptyModeValue_ReturnsValidationError()
    {
        var result = OpcUaSecurityParameters.Parse(Params(("SecurityMode", "  ")));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("不能为空"));
    }

    // ── 档位冲突：None 仅单独显式 ──

    [Fact]
    public void Parse_PolicyNoneWithModeSign_Conflict()
    {
        var result = OpcUaSecurityParameters.Parse(Params(
            ("SecurityPolicy", "None"),
            ("SecurityMode", "Sign")));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("冲突"));
    }

    [Fact]
    public void Parse_ModeNoneWithSecurePolicy_Conflict()
    {
        var result = OpcUaSecurityParameters.Parse(Params(
            ("SecurityMode", "None"),
            ("SecurityPolicy", "Basic256Sha256")));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("冲突"));
    }

    // ── D4 凭据：UserName/Password 必须成对 ──

    [Fact]
    public void Parse_UserNameWithoutPassword_ReturnsError()
    {
        var result = OpcUaSecurityParameters.Parse(Params(("UserName", "opcuser")));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Password"));
    }

    [Fact]
    public void Parse_PasswordWithoutUserName_ReturnsError()
    {
        var result = OpcUaSecurityParameters.Parse(Params(("Password", "s3cret")));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("UserName"));
    }

    [Fact]
    public void Parse_UserNameEmptyWithPassword_ReturnsError()
    {
        var result = OpcUaSecurityParameters.Parse(Params(
            ("UserName", "  "),
            ("Password", "s3cret")));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("UserName"));
    }

    [Fact]
    public void Parse_UserNameAndPassword_ValidCredentials()
    {
        var r = Require(Params(
            ("UserName", "opcuser"),
            ("Password", "s3cret")));
        Assert.True(r!.HasCredentials);
        Assert.Equal("opcuser", r.UserName);
        Assert.Equal("s3cret", r.Password);
    }

    // ── SQLite JSON 反序列化（JsonElement）兼容 ──

    [Fact]
    public void Parse_JsonElementStrings_Resolves()
    {
        var p = new Dictionary<string, object>
        {
            ["SecurityPolicy"] = JsonSerializer.Deserialize<JsonElement>("\"Basic256Sha256\""),
            ["SecurityMode"] = JsonSerializer.Deserialize<JsonElement>("\"SignAndEncrypt\""),
            ["UserName"] = JsonSerializer.Deserialize<JsonElement>("\"opcuser\""),
            ["Password"] = JsonSerializer.Deserialize<JsonElement>("\"s3cret\"")
        };
        var r = Require(p);
        Assert.Equal("Basic256Sha256", r!.PolicyName);
        Assert.Equal(Opc.Ua.MessageSecurityMode.SignAndEncrypt, r.Mode);
        Assert.Equal("opcuser", r.UserName);
    }

    [Fact]
    public void Parse_JsonElementNonString_TypeError()
    {
        var p = new Dictionary<string, object>
        {
            ["SecurityPolicy"] = JsonSerializer.Deserialize<JsonElement>("123")
        };
        var result = OpcUaSecurityParameters.Parse(p);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("SecurityPolicy"));
    }
}
