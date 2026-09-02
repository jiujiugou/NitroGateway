namespace NitroGateway.Webapi.Models;

/// <summary>
/// 服务器对等方证书条目（ADR-073 D8 证书面板）：
/// Subject / Thumbprint（40 位大写十六进制）/ 进入该目录的时间。
/// 信任状态以 pki 目录为唯一权威，本 DTO 只是文件系统 PKI 状态的只读投影。
/// </summary>
public sealed class OpcUaCertificateDto
{
    /// <summary>证书主题（Subject，如 CN=opcua-server）</summary>
    public string Subject { get; init; } = "";

    /// <summary>证书指纹（40 位大写十六进制，无分隔符）</summary>
    public string Thumbprint { get; init; } = "";

    /// <summary>进入该目录的时间（O 格式 UTC：rejected=被拒时间，trusted=信任时间）</summary>
    public string ImportedAt { get; init; } = "";

    /// <summary>证书有效期截止（O 格式 UTC，供运维评估轮换）</summary>
    public string NotAfter { get; init; } = "";
}
