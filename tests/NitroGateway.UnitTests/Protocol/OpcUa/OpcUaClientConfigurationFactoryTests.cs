using System.Security.Cryptography.X509Certificates;
using NitroGateway.Protocols.OpcUa;
using Opc.Ua;
using Xunit;

// SDK 1.5 的 Validate 为经典同步 API（Obsolete 但可用），与生产代码一致地压制告警。
#pragma warning disable CS0618

namespace NitroGateway.UnitTests.Protocol.OpcUa;

/// <summary>
/// OPC UA 客户端配置构建：证书校验器选择（无加密放行 / 加密严格）的回归锁定。
/// </summary>
public class OpcUaClientConfigurationFactoryTests
{
    [Fact]
    public void CreateCertificateValidator_Skip_ReturnsUnencryptedValidator()
        => Assert.IsType<OpcUaClientConfigurationFactory.UnencryptedCertificateValidator>(
            OpcUaClientConfigurationFactory.CreateCertificateValidator(skipServerCertificateValidation: true));

    [Fact]
    public void CreateCertificateValidator_NoSkip_ReturnsStandardValidator()
    {
        var validator = OpcUaClientConfigurationFactory.CreateCertificateValidator(skipServerCertificateValidation: false);
        Assert.IsNotType<OpcUaClientConfigurationFactory.UnencryptedCertificateValidator>(validator);
        Assert.IsType<CertificateValidator>(validator);
    }

    [Fact]
    public void UnencryptedValidator_SkipsServerCertificateValidation()
    {
        // 空证书集合 + 任意端点：默认校验器会因证书缺失/不匹配抛错；
        // 无加密校验器应直接放行（None 通道本就不使用服务器证书）。
        var validator = OpcUaClientConfigurationFactory.CreateCertificateValidator(skipServerCertificateValidation: true);
        var endpoint = new ConfiguredEndpoint();
        endpoint.Update(new EndpointDescription
        {
            Server = new ApplicationDescription { ApplicationUri = "urn:does:not:match:any:cert" },
            SecurityMode = MessageSecurityMode.None,
            SecurityPolicyUri = SecurityPolicies.None
        });

        var exception = Record.Exception(() => validator.Validate(new X509Certificate2Collection(), endpoint));

        Assert.Null(exception);
    }
}
