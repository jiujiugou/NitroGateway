namespace NitroGateway.Desktop.Services;

/// <summary>
/// 周期定时器抽象（轮询节奏是 view 关注点）：让 ViewModel 不依赖 System.Windows.Threading，
/// 保持「VM 与 UI 框架无关」；测试可注入手动触发替身。WPF 实现见 <see cref="DispatcherUiTimer"/>。
/// </summary>
public interface IUiTimer
{
    /// <summary>到达一个周期的时刻（订阅方执行轮询刷新）。</summary>
    event EventHandler? Tick;

    /// <summary>开始计时。</summary>
    void Start();

    /// <summary>停止计时。</summary>
    void Stop();
}
