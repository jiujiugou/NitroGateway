namespace NitroGateway.Host;

/// <summary>网关生命周期状态。协调关闭时的采集→转发 drain 顺序。</summary>
public sealed class GatewayLifecycle
{
    private readonly object _lock = new();
    private bool _draining;
    private bool _stopped;

    public bool IsDraining { get { lock (_lock) return _draining; } }
    public bool IsStopped { get { lock (_lock) return _stopped; } }

    public void RequestStop() { lock (_lock) { _draining = false; _stopped = false; } }
    public void MarkDraining() { lock (_lock) { _draining = true; } }
    public void MarkStopped() { lock (_lock) { _stopped = true; _draining = false; } }
}
