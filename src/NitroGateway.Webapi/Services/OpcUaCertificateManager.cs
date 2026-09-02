using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NitroGateway.Shared;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Services;

/// <summary>
/// OPC UA 服务器对等方证书信任管理（ADR-073 D8 / P2-1c）。
/// 直接操作文件系统 PKI 目录（<c>opcua/pki/rejected</c> → <c>opcua/pki/trusted</c>），与驱动
/// <c>BuildConfiguration</c> 的目录类证书存储同源 —— pki 目录是信任状态唯一权威，不入 SQLite 设备表
/// （防双写漂移，ADR-073 载荷墙）。读取用 <see cref="X509Certificate2"/> 解析 DER 派生 Subject/指纹，
/// 不重复实现 SDK <c>CertificateTrustList</c>/<c>ICertificateStore</c> 的校验逻辑。
/// </summary>
public interface IOpcUaCertificateManager
{
    /// <summary>读取被拒证书列表（首连未信任而被 SDK 丢入 rejected 的服务器证书）。</summary>
    IReadOnlyList<OpcUaCertificateDto> GetRejected();

    /// <summary>读取已信任服务器证书白名单（trusted 目录内容）。</summary>
    IReadOnlyList<OpcUaCertificateDto> GetTrusted();

    /// <summary>信任指定指纹的服务器证书：从 rejected 移入 trusted。重复信任/未知指纹返回 Failure。</summary>
    OperationResult Trust(string thumbprint);

    /// <summary>撤销信任（运维操作）：把 trusted 白名单中的证书移除，使其回到"未信任 → 下次连接被拒"。</summary>
    OperationResult Revoke(string thumbprint);
}

/// <inheritdoc cref="IOpcUaCertificateManager" />
public sealed class OpcUaCertificateManager : IOpcUaCertificateManager
{
    private static readonly string[] CertExtensions = [".der", ".crt", ".cer"];

    private readonly string _rejectedDir;
    private readonly string _trustedDir;
    private readonly ILogger<OpcUaCertificateManager> _logger;

    /// <param name="pkiRoot">PKI 根目录（<c>opcua/pki</c>）；与驱动 BuildConfiguration 的目录 StorePath 同源。</param>
    /// <param name="logger">日志记录器</param>
    public OpcUaCertificateManager(string pkiRoot, ILogger<OpcUaCertificateManager> logger)
    {
        _rejectedDir = Path.Combine(pkiRoot, "rejected");
        _trustedDir = Path.Combine(pkiRoot, "trusted");
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<OpcUaCertificateDto> GetRejected() => ReadDirectory(_rejectedDir);

    /// <inheritdoc />
    public IReadOnlyList<OpcUaCertificateDto> GetTrusted() => ReadDirectory(_trustedDir);

    /// <inheritdoc />
    public OperationResult Trust(string thumbprint)
    {
        var tp = NormalizeThumbprint(thumbprint, out var normalizeError);
        if (tp is null)
            return OperationResult.Failure(normalizeError!);

        var source = FindByThumbprint(_rejectedDir, tp);
        if (source is null)
            return OperationResult.Failure(OperationalError.NotFound(
                $"rejected 目录中未找到指纹 {tp} 的服务器证书，可能已信任或从未被拒。"));
        if (FindByThumbprint(_trustedDir, tp) is not null)
            return OperationResult.Failure(OperationalError.Validation(
                $"证书 {tp} 已在 trusted 白名单中（重复信任）。"));

        var subject = ReadSubject(source.FullName);
        try
        {
            Directory.CreateDirectory(_trustedDir);
            var destination = Path.Combine(_trustedDir, source.Name);
            File.Move(source.FullName, destination);
            _logger.LogInformation(
                "OPC UA 服务器证书已加入信任白名单: {Thumbprint} Subject={Subject}",
                tp, subject);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "信任 OPC UA 证书失败: {Thumbprint}", tp);
            return OperationResult.Failure(OperationalError.General($"信任证书失败：{ex.Message}"));
        }
    }

    /// <inheritdoc />
    public OperationResult Revoke(string thumbprint)
    {
        var tp = NormalizeThumbprint(thumbprint, out var normalizeError);
        if (tp is null)
            return OperationResult.Failure(normalizeError!);

        var target = FindByThumbprint(_trustedDir, tp);
        if (target is null)
            return OperationResult.Failure(OperationalError.NotFound(
                $"trusted 白名单中未找到指纹 {tp} 的证书（可能已撤销或从未信任）。"));

        try
        {
            File.Delete(target.FullName);
            _logger.LogInformation("OPC UA 证书信任已撤销: {Thumbprint}", tp);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "撤销 OPC UA 证书信任失败: {Thumbprint}", tp);
            return OperationResult.Failure(OperationalError.General($"撤销证书信任失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 列出目录中全部证书文件（rejected/trusted 均为 DER 目录存储）。不可解析文件跳过并记日志，
    /// 保证单条脏文件不致整份列表失败。
    /// </summary>
    private IReadOnlyList<OpcUaCertificateDto> ReadDirectory(string directory)
    {
        var result = new List<OpcUaCertificateDto>();
        if (!Directory.Exists(directory))
            return result;

        foreach (var file in Directory.EnumerateFiles(directory)
                     .Where(f => CertExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                     .OrderByDescending(f => File.GetLastWriteTimeUtc(f)))
        {
            try
            {
                var dto = Load(file);
                if (dto is not null)
                    result.Add(dto);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "跳过无法解析的证书文件: {File}", file);
            }
        }
        return result;
    }

    private static OpcUaCertificateDto? Load(string file)
    {
        using var cert = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(file));
        var thumbprint = NormalizeHex(cert.Thumbprint);
        if (string.IsNullOrEmpty(thumbprint))
            return null;
        return new OpcUaCertificateDto
        {
            Thumbprint = thumbprint,
            Subject = cert.Subject,
            ImportedAt = File.GetLastWriteTimeUtc(file).ToString("O", CultureInfo.InvariantCulture),
            NotAfter = cert.NotAfter == default ? "" : cert.NotAfter.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        };
    }

    private FileInfo? FindByThumbprint(string directory, string thumbprint)
    {
        if (!Directory.Exists(directory))
            return null;
        foreach (var file in Directory.EnumerateFiles(directory)
                     .Where(f => CertExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                using var cert = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(file));
                if (string.Equals(NormalizeHex(cert.Thumbprint), thumbprint, StringComparison.OrdinalIgnoreCase))
                    return new FileInfo(file);
            }
            catch
            {
                // 脏/部分写入文件跳过，按内容指纹匹配而非文件名
            }
        }
        return null;
    }

    private static string ReadSubject(string file)
    {
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(file));
            return cert.Subject;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>归一化用户输入指纹：去分隔符并转大写十六进制；非法 → Validation 错误。</summary>
    private static string? NormalizeThumbprint(string? thumbprint, out OperationalError? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            error = OperationalError.Validation("thumbprint 不能为空。");
            return null;
        }
        var hex = NormalizeHex(thumbprint);
        if (string.IsNullOrEmpty(hex) || hex.Length != 40)
        {
            error = OperationalError.Validation($"thumbprint 必须为 40 位十六进制指纹，实际为 '{thumbprint}'。");
            return null;
        }
        return hex;
    }

    /// <summary>把可能带 ':' / ' ' / '-' 分隔的指纹规整为大写连续十六进制；非十六进制字符返回空。</summary>
    private static string NormalizeHex(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (Uri.IsHexDigit(ch))
                builder.Append(ch);
        }
        return builder.ToString().ToUpperInvariant();
    }
}
