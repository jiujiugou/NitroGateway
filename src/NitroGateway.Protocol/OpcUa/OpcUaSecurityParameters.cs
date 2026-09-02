using System.Reflection;
using System.Linq;
using System.Text.Json;
using NitroGateway.Domain.Devices;
using Opc.Ua;

namespace NitroGateway.Protocols.OpcUa;

/// <summary>
/// ADR-073 层4 OPC UA 安全参数解析与端点选择（纯逻辑，无 SDK 会话，可单测）。
/// </summary>
/// <remarks>
/// <para><b>读参口径（D1）：</b>从 <see cref="DeviceConnection.Parameters"/> 读
/// <c>SecurityPolicy</c>/<c>SecurityMode</c>/<c>UserName</c>/<c>Password</c>。值可能为内存 string
/// 或 SQLite JSON 反序列化的 <see cref="JsonElement"/>，统一宽容解析。宿主在组装 DeviceConnection
/// 供驱动连接前已完成解密（D5 解密边界），本类型只消费内存明文，Protocol 模块不引入加解密依赖。</para>
/// <para><b>端点选择（D2/D3）：</b>建连前 GetEndpoints 拉取端点后，按
/// <see cref="EndpointDescription.SecurityPolicyUri"/>/<see cref="EndpointDescription.SecurityMode"/>/
/// <see cref="EndpointDescription.SecurityLevel"/> 手工过滤 —— SDK <c>SelectEndpoint</c> 无策略过滤
/// 重载（ADR-073 Context 更正）。None 仅显式声明 <c>SecurityPolicy=None</c> 或 <c>SecurityMode=None</c>
/// 才允许；未声明任何档位时安全优先（选非 None 中 SecurityLevel 最高者）；仅提供 None 端点且未显式
/// 声明 None → 配置错误。</para>
/// </remarks>
internal static class OpcUaSecurityParameters
{
    public const string PolicyKey = "SecurityPolicy";
    public const string ModeKey = "SecurityMode";
    public const string UserNameKey = "UserName";
    public const string PasswordKey = "Password";

    private const string NoneName = "None";

    /// <summary>策略短名（常量名，如 Basic256Sha256）→ 策略 URI（反射 SecurityPolicies 常量）。</summary>
    private static readonly IReadOnlyDictionary<string, string> PolicyUriByName = BuildPolicyMap();

    private static readonly IReadOnlyDictionary<string, string> PolicyNameByUri =
        PolicyUriByName.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    private static Dictionary<string, string> BuildPolicyMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in typeof(SecurityPolicies).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(string))
                continue;
            if (field.Name is "BaseUri") // URI 前缀常量，非真实安全策略
                continue;
            if (field.GetRawConstantValue() is string uri && !string.IsNullOrEmpty(uri))
                map[field.Name] = uri;
        }
        return map;
    }

    /// <summary>解析并校验安全参数。非法（空值/类型错误/非法枚举/冲突组合）→ IsValid=false + Errors。</summary>
    public static OpcUaSecurityParseResult Parse(IReadOnlyDictionary<string, object> parameters)
    {
        var errors = new List<string>();

        // ---- SecurityPolicy ----
        string? policyName = null;   // 显式声明的策略短名（None 时为 NoneName）
        string? policyUri = null;    // 显式声明的非 None 策略 URI
        var policyNone = false;
        if (TryReadString(parameters, PolicyKey, out var policyRaw, out var policyTypeError))
        {
            if (policyTypeError is not null)
            {
                errors.Add(policyTypeError);
            }
            else if (string.IsNullOrWhiteSpace(policyRaw))
            {
                errors.Add($"参数 {PolicyKey} 不能为空（存在该键但值为空串）。可用值：None、Basic128Rsa15、Basic256、Basic256Sha256 等，或移除该键使用默认安全优先）。");
            }
            else if (!TryResolvePolicy(policyRaw!, out policyName, out policyUri, out policyNone, out var policyError))
            {
                errors.Add(policyError!);
            }
        }

        // ---- SecurityMode ----
        MessageSecurityMode? mode = null;
        var modeNone = false;
        if (TryReadString(parameters, ModeKey, out var modeRaw, out var modeTypeError))
        {
            if (modeTypeError is not null)
            {
                errors.Add(modeTypeError);
            }
            else if (string.IsNullOrWhiteSpace(modeRaw))
            {
                errors.Add($"参数 {ModeKey} 不能为空（存在该键但值为空串）。可用值：None、Sign、SignAndEncrypt，或移除该键使用默认安全优先）。");
            }
            else
            {
                switch (modeRaw!.Trim())
                {
                    case "None":
                        mode = MessageSecurityMode.None;
                        modeNone = true;
                        break;
                    case "Sign":
                        mode = MessageSecurityMode.Sign;
                        break;
                    case "SignAndEncrypt":
                        mode = MessageSecurityMode.SignAndEncrypt;
                        break;
                    default:
                        errors.Add($"参数 {ModeKey} 取值非法：'{modeRaw}'。可用值：None、Sign、SignAndEncrypt。");
                        break;
                }
            }
        }

        // ---- 档位冲突校验 ----
        if (policyNone && mode is { } declaredMode && declaredMode != MessageSecurityMode.None)
            errors.Add($"{PolicyKey}=None 与 {ModeKey}={declaredMode} 冲突：None 表示无加密，不能与加密模式组合。");
        if (!policyNone && policyUri is not null && modeNone)
            errors.Add($"{ModeKey}=None 与 {PolicyKey}={policyName} 冲突：请去掉其一（None 仅单独声明）。");

        // ---- 凭据（D4）----
        var hasUserName = TryReadString(parameters, UserNameKey, out var userName, out var userNameTypeError)
                          && !string.IsNullOrWhiteSpace(userName);
        var hasPassword = TryReadString(parameters, PasswordKey, out var password, out var passwordTypeError)
                          && !string.IsNullOrWhiteSpace(password);
        if (userNameTypeError is not null) errors.Add(userNameTypeError);
        if (passwordTypeError is not null) errors.Add(passwordTypeError);
        if (hasUserName && !hasPassword)
            errors.Add($"已配置 {UserNameKey} 但 {PasswordKey} 缺失/为空：用户名密码认证需同时配置密码；如确为匿名请移除 {UserNameKey}。");
        if (hasPassword && !hasUserName)
            errors.Add($"已配置 {PasswordKey} 但 {UserNameKey} 缺失/为空：用户名密码认证需同时配置用户名。");

        if (errors.Count > 0)
            return new OpcUaSecurityParseResult { Errors = errors };

        var requirement = new OpcUaSecurityRequirement
        {
            PolicyName = policyName,
            PolicyUri = policyNone ? null : policyUri,
            Mode = mode is { } m && !modeNone ? m : null,
            NoneExplicit = policyNone || modeNone,
            HasCredentials = hasUserName && hasPassword,
            UserName = hasUserName ? userName!.Trim() : null,
            Password = hasPassword ? password! : null
        };
        return new OpcUaSecurityParseResult { Requirement = requirement, IsValid = true };
    }

    /// <summary>
    /// 按显式档位从发现到的端点中选择一个（D2/D3）。返回选中的端点或错误消息（含可用端点清单）。
    /// </summary>
    public static OpcUaEndpointSelection SelectEndpoint(
        IReadOnlyList<EndpointDescription> endpoints, OpcUaSecurityRequirement requirement)
    {
        if (endpoints is null || endpoints.Count == 0)
            return new OpcUaEndpointSelection { Error = "OPC UA 端点发现未返回任何端点。" };

        var noneEndpoints = endpoints.Where(IsNoneEndpoint).ToList();
        var secureEndpoints = endpoints.Where(e => !IsNoneEndpoint(e)).ToList();

        IReadOnlyList<EndpointDescription> candidates;
        if (requirement.NoneExplicit)
        {
            // D3：仅显式声明 None 才允许 None 端点
            candidates = noneEndpoints;
            if (candidates.Count == 0)
            {
                return new OpcUaEndpointSelection
                {
                    Error = "已显式配置 SecurityPolicy=None/SecurityMode=None，但服务器未提供无加密（None）端点。"
                            + Environment.NewLine + DescribeList("可用端点", endpoints)
                };
            }
        }
        else if (requirement.PolicyUri is not null || requirement.Mode is not null)
        {
            // D2：按显式策略 + 模式手工过滤
            var wantedMode = requirement.Mode;
            var wantedUri = requirement.PolicyUri;
            candidates = endpoints
                .Where(e => wantedMode is null || e.SecurityMode == wantedMode)
                .Where(e => wantedUri is null || string.Equals(e.SecurityPolicyUri, wantedUri, StringComparison.Ordinal))
                .ToList();
            if (candidates.Count == 0)
            {
                var wanted = (requirement.PolicyName is not null ? $"策略={requirement.PolicyName}" : "")
                             + (requirement.Mode is not null ? $"模式={requirement.Mode}" : "");
                return new OpcUaEndpointSelection
                {
                    Error = $"未找到匹配的安全端点（{wanted.TrimStart('，')}）。"
                            + Environment.NewLine + DescribeList("可用端点", endpoints)
                };
            }
        }
        else
        {
            // D2：未声明任何档位 → 安全优先：选非 None 中 SecurityLevel 最高者
            candidates = secureEndpoints;
            if (candidates.Count == 0)
            {
                return new OpcUaEndpointSelection
                {
                    Error = "目标服务器仅提供无加密（None）端点；未声明安全档位时默认安全优先，不允许自动回退 None。"
                            + "请显式配置 SecurityPolicy=None（或 SecurityMode=None）以连接该无加密端点。"
                            + Environment.NewLine + DescribeList("可用端点", endpoints)
                };
            }
        }

        // 同档位多端点取 SecurityLevel 最高者（None 端点级别通常为 0）
        var selected = candidates.OrderByDescending(e => e.SecurityLevel).ThenBy(e => e.EndpointUrl).First();
        return new OpcUaEndpointSelection { Endpoint = selected };
    }

    /// <summary>策略短名/完整 URI → 显示短名（用于错误提示/日志）。未知 URI 原样返回。</summary>
    public static string PolicyDisplayName(string? policyUri)
    {
        if (string.IsNullOrEmpty(policyUri))
            return NoneName;
        return PolicyNameByUri.TryGetValue(policyUri, out var name) ? name : policyUri;
    }

    private static bool IsNoneEndpoint(EndpointDescription e) => e.SecurityMode == MessageSecurityMode.None;

    private static string DescribeList(string title, IEnumerable<EndpointDescription> endpoints)
    {
        var lines = endpoints.Select(e =>
            $"  {e.EndpointUrl} 策略={PolicyDisplayName(e.SecurityPolicyUri)} 模式={e.SecurityMode} 安全级别={e.SecurityLevel}");
        return title + "：" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static bool TryResolvePolicy(
        string raw, out string? name, out string? uri, out bool isNone, out string? error)
    {
        name = null;
        uri = null;
        isNone = false;
        error = null;
        var trimmed = raw.Trim();

        // 短名（大小写不敏感）
        foreach (var kv in PolicyUriByName)
        {
            if (string.Equals(kv.Key, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                name = kv.Key;
                uri = kv.Value;
                isNone = string.Equals(kv.Key, NoneName, StringComparison.OrdinalIgnoreCase);
                return true;
            }
        }

        // 完整策略 URI（如 http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256）
        if (PolicyNameByUri.TryGetValue(trimmed, out var canonical))
        {
            name = canonical;
            uri = trimmed;
            isNone = string.Equals(canonical, NoneName, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        error = $"参数 {PolicyKey} 取值非法：'{raw}'。支持策略：{string.Join("、", PolicyUriByName.Keys.Where(k => !string.Equals(k, NoneName, StringComparison.OrdinalIgnoreCase)))}；"
                + $"{NoneName} 表示无加密（None）。";
        return false;
    }

    /// <summary>
    /// 读取字符串参数。键缺失/值为 null → false（视为未配置）；值为 string 或 JsonElement(string) → true。
    /// 存在但类型错误 → typeError 非空。
    /// </summary>
    private static bool TryReadString(
        IReadOnlyDictionary<string, object> parameters, string key, out string? value, out string? typeError)
    {
        value = null;
        typeError = null;
        if (!parameters.TryGetValue(key, out var raw) || raw is null)
            return false;
        switch (raw)
        {
            case string s:
                value = s;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } json:
                value = json.GetString();
                return true;
            case JsonElement { ValueKind: JsonValueKind.Null }:
                return false;
            case JsonElement json:
                typeError = $"参数 {key} 必须是字符串，实际为 JSON 类型 {json.ValueKind}。";
                return true;
            default:
                typeError = $"参数 {key} 必须是字符串，实际为 {raw.GetType().Name}。";
                return true;
        }
    }
}

/// <summary>安全参数解析结果。IsValid=false 时 Errors 含全部校验错误；否则 Requirement 非空。</summary>
internal sealed class OpcUaSecurityParseResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public OpcUaSecurityRequirement? Requirement { get; init; }
}

/// <summary>解析后的安全档位要求（ADR-073 D1/D3/D4）。</summary>
internal sealed class OpcUaSecurityRequirement
{
    /// <summary>显式策略短名（未声明为 null；None 时不参与过滤）。</summary>
    public string? PolicyName { get; init; }

    /// <summary>显式非 None 策略 URI（未声明/None 为 null）。</summary>
    public string? PolicyUri { get; init; }

    /// <summary>显式非 None 安全模式（未声明/None 为 null）。</summary>
    public MessageSecurityMode? Mode { get; init; }

    /// <summary>是否显式声明 None（SecurityPolicy=None 或 SecurityMode=None）。</summary>
    public bool NoneExplicit { get; init; }

    /// <summary>是否有用户名密码凭据（两者均非空）。</summary>
    public bool HasCredentials { get; init; }

    public string? UserName { get; init; }
    public string? Password { get; init; }
}

/// <summary>端点选择结果：命中或错误消息（含可用端点清单）。</summary>
internal sealed record OpcUaEndpointSelection
{
    public EndpointDescription? Endpoint { get; init; }
    public string? Error { get; init; }
}
