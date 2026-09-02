namespace NitroGateway.Persistence.Security;

/// <summary>
/// 设备连接凭据的可逆保护（ADR-073 D5）。
/// 用于 OPC UA 用户名/密码在落 SQLite <c>devices.ConnectionParams</c> 前加密、读取后解密，
/// 使明文密码只存在于"前端输入 → API 请求 → 宿主内存 DTO → 建会话"的瞬时链路，不落盘不落日志。
/// 接口形态对齐 Desktop <see cref="NitroGateway.Desktop"/> 的 DpapiProtector，但实现为跨平台
/// AES-256-GCM + 环境变量主密钥（Linux Docker 生产可用，见 ADR-073 Alternatives C）。
/// </summary>
public interface ICredentialProtector
{
    /// <summary>加密明文；密文含算法版本/随机数/认证标签元数据，可安全存入单列文本。</summary>
    string Protect(string plaintext);

    /// <summary>
    /// 解密受保护值。仅处理本实现产出的密文（带版本前缀）；非本实现格式原样返回
    /// （历史/非秘密值不受影响）。密钥缺失/错误时抛出，禁止"明文回写"兜底（ADR-073 载荷墙）。
    /// </summary>
    string Unprotect(string protectedValue);
}
