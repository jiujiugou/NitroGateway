using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Opc.Ua;
using Opc.Ua.Configuration;

// 与 OpcUaDriver 一致：SDK 1.5 把 CertificateValidator() 等经典同步 API 标记为 Obsolete，
// 但 1.5.378.156 仍稳定可用且为主推兼容路径；统一压制 CS0618，升级 SDK 大版本时再迁移。
#pragma warning disable CS0618

namespace NitroGateway.Protocols.OpcUa;

/// <summary>
/// OPC UA 客户端 <see cref="ApplicationConfiguration"/> 与 <see cref="UserIdentity"/> 构建
/// （纯逻辑，无会话/状态，可单测）。从 <c>OpcUaDriver</c> 抽出的无状态纯函数，行为不变。
/// <para>PKI 目录相对进程工作目录（<c>opcua/pki/...</c>）；信任状态以 pki 目录为唯一权威
/// （ADR-073 D6/D8）：服务端证书只信任 <c>opcua/pki/trusted</c> 白名单内的项，未信任证书被拒绝
/// （<c>BadCertificateUntrusted</c>）并落入 <c>opcua/pki/rejected</c>，由证书管理 API 移入
/// trusted 后重试。</para>
/// </summary>
internal static class OpcUaClientConfigurationFactory
{
    /// <summary>应用证书 SubjectName；首次连接自动生成到 opcua/pki/own 目录存储</summary>
    private const string AppSubjectName = "CN=NitroGateway, DC=localhost";

    /// <summary>
    /// 构建客户端 ApplicationConfiguration（含 PKI 目录、关闭 AutoAccept、按 RequestTimeoutMs 设超时）。
    /// <paramref name="skipServerCertificateValidation"/> = true 表示无加密（None）连接：服务器证书与
    /// None 通道无关，改用放行校验器；加密连接传 false，保持默认严格校验。
    /// </summary>
    public static ApplicationConfiguration BuildConfiguration(int requestTimeout, bool skipServerCertificateValidation = false)
    {
        var hostName = Dns.GetHostName();
        return new ApplicationConfiguration
        {
            ApplicationName = "NitroGateway",
            ApplicationUri = Utils.Format("urn:{0}:NitroGateway", hostName),
            ProductUri = "https://github.com/",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "opcua/pki/own",
                    SubjectName = AppSubjectName
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "opcua/pki/trusted"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "opcua/pki/issuers"
                },
                RejectedCertificateStore = new CertificateStoreIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "opcua/pki/rejected"
                },
                AutoAcceptUntrustedCertificates = false,
                AddAppCertToTrustedStore = true,
                MinimumCertificateKeySize = 2048
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = requestTimeout
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = Math.Max(5000, requestTimeout)
            },
            CertificateValidator = CreateCertificateValidator(skipServerCertificateValidation)
        };
    }

    /// <summary>
    /// 构建对等方（服务器）证书校验器。无加密（None）连接返回放行校验器：证书与通道无关，
    /// 且 SDK 的 <c>BadCertificateUriInvalid</c> 不可经 <c>CertificateValidation</c> 事件抑制
    /// （不在其可抑制列表），放行可避免无加密连接被一张它不使用的证书误拒。
    /// 加密连接返回默认严格校验器（信任库白名单 + ApplicationUri/域校验）。
    /// </summary>
    internal static CertificateValidator CreateCertificateValidator(bool skipServerCertificateValidation)
        => skipServerCertificateValidation
            ? new UnencryptedCertificateValidator()
            : new CertificateValidator();

    /// <summary>
    /// 无加密（None）连接专用校验器：不做任何服务器证书校验，直接放行。
    /// 仅用于显式 None；加密连接仍用默认 <see cref="CertificateValidator"/>。
    /// </summary>
    internal sealed class UnencryptedCertificateValidator : CertificateValidator
    {
        public override void Validate(X509Certificate2Collection certificates, ConfiguredEndpoint endpoint) { }

        public override Task ValidateAsync(
            X509Certificate2Collection certificates, ConfiguredEndpoint endpoint, CancellationToken ct)
            => Task.CompletedTask;

        public override Task ValidateAsync(X509Certificate2Collection certificates, CancellationToken ct)
            => Task.CompletedTask;

        protected override Task InternalValidateAsync(
            X509Certificate2Collection certificates, ConfiguredEndpoint endpoint, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>ADR-073 D4：按解析出的凭据构建用户身份；无凭据 → 匿名。</summary>
    public static UserIdentity BuildUserIdentity(OpcUaSecurityRequirement requirement) =>
        requirement.HasCredentials
            ? new UserIdentity(requirement.UserName!, Encoding.UTF8.GetBytes(requirement.Password!))
            : new UserIdentity();
}
