using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace NitroGateway.Persistence.Security;

/// <summary>
/// AES-256-GCM 跨平台凭据保护实现（ADR-073 D5 / Alternatives C）。
/// 主密钥经宿主环境变量注入（配置项 <c>OpcUa:CredentialKey</c>，docker-compose 由
/// <c>OPCUA_CREDENTIAL_KEY</c> 映射），密钥不进 DB/appsettings；无密钥时仅在使用路径 fail-fast
/// （无 OPC UA 凭据的部署无需配置，不影响既有安装升级启动）。
/// 密文格式：<c>ng1:</c> 版本前缀 + Base64(nonce[12] ‖ ciphertext ‖ tag[16])，自含随机数与认证标签。
/// </summary>
public sealed class AesGcmCredentialProtector : ICredentialProtector
{
    /// <summary>密文版本前缀：标识本实现产出的格式，Unprotect 据此判断是否需解密。</summary>
    internal const string Prefix = "ng1:";

    /// <summary>AES-GCM 随机数长度（12 字节，GCM 标准建议值）</summary>
    private const int NonceSize = 12;

    /// <summary>AES-GCM 认证标签长度（16 字节 = 128 bit）</summary>
    private const int TagSize = 16;

    private readonly string? _key;

    /// <param name="configuration">宿主配置；读取 <c>OpcUa:CredentialKey</c>（env <c>OPCUA_CREDENTIAL_KEY</c>）。</param>
    public AesGcmCredentialProtector(IConfiguration configuration)
        => _key = configuration["OpcUa:CredentialKey"]?.Trim();

    /// <param name="key">主密钥（测试/显式注入用，长度须 ≥ 32 字节）。</param>
    public AesGcmCredentialProtector(string key) => _key = key;

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext; // 空串视为"未配置"，不产生密文（调用方按无凭据处理）

        var key = RequireKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[NonceSize + cipher.Length + TagSize];
        nonce.CopyTo(blob, 0);
        cipher.CopyTo(blob, NonceSize);
        tag.CopyTo(blob, NonceSize + cipher.Length);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <inheritdoc />
    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue) || !protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            return protectedValue; // 非本实现格式：原样返回（历史/非秘密值）

        var key = RequireKey();
        var blob = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        if (blob.Length < NonceSize + TagSize)
            throw new InvalidOperationException("OPC UA 凭据密文格式损坏，无法解密");

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(blob.Length - TagSize, TagSize);
        var cipher = blob.AsSpan(NonceSize, blob.Length - NonceSize - TagSize);
        var plain = new byte[cipher.Length];

        using (var aes = new AesGcm(key, TagSize))
            aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>
    /// 派生并校验主密钥：非空且 ≥ 32 字节（与 <c>Security:JwtSecretKey</c> fail-fast 口径一致），
    /// 经 SHA-256 归一为定长 32 字节 AES-256 密钥。密钥缺失/过短在使用路径抛错，禁止明文回写兜底。
    /// </summary>
    private byte[] RequireKey()
    {
        if (string.IsNullOrEmpty(_key))
            throw new InvalidOperationException(
                "OpcUa:CredentialKey 未配置：无法加解密 OPC UA 连接凭据。请设置环境变量 OPCUA_CREDENTIAL_KEY（生产强随机密钥，≥32 字节）后重启。");
        if (Encoding.UTF8.GetByteCount(_key) < 32)
            throw new InvalidOperationException("OpcUa:CredentialKey 长度不足 32 字节，请配置强密钥后重启");
        return SHA256.HashData(Encoding.UTF8.GetBytes(_key));
    }
}
