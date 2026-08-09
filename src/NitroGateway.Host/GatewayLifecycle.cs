namespace NitroGateway.Host;

/// <summary>网关生命周期状态。协调关闭时的采集→转发 drain 顺序。</summary>
public sealed class GatewayLifecycle
{
    private readonly object _lock = new();
    private bool _draining;
    private bool _stopped;

    /// <summary>采集侧已请求停止：不再启动新的采集轮，但最后一轮可能仍在途。</summary>
    public bool IsDraining { get { lock (_lock) return _draining; } }
    /// <summary>采集侧最后一轮已完成：转发侧可以开始停机排空。</summary>
    public bool IsStopped { get { lock (_lock) return _stopped; } }

    /// <summary>采集引擎 StopAsync 起始调用：标记 draining（ADR-016 P1-1）。</summary>
    public void RequestStop() { lock (_lock) { _draining = true; } }

    /// <summary>采集引擎完成最后一轮后调用：标记 stopped，转发引擎据此启动排空。</summary>
    public void MarkStopped() { lock (_lock) { _stopped = true; } }
}
