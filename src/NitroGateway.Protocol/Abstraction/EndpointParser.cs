namespace NitroGateway.Protocols;

internal static class EndpointParser
{
    /// <summary>拆分 host 与可选端口；返回 (Host, Port)，端口缺失或非法时 Port 为 null（非法则抛异常）</summary>
    public static (string Host, int? Port) Split(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        // 带括号 IPv6：[::1]:502 或 [fe80::1]
        if (endpoint[0] == '[')
        {
            var close = endpoint.IndexOf(']');
            if (close < 0) throw new ArgumentException($"无效的端点: {endpoint}");
            var host = endpoint[1..close];
            var tail = endpoint[(close + 1)..];
            if (tail.Length == 0) return (host, null);
            if (tail[0] != ':' || !int.TryParse(tail[1..], out var port))
                throw new ArgumentException($"无效的端点: {endpoint}");
            return (host, port);
        }

        var idx = endpoint.LastIndexOf(':');
        if (idx < 0) return (endpoint, null);
        var hostPart = endpoint[..idx];
        return int.TryParse(endpoint[(idx + 1)..], out var p)
            ? (hostPart, p)
            : throw new ArgumentException($"无效的端点: {endpoint}");
    }
}
