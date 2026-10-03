namespace NitroGateway.Transport.MQTT;

/// <summary>
/// 重连单实例守卫（ADR-006 P1-3）：保证任意时刻至多一个重连循环在跑。
/// 抽成接缝以便并发系统化验证（Coyote）可注入坏实现做负控。
/// </summary>
internal interface IReconnectGuard
{
    /// <summary>尝试取得重连运行权；已有循环在跑则返回 <c>false</c>。</summary>
    bool TryBegin();

    /// <summary>归还重连运行权（循环退出时必调）。</summary>
    void End();
}

/// <summary>
/// 生产实现：锁 + 标志，结构上保证单实例（新增入口只要走本守卫就绕不过去）。
/// </summary>
internal sealed class ReconnectGuard : IReconnectGuard
{
    private readonly object _lock = new();
    private bool _active;

    public bool TryBegin()
    {
        lock (_lock)
        {
            if (_active) return false;
            _active = true;
            return true;
        }
    }

    public void End()
    {
        lock (_lock) _active = false;
    }
}
