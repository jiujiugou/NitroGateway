namespace NitroGateway.Transport.HTTP;

/// <summary>HTTP 认证类型</summary>
public enum HttpAuthType
{
    /// <summary>无认证</summary>
    None,

    /// <summary>Bearer Token 认证（Authorization: Bearer xxx）</summary>
    BearerToken
}
