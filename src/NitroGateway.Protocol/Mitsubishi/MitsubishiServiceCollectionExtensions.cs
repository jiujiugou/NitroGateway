namespace NitroGateway.Protocols.Mitsubishi;

/// <summary>三菱 MC 协议注册</summary>
public static class MitsubishiRegistration
{
    public static void Register(ProtocolDriverFactory factory)
    {
        factory.Register("Mitsubishi", (conn, logger) => new MitsubishiDriver(conn, logger));
    }
}
