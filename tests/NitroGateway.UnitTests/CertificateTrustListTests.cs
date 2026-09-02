using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Webapi.Services;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-073 D8 / AC-7（证书管理与信任流程）单测：
/// <see cref="OpcUaCertificateManager"/> 直接操作 pki/rejected → pki/trusted 目录
/// （信任状态以文件系统为唯一权威，不入 SQLite），真实自签 DER 走通
/// “拒绝(列出)→信任(移入 trusted)→撤销(删除)”闭环，并覆盖 404/400/非法指纹/脏文件跳过。
/// </summary>
public class CertificateTrustListTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ngw-pki-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static OpcUaCertificateManager Manager(string root) =>
        new(root, NullLogger<OpcUaCertificateManager>.Instance);

    /// <summary>在指定目录生成自签 DER 证书文件（文件名随意，.der 后缀，同 SDK rejected 落盘形式）。</summary>
    private static (string File, string Thumbprint) WriteDer(string directory, string cn)
    {
        Directory.CreateDirectory(directory);
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".der");
        File.WriteAllBytes(file, cert.Export(X509ContentType.Cert));
        return (file, cert.Thumbprint.ToUpperInvariant());
    }

    // ── 列出：空目录 / rejected 收录 / 脏文件跳过 ──

    [Fact]
    public void List_EmptyDirs_ReturnsEmpty()
    {
        var root = NewRoot();
        try
        {
            var mgr = Manager(root);
            Assert.Empty(mgr.GetRejected());
            Assert.Empty(mgr.GetTrusted());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void GetRejected_ReturnsImportedCertificate()
    {
        var root = NewRoot();
        try
        {
            var rejected = Path.Combine(root, "rejected");
            var (_, tp) = WriteDer(rejected, "OpcUaServer-Rejected");

            var list = Manager(root).GetRejected();
            var dto = Assert.Single(list);
            Assert.Equal(tp, dto.Thumbprint);
            Assert.Contains("OpcUaServer-Rejected", dto.Subject);
            Assert.False(string.IsNullOrEmpty(dto.ImportedAt));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void GetRejected_UnparseableFile_IsSkipped()
    {
        var root = NewRoot();
        try
        {
            var rejected = Path.Combine(root, "rejected");
            Directory.CreateDirectory(rejected);
            File.WriteAllText(Path.Combine(rejected, "junk.der"), "not a certificate");
            (_, _) = WriteDer(rejected, "OpcUaServer-Good");

            var list = Manager(root).GetRejected();
            var dto = Assert.Single(list); // 脏文件被跳过，不使整份列表失败
            Assert.Contains("OpcUaServer-Good", dto.Subject);
        }
        finally { Directory.Delete(root, true); }
    }

    // ── 信任：rejected → trusted ──

    [Fact]
    public void Trust_UnknownThumbprint_ReturnsNotFound()
    {
        var root = NewRoot();
        try
        {
            var result = Manager(root).Trust(new string('A', 40));
            Assert.True(result.IsFailure);
            Assert.Equal("NotFound", result.Error!.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Trust_RejectedCertificate_MovesToTrusted()
    {
        var root = NewRoot();
        try
        {
            var rejected = Path.Combine(root, "rejected");
            var (_, tp) = WriteDer(rejected, "OpcUaServer-ToTrust");
            var mgr = Manager(root);

            var result = mgr.Trust(tp);
            Assert.True(result.IsSuccess, result.Error?.Message);

            Assert.Empty(mgr.GetRejected());
            var dto = Assert.Single(mgr.GetTrusted());
            Assert.Equal(tp, dto.Thumbprint);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Trust_AlreadyTrusted_ReturnsValidation()
    {
        var root = NewRoot();
        try
        {
            var rejected = Path.Combine(root, "rejected");
            var trusted = Path.Combine(root, "trusted");
            Directory.CreateDirectory(trusted);
            var (file, tp) = WriteDer(rejected, "OpcUaServer-Duplicate");
            // 同证书同时存在于 rejected 与 trusted（双份字节一致 → 指纹一致）→ 重复信任应报 Validation
            File.Copy(file, Path.Combine(trusted, Path.GetFileName(file)));

            var result = Manager(root).Trust(tp);
            Assert.True(result.IsFailure);
            Assert.Equal("ValidationError", result.Error!.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Trust_InvalidThumbprint_ReturnsValidation()
    {
        var root = NewRoot();
        try
        {
            var result = Manager(root).Trust("not-a-hex");
            Assert.True(result.IsFailure);
            Assert.Equal("ValidationError", result.Error!.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    // ── 撤销：trusted 移除 ──

    [Fact]
    public void Revoke_TrustedCertificate_RemovesFromTrusted()
    {
        var root = NewRoot();
        try
        {
            var trusted = Path.Combine(root, "trusted");
            var (_, tp) = WriteDer(trusted, "OpcUaServer-Revoked");
            var mgr = Manager(root);
            Assert.Single(mgr.GetTrusted());

            var result = mgr.Revoke(tp);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Empty(mgr.GetTrusted());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Revoke_UnknownThumbprint_ReturnsNotFound()
    {
        var root = NewRoot();
        try
        {
            var result = Manager(root).Revoke(new string('B', 40));
            Assert.True(result.IsFailure);
            Assert.Equal("NotFound", result.Error!.Code);
        }
        finally { Directory.Delete(root, true); }
    }
}
